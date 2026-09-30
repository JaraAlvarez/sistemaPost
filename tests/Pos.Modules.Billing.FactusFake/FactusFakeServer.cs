using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pos.Modules.Billing.FactusFake;

/// <summary>
/// Factus API v2 SIMULADO (https://developers.factus.com.co). Se usa de dos formas:
/// <list type="bullet">
/// <item><see cref="CreateHandler"/>: manejador en memoria para <c>ConfigurePrimaryHttpMessageHandler</c> (rápido, sin puertos).</item>
/// <item><see cref="StartAsync"/>: servidor Kestrel real en 127.0.0.1 con puerto libre (pruebas de integración de punta a punta).</item>
/// </list>
/// Comportamiento documentado que imita: token de 1 h con refresh token, idempotencia por <c>reference_code</c>, rechazo DIAN
/// ("Regla: X, Rechazo: …") que bloquea los envíos siguientes con 409 "Se encontró una factura pendiente por enviar a la DIAN"
/// hasta eliminarlo, 422 de validación estilo Laravel, 80 peticiones por minuto con 429 + Retry-After y encabezados
/// X-RateLimit-*. Los formatos no publicados (consulta, listado) siguen el estilo v1/Laravel y están marcados como supuestos
/// en el cliente.
/// </summary>
public sealed class FactusFakeServer : IAsyncDisposable
{
    public const string DefaultClientId = "fake-client-id";
    public const string DefaultClientSecret = "fake-client-secret";
    public const string DefaultUsername = "integracion@factus.test";
    public const string DefaultPassword = "fake-password";

    /// <summary>URL base del manejador en memoria (no resuelve en red).</summary>
    public static readonly Uri InMemoryBaseUrl = new("https://api-sandbox.factus.test/");

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _accessTokens = [];
    private readonly HashSet<string> _refreshTokens = [];
    private readonly Dictionary<(FakeDocumentKind, string), FakeDocument> _documents = [];
    private readonly Queue<FakeFault> _faults = new();
    private readonly Queue<(string Rule, string Message)> _rejections = new();
    private readonly List<(Func<JsonElement, bool> When, string Rule, string Message)> _rejectionRules = [];
    private readonly Queue<DateTimeOffset> _window = new();
    private readonly List<FakeRecordedRequest> _requests = [];
    private int _pendingNext;
    private int _passwordGrants;
    private int _refreshGrants;
    private WebApplication? _app;

    public FactusFakeServer(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        NumberingRanges =
        [
            new FakeNumberingRange { Id = 8, Document = "21", Prefix = "SETP", From = 990000000, To = 995000000, Current = 990000001 },
            new FakeNumberingRange { Id = 9, Document = "22", Prefix = "NC", TechnicalKey = null, ResolutionNumber = "" },
            new FakeNumberingRange { Id = 10, Document = "24", Prefix = "DS", From = 1, To = 5000, TechnicalKey = null },
            new FakeNumberingRange { Id = 13, Document = "25", Prefix = "NA", TechnicalKey = null, ResolutionNumber = "" },
        ];
    }

    public string ClientId { get; set; } = DefaultClientId;
    public string ClientSecret { get; set; } = DefaultClientSecret;
    public string Username { get; set; } = DefaultUsername;
    public string Password { get; set; } = DefaultPassword;

    /// <summary>Duración de los tokens emitidos (<c>expires_in</c>).</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Si es verdadero, todo token se rechaza con 401 (credenciales revocadas en Factus).</summary>
    public bool RejectAllTokens { get; set; }

    public DuplicateReferenceBehavior DuplicateBehavior { get; set; } = DuplicateReferenceBehavior.ReturnExisting;

    /// <summary>Código HTTP con que responde un rechazo DIAN (supuesto: 201 con <c>is_validated</c> false; puede ser 422).</summary>
    public int RejectionStatusCode { get; set; } = 201;

    /// <summary>Peticiones por minuto antes de responder 429 (Factus: 80 por usuario). 0 = sin límite.</summary>
    public int RateLimitPerMinute { get; set; } = 80;

    /// <summary>Retardo de cada respuesta (tiempo real, no el reloj simulado).</summary>
    public TimeSpan Latency { get; set; }

    public List<FakeNumberingRange> NumberingRanges { get; }

