using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>
/// Implementación de <see cref="IFactusApi"/> (cliente tipado de IHttpClientFactory).
/// <para>Traducción de respuestas (https://developers.factus.com.co/respuestas-del-api, /manejo-errores, /limite-de-request):</para>
/// <list type="bullet">
/// <item>201 con <c>is_validated</c> true → <see cref="FactusOutcome.Accepted"/>; 200 con el documento → <see cref="FactusOutcome.Duplicate"/>
/// (SUPUESTO: la creación responde "Created"/201 y la reutilización de un <c>reference_code</c> ya validado devuelve el
/// documento existente — "idempotencia" en las preguntas frecuentes — con 200).</item>
/// <item><c>is_validated</c> false con un mensaje "Rechazo" → <see cref="FactusOutcome.Rejected"/> (bloquea los siguientes envíos
/// hasta eliminarlo); sin rechazo → <see cref="FactusOutcome.Pending"/> (la DIAN tarda: reenviar los mismos datos).</item>
/// <item>409 ("Se encontró una factura pendiente por enviar a la DIAN" o <c>reference_code</c> repetido) → se consulta por
/// <c>reference_code</c> y se devuelve el existente (<see cref="FactusOutcome.Duplicate"/>), o rechazo bloqueante si el
/// pendiente es otro documento.</item>
/// <item>422/400/404 → <see cref="FactusOutcome.Rejected"/> con los mensajes de validación.</item>
/// <item>401 → se renueva el token y se reintenta UNA vez; si persiste, o 402/403 → <see cref="FactusOutcome.CredentialsError"/>.</item>
/// <item>408/429/5xx, red o tiempo agotado → <see cref="FactusOutcome.TransientError"/> con <c>Retry-After</c> (o <c>X-RateLimit-Reset</c>).
/// Límite documentado: 80 peticiones por minuto por usuario (NIT).</item>
/// </list>
/// </summary>
internal sealed partial class FactusApiClient(
    HttpClient http,
    IFactusOptionsProvider optionsProvider,
    FactusTokenManager tokens,
    TimeProvider time,
    ILogger<FactusApiClient> logger) : IFactusApi
{
    public async Task<FactusQueryResult<FactusSession>> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var options = await optionsProvider.GetAsync(cancellationToken);
        if (options is null)
            return FactusQueryResult<FactusSession>.From(NotConfigured());

        var lease = await tokens.GetAsync(options, forcePasswordGrant: true, cancellationToken);
        return lease.Failure is { } failure
            ? FactusQueryResult<FactusSession>.From(failure)
            : FactusQueryResult<FactusSession>.Found(new FactusSession(lease.ExpiresAt), 200);
    }

    public Task<FactusResult> CreateBillAsync(FactusBillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SubmitAsync(FactusDocumentKind.Bill, request.ReferenceCode, FactusJson.Serialize(request), cancellationToken);
    }

    public Task<FactusResult> CreateCreditNoteAsync(FactusCreditNoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SubmitAsync(FactusDocumentKind.CreditNote, request.ReferenceCode, FactusJson.Serialize(request), cancellationToken);
    }

    public Task<FactusResult> CreateSupportDocumentAsync(FactusSupportDocumentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SubmitAsync(FactusDocumentKind.SupportDocument, request.ReferenceCode, FactusJson.Serialize(request), cancellationToken);
    }

    public Task<FactusResult> CreateAdjustmentNoteAsync(FactusAdjustmentNoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SubmitAsync(FactusDocumentKind.AdjustmentNote, request.ReferenceCode, FactusJson.Serialize(request), cancellationToken);
    }

    public async Task<FactusQueryResult<IReadOnlyList<FactusNumberingRange>>> GetNumberingRangesAsync(
        bool onlyActive, string? documentCode, CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (onlyActive)
            query.Add("filter[is_active]=1");
        if (!string.IsNullOrWhiteSpace(documentCode))
            query.Add("filter[document]=" + Uri.EscapeDataString(documentCode));
        var path = "v2/numbering-ranges" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);

        var exchange = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        if (exchange.Failure is { } failure)
            return FactusQueryResult<IReadOnlyList<FactusNumberingRange>>.From(failure);
        if (!exchange.IsSuccess)
            return QueryFailure<IReadOnlyList<FactusNumberingRange>>(exchange);

        using var document = exchange.Json;
        IReadOnlyList<FactusNumberingRange> ranges = document is null
            ? []
            : FactusResponseReader.ReadList(document.RootElement).Select(FactusResponseReader.ToRange).OfType<FactusNumberingRange>().ToList();
        return FactusQueryResult<IReadOnlyList<FactusNumberingRange>>.Found(ranges, exchange.Status);
    }

    public async Task<FactusQueryResult<FactusDocument>> GetDocumentAsync(FactusDocumentKind kind, string number, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        var exchange = await SendAsync(HttpMethod.Get, $"v2/{Resource(kind)}/{Uri.EscapeDataString(number)}", null, cancellationToken);
        if (exchange.Failure is { } failure)
            return FactusQueryResult<FactusDocument>.From(failure);
        if (!exchange.IsSuccess)
            return QueryFailure<FactusDocument>(exchange);

        using var json = exchange.Json;
        return json is not null && FactusResponseReader.ReadDocument(kind, json.RootElement) is { } found
            ? FactusQueryResult<FactusDocument>.Found(found, exchange.Status)
            : new FactusQueryResult<FactusDocument> { Status = FactusQueryStatus.NotFound, HttpStatus = exchange.Status };
    }

    public async Task<FactusQueryResult<FactusDocument>> FindByReferenceAsync(FactusDocumentKind kind, string referenceCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceCode);
        var exchange = await SendAsync(
            HttpMethod.Get, $"v2/{Resource(kind)}?filter[reference_code]={Uri.EscapeDataString(referenceCode)}", null, cancellationToken);
        if (exchange.Failure is { } failure)
            return FactusQueryResult<FactusDocument>.From(failure);
        if (!exchange.IsSuccess)
            return QueryFailure<FactusDocument>(exchange);

        using var json = exchange.Json;
        var match = json is null
            ? null
            : FactusResponseReader.ReadList(json.RootElement)
                .Where(e => FactusResponseReader.String(e, "reference_code") == referenceCode)
                .Select(e => FactusResponseReader.ToDocument(kind, e))
                .FirstOrDefault();
        return match is null
            ? new FactusQueryResult<FactusDocument> { Status = FactusQueryStatus.NotFound, HttpStatus = exchange.Status }
            : FactusQueryResult<FactusDocument>.Found(match, exchange.Status);
    }

    public Task<FactusQueryResult<FactusFile>> DownloadPdfAsync(FactusDocumentKind kind, string number, CancellationToken cancellationToken) =>
        DownloadAsync(kind, number, "download-pdf", "pdf_base_64_encoded", "application/pdf", ".pdf", cancellationToken);

    public Task<FactusQueryResult<FactusFile>> DownloadXmlAsync(FactusDocumentKind kind, string number, CancellationToken cancellationToken) =>
        DownloadAsync(kind, number, "download-xml", "xml_base_64_encoded", "application/xml", ".xml", cancellationToken);

    public async Task<FactusQueryResult<bool>> DeleteUnvalidatedAsync(FactusDocumentKind kind, string referenceCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceCode);
        var prefix = kind == FactusDocumentKind.Bill ? "v2/bills/destroy/reference/" : $"v2/{Resource(kind)}/reference/";
        var exchange = await SendAsync(HttpMethod.Delete, prefix + Uri.EscapeDataString(referenceCode), null, cancellationToken);
        if (exchange.Failure is { } failure)
            return FactusQueryResult<bool>.From(failure);
        if (!exchange.IsSuccess)
            return QueryFailure<bool>(exchange);
        exchange.Json?.Dispose();
        return FactusQueryResult<bool>.Found(true, exchange.Status);
    }

    // ---- Emisión ----

    private async Task<FactusResult> SubmitAsync(FactusDocumentKind kind, string referenceCode, string body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceCode);
        var exchange = await SendAsync(HttpMethod.Post, $"v2/{Resource(kind)}/validate", body, cancellationToken);
        if (exchange.Failure is { } failure)
        {
            LogSubmission(logger, kind, referenceCode, failure.HttpStatus, failure.Outcome);
            return failure;
        }

        FactusResult result;
        using (var json = exchange.Json)
        {
            result = exchange.Status switch
            {
                >= 200 and < 300 => FromDocument(kind, json, exchange.Status),
                409 => await ResolveConflictAsync(kind, referenceCode, json, cancellationToken),
                402 or 403 => FactusResult.Credentials(Describe(json, $"Factus negó el acceso o la cuenta no tiene documentos disponibles (HTTP {exchange.Status})."), exchange.Status),
                _ => Rejection(kind, json, exchange.Status),
            };
        }

        LogSubmission(logger, kind, referenceCode, result.HttpStatus, result.Outcome);
        return result;
    }

    private static FactusResult FromDocument(FactusDocumentKind kind, JsonDocument? json, int status)
    {
        var document = json is null ? null : FactusResponseReader.ReadDocument(kind, json.RootElement);
        if (document is null)
            return FactusResult.Transient("Factus respondió sin el documento esperado.", status);

        if (document.IsValidated)
        {
            return new FactusResult
            {
                Outcome = status == 201 ? FactusOutcome.Accepted : FactusOutcome.Duplicate,
                Document = document,
                Messages = document.DianMessages,
                HttpStatus = status,
            };
        }

        return document.HasRejection
            ? new FactusResult
            {
                Outcome = FactusOutcome.Rejected,
                Document = document,
                Messages = document.DianMessages,
                HttpStatus = status,
                BlocksFurtherSubmissions = true,
                Detail = "La DIAN rechazó el documento: corrija los datos, elimínelo en Factus y reenvíelo.",
            }
            : new FactusResult
            {
                Outcome = FactusOutcome.Pending,
                Document = document,
                Messages = document.DianMessages,
                HttpStatus = status,
                Detail = "La DIAN aún no valida el documento: reenvíe los mismos datos más tarde.",
            };
    }

    private static FactusResult Rejection(FactusDocumentKind kind, JsonDocument? json, int status)
    {
        // Un 422 puede traer el documento con el rechazo de la DIAN o solo errores de validación del JSON.
        if (json is not null && FactusResponseReader.ReadDocument(kind, json.RootElement) is { } document && document.DianMessages.Count > 0)
            return FromDocument(kind, json, status) with { HttpStatus = status };

        var messages = json is null ? [] : FactusResponseReader.ValidationMessages(json.RootElement);
        return new FactusResult
        {
            Outcome = FactusOutcome.Rejected,
            Messages = messages,
            HttpStatus = status,
            Detail = Describe(json, status == 422 ? "Factus rechazó los datos del documento (validación)." : $"Factus rechazó la petición (HTTP {status})."),
        };
    }

    private async Task<FactusResult> ResolveConflictAsync(FactusDocumentKind kind, string referenceCode, JsonDocument? json, CancellationToken cancellationToken)
    {
        var conflictMessage = Describe(json, "Conflicto en Factus.");
        var existing = await FindByReferenceAsync(kind, referenceCode, cancellationToken);
        switch (existing.Status)
        {
            case FactusQueryStatus.Found:
                var document = existing.Value!;
                if (document.HasRejection)
                {
                    return new FactusResult
                    {
                        Outcome = FactusOutcome.Rejected,
                        Document = document,
                        Messages = document.DianMessages,
                        HttpStatus = 409,
                        BlocksFurtherSubmissions = true,
                        Detail = "El documento ya existe en Factus rechazado: elimínelo y reenvíelo corregido.",
                    };
                }
                return new FactusResult
                {
                    Outcome = document.IsValidated ? FactusOutcome.Duplicate : FactusOutcome.Pending,
                    Document = document,
                    Messages = document.DianMessages,
                    HttpStatus = 409,
                    Detail = document.IsValidated ? "El reference_code ya estaba validado; se devuelve el documento existente." : conflictMessage,
                };
            case FactusQueryStatus.NotFound:
                // Otro documento (no este) quedó pendiente/rechazado y bloquea los envíos del emisor.
                return new FactusResult
                {
                    Outcome = FactusOutcome.Rejected,
                    Messages = [new FactusMessage("409", conflictMessage)],
                    HttpStatus = 409,
                    BlocksFurtherSubmissions = true,
                    Detail = "Factus tiene otro documento pendiente por enviar a la DIAN que bloquea los envíos: elimínelo o reenvíelo.",
                };
            case FactusQueryStatus.CredentialsError:
                return FactusResult.Credentials(existing.Detail ?? "Credenciales de Factus inválidas.", existing.HttpStatus);
            default:
                return FactusResult.Transient($"Conflicto 409 y no fue posible consultar el documento existente: {existing.Detail}", existing.HttpStatus, existing.RetryAfter);
        }
    }

    // ---- Consulta y descarga ----

    private async Task<FactusQueryResult<FactusFile>> DownloadAsync(
        FactusDocumentKind kind, string number, string action, string base64Field, string contentType, string extension, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        var exchange = await SendAsync(HttpMethod.Get, $"v2/{Resource(kind)}/{Uri.EscapeDataString(number)}/{action}", null, cancellationToken);
        if (exchange.Failure is { } failure)
            return FactusQueryResult<FactusFile>.From(failure);
        if (!exchange.IsSuccess)
            return QueryFailure<FactusFile>(exchange);

        using var json = exchange.Json;
        var data = json is null ? default : FactusResponseReader.Data(json.RootElement);
        var encoded = data.ValueKind == JsonValueKind.Object ? FactusResponseReader.String(data, base64Field) : null;
        if (encoded is null)
            return new FactusQueryResult<FactusFile> { Status = FactusQueryStatus.Failed, HttpStatus = exchange.Status, Detail = $"Factus no devolvió {base64Field}." };

        byte[] content;
        try
        {
            content = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return new FactusQueryResult<FactusFile> { Status = FactusQueryStatus.Failed, HttpStatus = exchange.Status, Detail = "Base64 inválido en la descarga." };
        }

        var fileName = FactusResponseReader.String(data, "file_name") ?? number;
        if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            fileName += extension;
        return FactusQueryResult<FactusFile>.Found(new FactusFile(fileName, contentType, content), exchange.Status);
    }

    private static FactusQueryResult<T> QueryFailure<T>(Exchange exchange)
    {
        using var json = exchange.Json;
        return new FactusQueryResult<T>
        {
            Status = exchange.Status == 404 ? FactusQueryStatus.NotFound : FactusQueryStatus.Failed,
            HttpStatus = exchange.Status,
            Detail = Describe(json, $"HTTP {exchange.Status}"),
        };
    }

    // ---- Transporte: token, 401 con reintento único, errores de red ----

    private async Task<Exchange> SendAsync(HttpMethod method, string path, string? jsonBody, CancellationToken cancellationToken)
    {
        var options = await optionsProvider.GetAsync(cancellationToken);
        if (options is null)
            return new Exchange(0, null, NotConfigured());

        for (var attempt = 1; ; attempt++)
        {
            var lease = await tokens.GetAsync(options, forcePasswordGrant: false, cancellationToken);
            if (lease.Failure is { } authFailure)
                return new Exchange(0, null, authFailure);

            using var request = new HttpRequestMessage(method, new Uri(options.EffectiveBaseUrl, path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.Token);
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Exchange(0, null, FactusResult.Transient("Factus no respondió a tiempo."));
            }
            catch (HttpRequestException ex)
            {
                return new Exchange(0, null, FactusResult.Transient($"Sin conexión con Factus ({ex.HttpRequestError})."));
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await tokens.InvalidateAsync(options, lease.Token!, cancellationToken);
                    if (attempt == 1)
                    {
                        LogUnauthorizedRetry(logger);
                        continue;
                    }
                    return new Exchange(0, null, FactusResult.Credentials("Factus rechazó el token aun después de renovarlo (HTTP 401).", status));
                }

                if (IsTransient(response.StatusCode))
                {
                    var retryAfter = RetryAfterOf(response, time);
                    var detail = response.StatusCode == HttpStatusCode.TooManyRequests
                        ? "Límite de peticiones de Factus alcanzado (HTTP 429)."
                        : $"Factus no disponible (HTTP {status}).";
                    return new Exchange(0, null, FactusResult.Transient(detail, status, retryAfter));
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new Exchange(status, FactusResponseReader.Parse(body), null);
            }
        }
    }

    internal static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary><c>Retry-After</c> (segundos o fecha); si falta, <c>X-RateLimit-Reset</c> (segundos o marca Unix).</summary>
    internal static TimeSpan? RetryAfterOf(HttpResponseMessage response, TimeProvider time)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
            return delta;
        if (retry?.Date is { } date)
            return Max(date - time.GetUtcNow());

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reset))
        {
            return reset > 100_000
                ? Max(DateTimeOffset.FromUnixTimeSeconds(reset) - time.GetUtcNow())
                : TimeSpan.FromSeconds(reset);
        }
        return null;

        static TimeSpan Max(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    private static string Resource(FactusDocumentKind kind) => kind switch
    {
        FactusDocumentKind.Bill => "bills",
        FactusDocumentKind.CreditNote => "credit-notes",
        FactusDocumentKind.SupportDocument => "support-documents",
        FactusDocumentKind.AdjustmentNote => "adjustment-notes",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Describe(JsonDocument? json, string fallback) =>
        json is not null && FactusResponseReader.Message(json.RootElement) is { Length: > 0 } message ? message : fallback;

    private static FactusResult NotConfigured() => FactusResult.Credentials("Factus no está configurado (faltan credenciales).");

    [LoggerMessage(Level = LogLevel.Information, Message = "Factus: {Kind} {ReferenceCode} → {Outcome} (HTTP {Status}).")]
    private static partial void LogSubmission(ILogger logger, FactusDocumentKind kind, string referenceCode, int? status, FactusOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Factus respondió 401: se renueva el token y se reintenta una vez.")]
    private static partial void LogUnauthorizedRetry(ILogger logger);

    /// <summary>Respuesta HTTP ya leída (código y JSON) o el fallo tipado que la reemplaza.</summary>
    private readonly record struct Exchange(int Status, JsonDocument? Json, FactusResult? Failure)
    {
        public bool IsSuccess => Status is >= 200 and < 300;
    }
}
