using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pos.Cloud.Licensing.Domain;

namespace Pos.Cloud.Licensing.Application;

/// <summary>
/// Reglas del protocolo de Wompi sin red (https://docs.wompi.co): firma de integridad del Web Checkout, verificación del checksum
/// de los eventos y lectura de transacciones. Funciones puras: se prueban sin servidor.
/// </summary>
public static class WompiProtocol
{
    public const string TransactionUpdated = "transaction.updated";
    public const string ChecksumHeader = "X-Event-Checksum";

    /// <summary>
    /// Firma de integridad: SHA-256 en hexadecimal (minúsculas) de <c>referencia + monto en centavos + moneda [+ expiración] +
    /// secreto de integridad</c>.
    /// </summary>
    public static string IntegritySignature(string reference, long amountInCents, string currency, string integritySecret, string? expirationTime = null) =>
        Sha256Hex(string.Concat(reference, amountInCents.ToString(CultureInfo.InvariantCulture), currency, expirationTime ?? string.Empty, integritySecret));

    /// <summary>
    /// Redirección al Web Checkout: <c>…/p/?public-key=…&amp;currency=COP&amp;amount-in-cents=…&amp;reference=…&amp;signature:integrity=…&amp;redirect-url=…</c>
    /// y, si la orden vence, <c>&amp;expiration-time=…</c> (ISO 8601 UTC), que también entra en la firma de integridad.
    /// </summary>
    public static string CheckoutUrl(string checkoutUrl, string publicKey, string integritySecret, CheckoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expiration = request.ExpiresAt is { } expires ? ExpirationTime(expires) : null;
        var signature = IntegritySignature(request.Reference, request.AmountInCents, request.Currency, integritySecret, expiration);
        var separator = checkoutUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{checkoutUrl}{separator}public-key={Uri.EscapeDataString(publicKey)}&currency={Uri.EscapeDataString(request.Currency)}" +
            $"&amount-in-cents={request.AmountInCents}&reference={Uri.EscapeDataString(request.Reference)}" +
            $"&signature:integrity={signature}&redirect-url={Uri.EscapeDataString(request.RedirectUrl)}");
        return expiration is null ? url : $"{url}&expiration-time={Uri.EscapeDataString(expiration)}";
    }

    /// <summary>Fecha de expiración del checkout en el formato de Wompi: ISO 8601 en UTC con milisegundos (<c>2026-10-17T20:28:50.000Z</c>).</summary>
    public static string ExpirationTime(DateTimeOffset expiresAt) =>
        expiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Verifica un evento: concatena los valores de <c>signature.properties</c> (rutas dentro de <c>data</c>, en su orden), el
    /// <c>timestamp</c> y el secreto de eventos; el SHA-256 en hexadecimal debe coincidir (sin distinguir mayúsculas, en tiempo
    /// constante) con <c>signature.checksum</c> o, si falta, con la cabecera <c>X-Event-Checksum</c>.
    /// </summary>
    public static GatewayEvent ParseEvent(string body, string? checksumHeader, string eventsSecret)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body ?? string.Empty, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return new GatewayEvent(false, null, null, null, "El cuerpo no es JSON válido.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new GatewayEvent(false, null, null, null, "El cuerpo no es un objeto JSON.");
            }

            var eventType = Text(root, "event");
            var environment = Text(root, "environment");
            var timestamp = Timestamp(root);
            var transaction = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("transaction", out var tx) && tx.ValueKind == JsonValueKind.Object
                    ? ReadTransaction(tx)
                    : null;

            var expected = ComputeChecksum(root, eventsSecret, out var problem);
            if (expected is null)
            {
                return new GatewayEvent(false, eventType, transaction, environment, problem, timestamp);
            }

            var received = root.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.Object
                ? Text(signature, "checksum")
                : null;
            received ??= checksumHeader;
            if (string.IsNullOrWhiteSpace(received))
            {
                return new GatewayEvent(false, eventType, transaction, environment, "El evento no trae checksum.", timestamp);
            }

            return SameHex(expected, received.Trim())
                ? new GatewayEvent(true, eventType, transaction, environment, null, timestamp)
                : new GatewayEvent(false, eventType, transaction, environment, "El checksum no coincide.", timestamp);
        }
    }

    /// <summary>Checksum esperado del evento, o nulo si le faltan la firma, sus propiedades o el timestamp.</summary>
    public static string? ComputeChecksum(JsonElement root, string eventsSecret, out string? problem)
    {
        problem = null;
        if (!root.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.Object
            || !signature.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array
            || properties.GetArrayLength() is 0 or > 20)
        {
            problem = "El evento no trae signature.properties.";
            return null;
        }

        if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
        {
            problem = "El evento no trae timestamp.";
            return null;
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            problem = "El evento no trae data.";
            return null;
        }

        var builder = new StringBuilder();
        foreach (var property in properties.EnumerateArray())
        {
            if (property.ValueKind != JsonValueKind.String || !TryResolve(data, property.GetString()!, out var value))
            {
                problem = "Una propiedad firmada no existe en data.";
                return null;
            }

            builder.Append(value);
        }

        builder.Append(timestamp.ValueKind == JsonValueKind.String ? timestamp.GetString() : timestamp.GetRawText());
        builder.Append(eventsSecret);
        return Sha256Hex(builder.ToString());
    }

    /// <summary>Transacción del JSON de Wompi (evento o respuesta de la API).</summary>
    public static GatewayTransaction? ReadTransaction(JsonElement element)
    {
        var id = Text(element, "id");
        var reference = Text(element, "reference");
        var status = Text(element, "status");
        if (id is null || reference is null || status is null
            || !element.TryGetProperty("amount_in_cents", out var amount) || amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt64(out var cents))
        {
            return null;
        }

        return new GatewayTransaction(id, reference, cents, Text(element, "currency") ?? string.Empty, status.ToUpperInvariant(), Text(element, "payment_method_type"));
    }

    /// <summary>
    /// ¿El evento está dentro de la ventana de tiempo admitida? (antirrepetición: el <c>timestamp</c> va firmado). Sin timestamp legible: no.
    /// </summary>
    public static bool IsFresh(DateTimeOffset? timestamp, DateTimeOffset now, TimeSpan maxAge, TimeSpan maxFuture) =>
        timestamp is { } at && at >= now - maxAge && at <= now + maxFuture;

    /// <summary><c>timestamp</c> del evento: segundos Unix (número o texto).</summary>
    private static DateTimeOffset? Timestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var value))
        {
            return null;
        }

        long seconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            seconds = number;
        }
        else if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            seconds = parsed;
        }
        else
        {
            return null;
        }

        return seconds is > 0 and < 253_402_300_799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Compara dos hexadecimales sin distinguir mayúsculas y en tiempo constante.</summary>
    public static bool SameHex(string expected, string received)
    {
        var a = Encoding.ASCII.GetBytes(expected.ToLowerInvariant());
        var b = Encoding.ASCII.GetBytes(received.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool TryResolve(JsonElement data, string path, out string value)
    {
        value = string.Empty;
        var current = data;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        value = current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => current.GetRawText(),
        };
        return true;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