    /// <summary>URL base activa: la de Kestrel si se inició, si no la del manejador en memoria.</summary>
    public Uri BaseUrl { get; private set; } = InMemoryBaseUrl;

    public int PasswordGrants { get { lock (_gate) return _passwordGrants; } }
    public int RefreshGrants { get { lock (_gate) return _refreshGrants; } }

    public IReadOnlyList<FakeRecordedRequest> Requests { get { lock (_gate) return _requests.ToList(); } }

    public IReadOnlyList<FakeDocument> Documents { get { lock (_gate) return _documents.Values.ToList(); } }

    // ---- Configuración de escenarios ----

    public void EnqueueFault(FakeFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        lock (_gate)
            _faults.Enqueue(fault);
    }

    /// <summary>El siguiente documento creado queda rechazado por la DIAN con esa regla.</summary>
    public void RejectNext(string rule = "FAK24", string message = "No está informado el DV del NIT")
    {
        lock (_gate)
            _rejections.Enqueue((rule, message));
    }

    /// <summary>Rechaza todo documento cuyo JSON cumpla la condición.</summary>
    public void RejectWhen(Func<JsonElement, bool> when, string rule, string message)
    {
        lock (_gate)
            _rejectionRules.Add((when, rule, message));
    }

    /// <summary>Los siguientes <paramref name="count"/> documentos quedan pendientes (la DIAN "tarda").</summary>
    public void DelayValidationNext(int count = 1)
    {
        lock (_gate)
            _pendingNext += count;
    }

    /// <summary>Invalida todos los access tokens emitidos (el cliente recibirá 401 y deberá renovar).</summary>
    public void ExpireAllTokens()
    {
        lock (_gate)
            _accessTokens.Clear();
    }

    public void RevokeRefreshTokens()
    {
        lock (_gate)
            _refreshTokens.Clear();
    }

    public FakeDocument? Find(FakeDocumentKind kind, string referenceCode)
    {
        lock (_gate)
            return _documents.GetValueOrDefault((kind, referenceCode));
    }

    // ---- Transportes ----

    /// <summary>Manejador en memoria. Usar con <see cref="InMemoryBaseUrl"/> (o cualquier URL: se ignora el host).</summary>
    public HttpMessageHandler CreateHandler() => new FactusFakeHandler(this);

