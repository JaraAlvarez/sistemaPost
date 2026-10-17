using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Domain;

namespace Pos.Modules.Cash.Infrastructure.Sistecredito;

/// <summary>
/// Adaptador HTTP de la API de pagos de Sistecrédito (D7B-01), copia fiel del protocolo que tutiendanueva ya usa en su tienda en línea
/// (<c>backend/src/services/sistecredito.service.js</c>; guías G-ALI-12 y G-ALI-09 de Sistecrédito):
/// <list type="bullet">
/// <item>Base <c>https://api.credinet.co/pay</c>: <c>POST /create</c> y <c>GET /GetTransactionResponse?transactionId=</c>.</item>
/// <item>Cabeceras <c>SCLocation: 0,0</c>, <c>SCOrigen: Staging|Production</c>, <c>country: co</c>, <c>Ocp-Apim-Subscription-Key</c>,
/// <c>ApplicationKey</c> y <c>ApplicationToken</c>.</item>
/// <item>Método de pago 2 (fijo en los dos ambientes), moneda COP, valor en pesos enteros, simulador propio apagado
/// (<c>sandbox.isActive = false</c>: el ambiente lo decide <c>SCOrigen</c>).</item>
/// </list>
/// Nunca escribe las llaves ni el documento del cliente en los mensajes.
/// </summary>
public sealed class SistecreditoHttpGateway : ISistecreditoGateway
{
    public const string HttpClientName = "Sistecredito";

    /// <summary>Id del método de pago dentro de la pasarela: fijo, mismo valor en Staging y Production (G-ALI-12). No es una llave.</summary>
    public const int PaymentMethodId = 2;

    public static readonly Uri DefaultBaseAddress = new("https://api.credinet.co/pay/");

    /// <summary>Errores que significan "estas llaves no sirven" (no un problema del cobro).</summary>
    public static readonly IReadOnlyList<int> CredentialErrorCodes = [685, 690, 691, 694];

    /// <summary>Traducción de los códigos de G-ALI-09 que puede disparar lo que se envía: factura, valor, URL, documento y llaves.</summary>
    public static readonly IReadOnlyDictionary<int, string> ErrorMessages = new Dictionary<int, string>
    {
        [602] = "La factura enviada es demasiado larga.",
        [613] = "El valor del pago está vacío.",
        [614] = "El valor del pago es nulo.",
        [615] = "El valor es demasiado bajo para Sistecrédito: cobre un valor mayor o elija otro medio de pago.",
        [619] = "La URL de confirmación no es válida.",
        [620] = "La URL de respuesta no es válida.",
        [624] = "Falta el tipo de documento del cliente.",
        [625] = "El tipo de documento no puede ser nulo.",
        [626] = "El tipo de documento no tiene un formato reconocido.",
        [627] = "Falta el número de documento del cliente.",
        [628] = "El número de documento no puede ser nulo.",
        [629] = "El número de documento supera los caracteres permitidos.",

        // 685 envuelve el 601 "ImpossibleToRetrieveConfiguration": Sistecrédito no encuentra una tienda con esas tres llaves en el
        // ambiente usado (llave mal copiada, de otro ambiente o tienda todavía no habilitada).
        [685] = "Sistecrédito no reconoce las credenciales conectadas. Revise la llave de suscripción, la ApplicationKey y el ApplicationToken, y confirme con Sistecrédito que la tienda esté habilitada en este ambiente (pruebas o producción).",
        [690] = "La ApplicationKey no tiene el formato correcto: revise la credencial conectada.",
        [691] = "Falta la ApplicationKey: revise la credencial conectada.",
        [694] = "El ApplicationToken no es correcto: revise la credencial conectada.",
        [708] = "La transacción no fue encontrada.",
        [738] = "Ya existe una transacción activa con esa factura.",
        [739] = "Sistecrédito no está disponible para este cliente: elija otro medio de pago.",
    };

    private static readonly string[] InstallmentKeys =
        ["installments", "numberOfInstallments", "installmentsNumber", "installmentNumber", "quotas", "numberOfQuotas", "cuotas", "numeroCuotas", "dues"];

    private readonly HttpClient _http;
    private readonly SistecreditoOptions _options;
    private readonly TimeProvider _time;

    public SistecreditoHttpGateway(HttpClient http, SistecreditoOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _http.BaseAddress ??= DefaultBaseAddress;
        _options = options ?? new SistecreditoOptions();
        _time = time ?? TimeProvider.System;
    }

    public static string ErrorMessageFor(int? errorCode, string fallback) =>
        errorCode is { } code && ErrorMessages.TryGetValue(code, out var message) ? message : fallback;

    public async Task<SistecreditoCreated> CreateAsync(SistecreditoConnection connection, SistecreditoCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        var payload = Payload(
            request.Invoice.Length > 60 ? request.Invoice[..60] : request.Invoice, request.Description, request.Amount, request.DocumentType, request.DocumentNumber,
            request.UrlResponse, request.UrlConfirmation);
        var (root, _) = await SendAsync(Create(connection, payload), cancellationToken);
        var data = Property(root, "data");
        var id = Text(data, "_id");
        if (id is null)
        {
            throw new SistecreditoGatewayException("Sistecrédito no devolvió el número de la transacción: intente de nuevo.", SistecreditoFailure.Provider);
        }

        return new SistecreditoCreated(id, Text(data, "transactionStatus"));
    }