    /// <summary>Inicia Kestrel en 127.0.0.1 con un puerto libre y devuelve la URL base.</summary>
    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
            return BaseUrl;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            var response = await HandleAsync(
                context.Request.Method,
                context.Request.Path.Value!.TrimStart('/') + context.Request.QueryString.Value,
                context.Request.Headers.Authorization.ToString(),
                context.Request.ContentType,
                body,
                context.RequestAborted);
            if (response.Drop)
            {
                context.Abort();
                return;
            }
            context.Response.StatusCode = response.Status;
            foreach (var (name, value) in response.Headers)
                context.Response.Headers[name] = value;
            if (response.Json is not null)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(response.Json, context.RequestAborted);
            }
        });

        await app.StartAsync(cancellationToken);
        _app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseUrl = new Uri(address.TrimEnd('/') + "/");
        return BaseUrl;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    // ---- Núcleo ----

    internal async Task<FakeResponse> HandleAsync(
        string method, string pathAndQuery, string? authorization, string? contentType, string body, CancellationToken cancellationToken)
    {
        if (Latency > TimeSpan.Zero)
            await Task.Delay(Latency, cancellationToken);

        var bearer = authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
        var (path, query) = SplitQuery(pathAndQuery);
        var isOAuth = method == "POST" && path == "oauth/token";

        lock (_gate)
        {
            _requests.Add(new FakeRecordedRequest(method, pathAndQuery, isOAuth ? "(formulario oculto)" : body, bearer is not null));

            if (_faults.TryPeek(out var fault) && (!isOAuth || fault.AppliesToOAuth))
            {
                _faults.Dequeue();
                if (fault.Drop)
                    return FakeResponse.Dropped;
                var headers = new Dictionary<string, string>();
                if (fault.RetryAfter is { } retry)
                    headers["Retry-After"] = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return new FakeResponse(fault.StatusCode, Message(fault.StatusCode == 429 ? "Too Many Attempts." : "Error del servidor simulado."), headers);
            }

            if (isOAuth)
                return Token(contentType, body);

            if (bearer is null || RejectAllTokens || !_accessTokens.TryGetValue(bearer, out var expires) || _time.GetUtcNow() >= expires)
                return new FakeResponse(401, Message("Unauthenticated."));

            if (Throttle() is { } throttled)
                return throttled;

            var response = Route(method, path, query, body);
            response.Headers["X-RateLimit-Limit"] = RateLimitPerMinute.ToString(CultureInfo.InvariantCulture);
            response.Headers["X-RateLimit-Remaining"] = Math.Max(0, RateLimitPerMinute - _window.Count).ToString(CultureInfo.InvariantCulture);
            return response;
        }
    }

    private FakeResponse? Throttle()
    {
        if (RateLimitPerMinute <= 0)
            return null;
        var now = _time.GetUtcNow();
        while (_window.TryPeek(out var first) && now - first >= TimeSpan.FromMinutes(1))
            _window.Dequeue();
        if (_window.Count >= RateLimitPerMinute)
        {
            var wait = TimeSpan.FromMinutes(1) - (now - _window.Peek());
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return new FakeResponse(429, Message("Too Many Attempts."), new Dictionary<string, string>
            {
                ["Retry-After"] = seconds,
                ["X-RateLimit-Limit"] = RateLimitPerMinute.ToString(CultureInfo.InvariantCulture),
                ["X-RateLimit-Remaining"] = "0",
                ["X-RateLimit-Reset"] = seconds,
            });
        }
        _window.Enqueue(now);
        return null;
    }

    private FakeResponse Token(string? contentType, string body)
    {
        if (contentType is null || !contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return new FakeResponse(400, """{"error":"unsupported_grant_type","message":"Se esperaba form-data."}""");

        var form = ParseForm(body);
        if (form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret)
            return new FakeResponse(401, """{"error":"invalid_client","error_description":"Client authentication failed","message":"Client authentication failed"}""");

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "password":
                if (form.GetValueOrDefault("username") != Username || form.GetValueOrDefault("password") != Password)
                    return new FakeResponse(400, """{"error":"invalid_grant","error_description":"The user credentials were incorrect.","message":"The user credentials were incorrect."}""");
                _passwordGrants++;
                return IssueToken();
            case "refresh_token":
                if (form.GetValueOrDefault("refresh_token") is not { } refresh || !_refreshTokens.Remove(refresh))
                    return new FakeResponse(401, """{"error":"invalid_request","error_description":"The refresh token is invalid.","message":"The refresh token is invalid."}""");
                _refreshGrants++;
                return IssueToken();
            default:
                return new FakeResponse(400, """{"error":"unsupported_grant_type"}""");
        }
    }

    private FakeResponse IssueToken()
    {
        var access = "fake-access-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var refresh = "fake-refresh-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _accessTokens[access] = _time.GetUtcNow() + TokenLifetime;
        _refreshTokens.Add(refresh);
        var json = new JsonObject
        {
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)TokenLifetime.TotalSeconds,
            ["access_token"] = access,
            ["refresh_token"] = refresh,
        };
        return new FakeResponse(200, json.ToJsonString());
    }

    private FakeResponse Route(string method, string path, Dictionary<string, string> query, string body)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] != "v2")
            return NotFound();

        if (segments[1] == "numbering-ranges" && method == "GET")
            return segments.Length == 2 ? Ranges(query) : NotFound();

        if (KindOf(segments[1]) is not { } kind)
            return NotFound();

        return (method, segments.Length) switch
        {
            ("POST", 3) when segments[2] == "validate" => Create(kind, body),
            ("GET", 2) => List(kind, query),
            ("GET", 3) => Show(kind, segments[2]),
            ("GET", 4) when segments[3] is "download-pdf" or "download-xml" => Download(kind, segments[2], segments[3] == "download-pdf"),
            ("DELETE", 5) when kind == FakeDocumentKind.Bill && segments[2] == "destroy" && segments[3] == "reference" => Delete(kind, segments[4]),
            ("DELETE", 4) when kind != FakeDocumentKind.Bill && segments[2] == "reference" => Delete(kind, segments[3]),
            _ => NotFound(),
        };
    }

    // ---- Emisión ----

    private FakeResponse Create(FakeDocumentKind kind, string body)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new FakeResponse(400, Message("JSON mal formado."));
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            var errors = Validate(kind, root);
            if (errors.Count > 0)
                return Unprocessable(errors);

            var reference = root.GetProperty("reference_code").GetString()!;
            if (_documents.TryGetValue((kind, reference), out var existing))
                return Resubmit(existing);

            if (_documents.Values.FirstOrDefault(d => d.Kind == kind && d.State == FakeDocumentState.Rejected) is not null)
                return new FakeResponse(409, Message(PendingMessage(kind)));

            var range = SelectRange(kind, root, out var rangeError);
            if (range is null)
                return Unprocessable(new() { ["numbering_range_id"] = [rangeError!] });

            var number = range.Prefix + range.Current.ToString(CultureInfo.InvariantCulture);
            range.Current++;
            var now = _time.GetUtcNow();
            var document = new FakeDocument
            {
                Kind = kind,
                ReferenceCode = reference,
                Number = number,
                Cufe = Convert.ToHexStringLower(SHA384.HashData(Encoding.UTF8.GetBytes($"{kind}|{number}|{reference}"))),
                NumberingRangeId = range.Id,
                RequestJson = body,
                Totals = Totals(root),
                CreatedAt = now,
            };

            var rejection = _rejections.Count > 0 ? _rejections.Dequeue() : _rejectionRules.Where(r => r.When(root)).Select(r => (r.Rule, r.Message)).FirstOrDefault();
            if (rejection.Rule is not null)
            {
                document.State = FakeDocumentState.Rejected;
                document.Errors[rejection.Rule] = $"Regla: {rejection.Rule}, Rechazo: {rejection.Message}";
            }
            else if (_pendingNext > 0)
            {
                _pendingNext--;
                document.State = FakeDocumentState.Pending;
            }
            else
            {
                document.State = FakeDocumentState.Validated;
                document.ValidatedAt = now;
                document.Errors["FAJ44b"] = "Regla: FAJ44b, Notificación: Nombre informado No corresponde al registrado en el RUT con respecto al Nit suminstrado.";
            }

            _documents[(kind, reference)] = document;
            var status = document.State == FakeDocumentState.Rejected ? RejectionStatusCode : 201;
            var message = document.State == FakeDocumentState.Validated
                ? $"Documento con el código de referencia {reference} registrado y validado con éxito"
                : $"Documento con el código de referencia {reference} registrado";
            return Envelope(status, status == 201 ? "Created" : "Unprocessable Content", message, DocumentJson(document));
        }
    }

    private FakeResponse Resubmit(FakeDocument existing)
    {
        switch (existing.State)
        {
            case FakeDocumentState.Validated:
                return DuplicateBehavior == DuplicateReferenceBehavior.Conflict
                    ? new FactusConflict($"El código de referencia {existing.ReferenceCode} ya fue utilizado.").Response
                    : Envelope(200, "OK", $"Documento con el código de referencia {existing.ReferenceCode} ya fue validado", DocumentJson(existing));
            case FakeDocumentState.Pending:
                // "El API detectará que es un reintento, consultará el estado actual en la DIAN y actualizará el status".
                existing.State = FakeDocumentState.Validated;
                existing.ValidatedAt = _time.GetUtcNow();
                return Envelope(200, "OK", $"Documento con el código de referencia {existing.ReferenceCode} validado", DocumentJson(existing));
            default:
                return new FakeResponse(409, Message(PendingMessage(existing.Kind)));
        }
    }

    private sealed record FactusConflict(string Text)
    {
        public FakeResponse Response => new(409, new JsonObject { ["status"] = "Conflict", ["message"] = Text }.ToJsonString());
    }

    private Dictionary<string, List<string>> Validate(FakeDocumentKind kind, JsonElement root)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
                errors[field] = list = [];
            list.Add(message);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            Add("body", "El cuerpo debe ser un objeto JSON.");
            return errors;
        }

        if (!root.TryGetProperty("reference_code", out var reference) || reference.ValueKind != JsonValueKind.String || reference.GetString()!.Length == 0)
            Add("reference_code", "El campo reference_code es obligatorio.");

        if (!root.TryGetProperty("payment_details", out var payments) || payments.ValueKind != JsonValueKind.Array || payments.GetArrayLength() == 0)
        {
            Add("payment_details", "El campo payment_details es obligatorio.");
        }
        else
        {
            var i = 0;
            foreach (var payment in payments.EnumerateArray())
            {
                Required(payment, $"payment_details.{i}", "payment_form", Add);
                Required(payment, $"payment_details.{i}", "payment_method_code", Add);
                Amount(payment, $"payment_details.{i}", "amount", Add, required: true);
                i++;
            }
        }

        switch (kind)
        {
            case FakeDocumentKind.Bill:
                ValidateParty(root, "customer", required: true, Add);
                break;
            case FakeDocumentKind.CreditNote:
                Required(root, null, "correction_concept_code", Add);
                ValidateParty(root, "customer", required: false, Add);
                var customization = root.TryGetProperty("customization_id", out var c) ? c.GetString() : "20";
                if (customization != "22")
                {
                    var billNumber = root.TryGetProperty("bill_number", out var b) ? b.GetString() : null;
                    var bill = _documents.Values.FirstOrDefault(d => d.Kind == FakeDocumentKind.Bill && d.Number == billNumber);
                    if (bill is null || bill.State != FakeDocumentState.Validated)
                        Add("bill_number", "La factura referenciada no existe o no está validada.");
                }
                break;
            case FakeDocumentKind.SupportDocument or FakeDocumentKind.AdjustmentNote:
                if (kind == FakeDocumentKind.AdjustmentNote)
                {
                    Required(root, null, "correction_concept_code", Add);
                    var supportNumber = root.TryGetProperty("support_document_number", out var n) ? n.GetString() : null;
                    var support = _documents.Values.FirstOrDefault(d => d.Kind == FakeDocumentKind.SupportDocument && d.Number == supportNumber);
                    if (support is null || support.State != FakeDocumentState.Validated)
                        Add("support_document_number", "El documento soporte referenciado no existe o no está validado.");
                }

                if (!root.TryGetProperty("provider", out var provider) || provider.ValueKind != JsonValueKind.Object)
                {
                    Add("provider", "El campo provider es obligatorio.");
                }
                else
                {
                    foreach (var field in new[] { "identification_document_code", "identification", "names", "address", "country_code" })
                        Required(provider, "provider", field, Add);
                }
                break;
        }

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            Add("items", "El campo items es obligatorio.");
        }
        else
        {
            var i = 0;
            foreach (var item in items.EnumerateArray())
            {
                var prefix = $"items.{i}";
                foreach (var field in new[] { "code_reference", "name", "unit_measure_code", "standard_code" })
                    Required(item, prefix, field, Add);
                Amount(item, prefix, "quantity", Add, required: true);
                Amount(item, prefix, "price", Add, required: true);
                Amount(item, prefix, "discount_rate", Add, required: false);
                Amount(item, prefix, "discount_amount", Add, required: false);
                if (item.TryGetProperty("discount_rate", out _) && item.TryGetProperty("discount_amount", out _))
                    Add($"{prefix}.discount_amount", "Use discount_rate o discount_amount, no ambos.");
                if (!item.TryGetProperty("taxes", out var taxes) || taxes.ValueKind != JsonValueKind.Array)
                {
                    Add($"{prefix}.taxes", "El campo taxes es obligatorio.");
                }
                else
                {
                    var t = 0;
                    foreach (var tax in taxes.EnumerateArray())
                    {
                        Required(tax, $"{prefix}.taxes.{t}", "code", Add);
                        Amount(tax, $"{prefix}.taxes.{t}", "rate", Add, required: true);
                        t++;
                    }
                }
                i++;
            }
        }

        if (root.TryGetProperty("cash_rounding_amount", out _))
        {
            Amount(root, null, "cash_rounding_amount", Add, required: false);
            if (DecimalOf(root, "cash_rounding_amount") is { } rounding && Math.Abs(rounding) > 500m)
                Add("cash_rounding_amount", "El valor máximo permitido es ±500.00.");
        }
        return errors;
    }

    private static void ValidateParty(JsonElement root, string name, bool required, Action<string, string> add)
    {
        if (!root.TryGetProperty(name, out var party) || party.ValueKind != JsonValueKind.Object)
        {
            if (required)
                add(name, $"El campo {name} es obligatorio.");
            return;
        }
        Required(party, name, "identification_document_code", add);
        Required(party, name, "identification", add);
        Required(party, name, "legal_organization_code", add);
        var organization = party.TryGetProperty("legal_organization_code", out var o) ? o.GetString() : null;
        if (organization == "1")
            Required(party, name, "company", add);
        if (organization == "2")
            Required(party, name, "names", add);
    }

    private static void Required(JsonElement element, string? prefix, string field, Action<string, string> add)
    {
        if (!element.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            add(prefix is null ? field : $"{prefix}.{field}", $"El campo {field} es obligatorio.");
    }

    /// <summary>Valores numéricos: texto con máximo dos decimales (la API los documenta como string).</summary>
    private static void Amount(JsonElement element, string? prefix, string field, Action<string, string> add, bool required)
    {
        var name = prefix is null ? field : $"{prefix}.{field}";
        if (!element.TryGetProperty(field, out var value))
        {
            if (required)
                add(name, $"El campo {field} es obligatorio.");
            return;
        }
        if (value.ValueKind != JsonValueKind.String
            || !decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            || decimal.Round(number, 2) != number)
        {
            add(name, $"El campo {field} debe ser numérico con máximo dos decimales.");
        }
    }

    private FakeNumberingRange? SelectRange(FakeDocumentKind kind, JsonElement root, out string? error)
    {
        var code = RangeCode(kind);
        var active = NumberingRanges.Where(r => r.Document == code && r.IsActive && !r.IsExpired).ToList();
        error = null;
        if (root.TryGetProperty("numbering_range_id", out var id) && id.ValueKind == JsonValueKind.Number)
        {
            var range = active.FirstOrDefault(r => r.Id == id.GetInt32());
            if (range is null)
                error = "El rango de numeración no existe, no está activo o no corresponde al documento.";
            return range;
        }
        if (active.Count == 1)
            return active[0];
        error = active.Count == 0 ? "No hay rangos de numeración activos." : "Tiene varios rangos activos: debe enviar numbering_range_id.";
        return null;
    }

    private static FakeTotals Totals(JsonElement root)
    {
        decimal gross = 0, discounts = 0, taxable = 0, taxes = 0;
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            var lineGross = Round(DecimalOf(item, "price")!.Value * DecimalOf(item, "quantity")!.Value);
            var discount = DecimalOf(item, "discount_amount") ?? Round(lineGross * (DecimalOf(item, "discount_rate") ?? 0m) / 100m);
            var lineBase = lineGross - discount;
            gross += lineGross;
            discounts += discount;
            var taxed = false;
            foreach (var tax in item.GetProperty("taxes").EnumerateArray())
            {
                var excluded = tax.TryGetProperty("is_excluded", out var e) && e.ValueKind == JsonValueKind.True;
                if (excluded)
                    continue;
                taxed = true;
                // Cada impuesto por separado con redondeo bancario (preguntas frecuentes de Factus).
                taxes += Round(lineBase * DecimalOf(tax, "rate")!.Value / 100m);
            }
            if (taxed)
                taxable += lineBase;
        }
        return new FakeTotals(gross, discounts, taxable, taxes, gross - discounts + taxes);
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.ToEven);

    // ---- Consulta ----

    private FakeResponse List(FakeDocumentKind kind, Dictionary<string, string> query)
    {
        IEnumerable<FakeDocument> documents = _documents.Values.Where(d => d.Kind == kind).OrderBy(d => d.CreatedAt);
        if (query.TryGetValue("filter[reference_code]", out var reference))
            documents = documents.Where(d => d.ReferenceCode == reference);
        if (query.TryGetValue("filter[number]", out var number))
            documents = documents.Where(d => d.Number == number);
        if (query.TryGetValue("filter[status]", out var status))
            documents = documents.Where(d => (d.State == FakeDocumentState.Validated ? "1" : "0") == status);

        var items = new JsonArray(documents.Select(d => (JsonNode)Summary(d)).ToArray());
        var count = items.Count;
        var page = new JsonObject
        {
            ["data"] = items,
            ["pagination"] = new JsonObject
            {
                ["total"] = count,
                ["per_page"] = 10,
                ["current_page"] = 1,
                ["last_page"] = 1,
                ["from"] = count == 0 ? null : 1,
                ["to"] = count == 0 ? null : count,
                ["links"] = new JsonArray(),
            },
        };
        return Envelope(200, "OK", "Solicitud exitosa", page);
    }

    private FakeResponse Show(FakeDocumentKind kind, string number)
    {
        var document = _documents.Values.FirstOrDefault(d => d.Kind == kind && d.Number == number);
        return document is null ? NotFound() : Envelope(200, "OK", "Solicitud exitosa", DocumentJson(document));
    }

    private FakeResponse Download(FakeDocumentKind kind, string number, bool pdf)
    {
        var document = _documents.Values.FirstOrDefault(d => d.Kind == kind && d.Number == number);
        if (document is null)
            return NotFound();
        var content = pdf
            ? Encoding.ASCII.GetBytes($"%PDF-1.4\n% Factus simulado {document.Number}\n%%EOF\n")
            : Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Invoice><ID>{document.Number}</ID><UUID>{document.Cufe}</UUID></Invoice>");
        var data = new JsonObject
        {
            ["file_name"] = $"fv0{document.Number}",
            [pdf ? "pdf_base_64_encoded" : "xml_base_64_encoded"] = Convert.ToBase64String(content),
        };
        return Envelope(200, "OK", "Solicitud exitosa", data);
    }

    private FakeResponse Delete(FakeDocumentKind kind, string reference)
    {
        if (!_documents.TryGetValue((kind, reference), out var document))
            return NotFound();
        if (document.State == FakeDocumentState.Validated)
            return new FakeResponse(409, Message("El documento ya fue validado por la DIAN y no se puede eliminar."));
        _documents.Remove((kind, reference));
        return Envelope(200, "OK", "Documento eliminado con éxito", null);
    }

    private FakeResponse Ranges(Dictionary<string, string> query)
    {
        IEnumerable<FakeNumberingRange> ranges = NumberingRanges;
        if (query.TryGetValue("filter[is_active]", out var active))
            ranges = ranges.Where(r => (r.IsActive ? "1" : "0") == active);
        if (query.TryGetValue("filter[document]", out var document))
            ranges = ranges.Where(r => r.Document == document);
        if (query.TryGetValue("filter[id]", out var id))
            ranges = ranges.Where(r => r.Id.ToString(CultureInfo.InvariantCulture) == id);

        var items = new JsonArray(ranges.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id,
            ["document"] = r.Document,
            ["prefix"] = r.Prefix,
            ["from"] = r.From,
            ["to"] = r.To,
            ["current"] = r.Current,
            ["resolution_number"] = r.ResolutionNumber,
            ["start_date"] = r.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["end_date"] = r.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["technical_key"] = r.TechnicalKey,
            ["is_expired"] = r.IsExpired ? 1 : 0,
            ["is_active"] = r.IsActive ? 1 : 0,
        }).ToArray());
        return Envelope(200, "OK", "Solicitud exitosa", new JsonObject { ["data"] = items });
    }

    // ---- JSON de respuesta ----

    private JsonObject DocumentJson(FakeDocument document)
    {
        var range = NumberingRanges.First(r => r.Id == document.NumberingRangeId);
        var (codeName, uniqueCode) = document.Kind switch
        {
            FakeDocumentKind.Bill => (("01", "Factura electrónica de Venta"), "cufe"),
            FakeDocumentKind.CreditNote => (("91", "Nota Crédito"), "cude"),
            FakeDocumentKind.AdjustmentNote => (("95", "Nota de ajuste al documento soporte"), "cuds"),
            _ => (("05", "Documento soporte"), "cuds"),
        };
        var validated = document.State == FakeDocumentState.Validated;
        var errors = new JsonObject();
        foreach (var (rule, message) in document.Errors)
            errors[rule] = message;

        return new JsonObject
        {
            ["reference_code"] = document.ReferenceCode,
            ["number"] = document.Number,
            ["document_type"] = new JsonObject { ["code"] = codeName.Item1, ["name"] = codeName.Item2 },
            ["is_validated"] = validated,
            ["validated_at"] = document.ValidatedAt?.ToOffset(TimeSpan.FromHours(-5)).ToString("dd-MM-yyyy hh:mm:ss tt", CultureInfo.InvariantCulture),
            ["errors"] = errors,
            ["created_at"] = document.CreatedAt.ToOffset(TimeSpan.FromHours(-5)).ToString("dd-MM-yyyy hh:mm:ss tt", CultureInfo.InvariantCulture),
            [uniqueCode] = validated ? document.Cufe : null,
            ["numbering_range"] = new JsonObject
            {
                ["prefix"] = range.Prefix,
                ["from"] = range.From,
                ["to"] = range.To,
                ["resolution_number"] = range.ResolutionNumber,
                ["start_date"] = range.StartDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                ["end_date"] = range.EndDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
            },
            ["totals"] = new JsonObject
            {
                ["prepayment_amount"] = "0.00",
                ["gross_amount"] = Text(document.Totals.GrossAmount),
                ["taxable_amount"] = Text(document.Totals.TaxableAmount),
                ["tax_amount"] = Text(document.Totals.TaxAmount),
                ["surcharge_amount"] = "0.00",
                ["total"] = Text(document.Totals.Total),
            },
            ["links"] = new JsonObject
            {
                ["qr"] = validated ? $"https://catalogo-vpfe-hab.dian.gov.co/document/searchqr?documentkey={document.Cufe}" : null,
                ["public_url"] = $"https://app-sandbox.factus.test/documents/{document.Number}",
            },
        };
    }

    private static JsonObject Summary(FakeDocument document) => new()
    {
        ["id"] = document.Number.GetHashCode(StringComparison.Ordinal) & 0x7fffffff,
        ["number"] = document.Number,
        ["reference_code"] = document.ReferenceCode,
        ["cufe"] = document.State == FakeDocumentState.Validated ? document.Cufe : null,
        ["status"] = document.State == FakeDocumentState.Validated ? 1 : 0,
        ["total"] = Text(document.Totals.Total),
        ["errors"] = new JsonArray(document.Errors.Values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
    };

    // ---- Utilidades ----

    private static string PendingMessage(FakeDocumentKind kind) => kind switch
    {
        FakeDocumentKind.Bill => "Se encontró una factura pendiente por enviar a la DIAN, puede eliminarla o enviarla.",
        FakeDocumentKind.CreditNote => "Se encontró una nota crédito pendiente por enviar a la DIAN, puede eliminarla o enviarla.",
        FakeDocumentKind.AdjustmentNote => "Se encontró una nota de ajuste pendiente por enviar a la DIAN, puede eliminarla o enviarla.",
        _ => "Se encontró un documento soporte pendiente por enviar a la DIAN, puede eliminarlo o enviarlo.",
    };

    private static FakeDocumentKind? KindOf(string resource) => resource switch
    {
        "bills" => FakeDocumentKind.Bill,
        "credit-notes" => FakeDocumentKind.CreditNote,
        "support-documents" => FakeDocumentKind.SupportDocument,
        "adjustment-notes" => FakeDocumentKind.AdjustmentNote,
        _ => null,
    };

    private static string RangeCode(FakeDocumentKind kind) => kind switch
    {
        FakeDocumentKind.Bill => "21",
        FakeDocumentKind.CreditNote => "22",
        FakeDocumentKind.AdjustmentNote => "25",
        _ => "24",
    };

    private static decimal? DecimalOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string Text(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static FakeResponse Envelope(int status, string statusText, string message, JsonNode? data) =>
        new(status, new JsonObject { ["status"] = statusText, ["message"] = message, ["data"] = data }.ToJsonString());

    private static FakeResponse Unprocessable(Dictionary<string, List<string>> errors)
    {
        var json = new JsonObject();
        foreach (var (field, messages) in errors)
            json[field] = new JsonArray(messages.Select(m => (JsonNode)JsonValue.Create(m)).ToArray());
        return new FakeResponse(422, new JsonObject
        {
            ["message"] = "Los datos proporcionados no son válidos.",
            ["errors"] = json,
        }.ToJsonString());
    }

    private static FakeResponse NotFound() => new(404, Message("No se encontró el recurso solicitado."));

    private static string Message(string text) => new JsonObject { ["message"] = text }.ToJsonString();

    private static (string Path, Dictionary<string, string> Query) SplitQuery(string pathAndQuery)
    {
        var index = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = (index < 0 ? pathAndQuery : pathAndQuery[..index]).Trim('/');
        var query = index < 0 ? [] : ParseForm(pathAndQuery[(index + 1)..]);
        return (path, query);
    }

    private static Dictionary<string, string> ParseForm(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            result[key] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }
        return result;
    }
}

/// <summary>Respuesta del simulado, independiente del transporte.</summary>
internal sealed record FakeResponse(int Status, string? Json, Dictionary<string, string> Headers)
{
    public FakeResponse(int status, string? json) : this(status, json, [])
    {
    }

    public bool Drop { get; init; }

    public static FakeResponse Dropped => new(0, null) { Drop = true };
}