    public async Task<SistecreditoTransaction> GetTransactionAsync(SistecreditoConnection connection, string transactionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"GetTransactionResponse?transactionId={Uri.EscapeDataString(transactionId)}");
        AddHeaders(request, connection.Credentials, connection.Environment);
        var (root, _) = await SendAsync(request, cancellationToken);
        var data = Property(root, "data");
        var response = Property(data, "paymentMethodResponse");
        return new SistecreditoTransaction(
            Text(data, "_id") ?? transactionId, Text(data, "transactionStatus"), Text(response, "paymentRedirectUrl"), Text(response, "description"),
            Installments(data) ?? Installments(response));
    }

    public async Task<SistecreditoTransaction> WaitForRedirectUrlAsync(
        SistecreditoConnection connection, string transactionId, int attempts, TimeSpan interval, CancellationToken cancellationToken)
    {
        SistecreditoTransaction? last = null;
        var total = Math.Max(1, attempts);
        for (var i = 0; i < total; i++)
        {
            last = await GetTransactionAsync(connection, transactionId, cancellationToken);
            if (last.RedirectUrl is not null || SistecreditoProtocol.IsFailed(last.Status) || last.Status == SistecreditoProtocol.ApprovedStatus)
            {
                return last;
            }

            if (i < total - 1 && interval > TimeSpan.Zero)
            {
                await Task.Delay(interval, _time, cancellationToken);
            }
        }

        return last!;
    }

    public async Task<SistecreditoVerification> VerifyCredentialsAsync(SistecreditoConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var chosen = await WorksInAsync(connection.Credentials, connection.Environment, cancellationToken);
        switch (chosen.Outcome)
        {
            case VerifyOutcome.Works:
                return new SistecreditoVerification(true, $"Conexión correcta: Sistecrédito reconoce las llaves en {SistecreditoMapping.Name(connection.Environment)}.", null);
            case VerifyOutcome.Unreachable:
                return new SistecreditoVerification(false, "No se pudo conectar con Sistecrédito: revise el internet de este equipo e intente de nuevo.", null);
            case VerifyOutcome.Unverifiable:
                return new SistecreditoVerification(
                    false,
                    $"No se pudo verificar la conexión: Sistecrédito no respondió bien en este momento{(chosen.HttpStatus is { } http ? $" (código {http})" : string.Empty)}. "
                    + "Intente de nuevo en unos minutos; mientras tanto Sistecrédito no se activa.",
                    null);
        }

        // Llaves rechazadas en el ambiente elegido: si en el otro sí las reconocen, el problema es el ambiente (Sistecrédito da un juego de llaves por ambiente).
        var otherEnvironment = connection.Environment == SistecreditoEnvironment.Staging ? SistecreditoEnvironment.Production : SistecreditoEnvironment.Staging;
        var other = await WorksInAsync(connection.Credentials, otherEnvironment, cancellationToken);
        if (other.Outcome == VerifyOutcome.Works)
        {
            var label = otherEnvironment == SistecreditoEnvironment.Staging ? "Pruebas (Staging)" : "Producción";
            return new SistecreditoVerification(
                false,
                $"Estas llaves NO funcionan en {SistecreditoMapping.Name(connection.Environment)}, pero SÍ en {SistecreditoMapping.Name(otherEnvironment)}. "
                + $"Cambie el ambiente a {label} y vuelva a guardar.",
                otherEnvironment);
        }

        return new SistecreditoVerification(false, ErrorMessageFor(chosen.ErrorCode, ErrorMessages[685]), null);
    }

    // ─────────────────────────────── Detalle del protocolo ───────────────────────────────

    /// <summary>Resultado de probar las llaves contra un ambiente.</summary>
    private enum VerifyOutcome
    {
        /// <summary>Sistecrédito respondió con su formato y no rechazó las llaves.</summary>
        Works,

        /// <summary>Sistecrédito (o su puerta de enlace, HTTP 401/403) rechazó las llaves.</summary>
        CredentialsRejected,

        /// <summary>Sin internet o tiempo agotado.</summary>
        Unreachable,

        /// <summary>Respondió algo que no es de Sistecrédito (5xx, vacío, HTML…): no se sabe si las llaves sirven.</summary>
        Unverifiable,
    }

    /// <summary>
    /// Una llamada de verificación contra un ambiente: crea una transacción (queda sin pagar y vence sola). NO usa el simulador: con él
    /// Sistecrédito responde 618 "SandboxNotDefined" antes de mirar las llaves y las malas pasaban sin aviso. Solo cuenta como "funciona"
    /// una respuesta reconocible de Sistecrédito (JSON con <c>errorCode</c> o <c>data</c>) que no sea de llaves rechazadas; un 401/403 de
    /// la puerta de enlace son llaves malas y un 5xx o una respuesta vacía es "no se pudo verificar".
    /// </summary>
    private async Task<(VerifyOutcome Outcome, int? ErrorCode, int? HttpStatus)> WorksInAsync(
        SistecreditoCredentials credentials, SistecreditoEnvironment environment, CancellationToken cancellationToken)
    {
        var payload = Payload(
            $"VERIF-{_time.GetUtcNow().ToUnixTimeMilliseconds()}", "Verificación de conexión", 50_000m, "CC", "1000000000", _options.ResponseUrl,
            _options.ConfirmationUrl);
        try
        {
            using var request = Create(new SistecreditoConnection(credentials, environment), payload);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            using var response = await _http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var status = (int)response.StatusCode;
            var root = TryParse(body);
            var code = root is { } parsed ? ErrorCode(parsed) : null;
            if (code is { } known && CredentialErrorCodes.Contains(known))
            {
                return (VerifyOutcome.CredentialsRejected, code, status);
            }

            if (status is 401 or 403)
            {
                return (VerifyOutcome.CredentialsRejected, code, status);
            }

            var recognizable = root is { } json && (Property(json, "errorCode") is not null || Property(json, "data") is not null);
            return status >= 500 || !recognizable ? (VerifyOutcome.Unverifiable, code, status) : (VerifyOutcome.Works, code, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (VerifyOutcome.Unreachable, null, null);
        }
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 120));

    private static string Payload(
        string invoice, string description, decimal amount, string documentType, string documentNumber, string urlResponse, string urlConfirmation) =>
        JsonSerializer.Serialize(new
        {
            invoice,
            description = description.Length > 100 ? description[..100] : description,
            paymentMethod = new { paymentMethodId = PaymentMethodId },
            currency = "COP",
            value = (long)decimal.Round(amount, 0, MidpointRounding.AwayFromZero),

            // Simulador propio de Sistecrédito (distinto del ambiente, que va en SCOrigen): siempre apagado.
            sandbox = new { isActive = false },
            urlResponse,
            urlConfirmation,
            methodConfirmation = "POST",
            client = new { docType = documentType, document = documentNumber },
        });

    private static HttpRequestMessage Create(SistecreditoConnection connection, string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "create") { Content = new StringContent(payload, Encoding.UTF8) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        AddHeaders(request, connection.Credentials, connection.Environment);
        return request;
    }

    private static void AddHeaders(HttpRequestMessage request, SistecreditoCredentials credentials, SistecreditoEnvironment environment)
    {
        request.Headers.TryAddWithoutValidation("SCLocation", "0,0");
        request.Headers.TryAddWithoutValidation("SCOrigen", environment == SistecreditoEnvironment.Staging ? "Staging" : "Production");
        request.Headers.TryAddWithoutValidation("country", "co");
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", credentials.SubscriptionKey);
        request.Headers.TryAddWithoutValidation("ApplicationKey", credentials.ApplicationKey);
        request.Headers.TryAddWithoutValidation("ApplicationToken", credentials.ApplicationToken);
    }

    private async Task<(JsonElement Root, int Status)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        HttpResponseMessage response;
        string body;
        try
        {
            using (request)
            {
                response = await _http.SendAsync(request, timeout.Token);
                body = await response.Content.ReadAsStringAsync(timeout.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SistecreditoGatewayException("Sin conexión con Sistecrédito.", SistecreditoFailure.Network, innerException: ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var root = TryParse(body);
            var code = root is { } parsed ? ErrorCode(parsed) : null;
            if (!response.IsSuccessStatusCode || code is not null)
            {
                var fallback = root is { } withMessage && Text(withMessage, "message") is { } message
                    ? $"Sistecrédito respondió: {message}"
                    : $"Sistecrédito no está respondiendo bien en este momento (código {status}). Intente de nuevo o elija otro medio de pago.";
                throw new SistecreditoGatewayException(ErrorMessageFor(code, fallback), SistecreditoFailure.Provider, code);
            }

            return root is { } ok
                ? (ok, status)
                : throw new SistecreditoGatewayException("Sistecrédito envió una respuesta que no se entiende: intente de nuevo.", SistecreditoFailure.Provider);
        }
    }

    private static JsonElement? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>errorCode</c> distinto de cero (Sistecrédito responde <c>errorCode: 0</c> en los éxitos).</summary>
    private static int? ErrorCode(JsonElement root) => Property(root, "errorCode") is { } value && Number(value) is { } code && code != 0 ? code : null;

    private static JsonElement? Property(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : property.Value;
            }
        }

        return null;
    }

    private static string? Text(JsonElement? element, string name) => Property(element, name) switch
    {
        { ValueKind: JsonValueKind.String } value when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString()!.Trim(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        _ => null,
    };

    private static int? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    /// <summary>Cuotas, si Sistecrédito las informa (el nombre del campo no está documentado: se buscan los nombres usuales).</summary>
    private static int? Installments(JsonElement? element) =>
        InstallmentKeys.Select(key => Property(element, key) is { } value ? Number(value) : null).FirstOrDefault(n => n is > 0);
}
