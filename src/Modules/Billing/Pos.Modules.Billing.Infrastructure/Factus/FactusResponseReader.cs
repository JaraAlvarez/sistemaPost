using System.Globalization;
using System.Text.Json;

namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>
/// Lectura tolerante de las respuestas de Factus. La documentación v2 describe los campos de la creación
/// (<c>{status, message, data:{reference_code, number, is_validated, validated_at, errors{}, cufe, links{qr, public_url},
/// totals{…}}}</c>) pero no publica ejemplos completos de consulta, listado ni rangos. SUPUESTOS (a confirmar en sandbox):
/// la consulta puede anidar el documento (<c>data.bill</c>, como en v1), el listado es paginado (<c>data.data[]</c>) y
/// las notas/documentos soporte pueden traer <c>cude</c>/<c>cuds</c> en vez de <c>cufe</c>. Por eso se lee con
/// <see cref="JsonElement"/> y alternativas en lugar de DTOs rígidos.
/// </summary>
internal static class FactusResponseReader
{
    private static readonly string[] NestedDocumentKeys = ["bill", "credit_note", "support_document", "adjustment_note", "document"];
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "yyyy/MM/dd"];

    /// <summary>JSON de la respuesta, o <c>null</c> si está vacía o no es JSON.</summary>
    public static JsonDocument? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>El objeto <c>data</c> (o la raíz si no existe).</summary>
    public static JsonElement Data(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? data
            : root;

    /// <summary>Documento dentro de una respuesta de creación o consulta.</summary>
    public static FactusDocument? ReadDocument(FactusDocumentKind kind, JsonElement root)
    {
        var element = Data(root);
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        // Solo se desciende si el propio data no es el documento: la respuesta de una nota trae ADEMÁS el documento que corrige
        // (p. ej. la nota de ajuste incluye "support_document": {reference_code, number, cuds}) y no debe confundirse con ella.
        foreach (var key in element.TryGetProperty("number", out _) || element.TryGetProperty("reference_code", out _) ? [] : NestedDocumentKeys)
        {
            if (element.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                element = nested;
                break;
            }
        }

        return LooksLikeDocument(element) ? ToDocument(kind, element) : null;
    }

    /// <summary>Elementos de un listado (paginado <c>data.data[]</c> o arreglo directo <c>data[]</c>).</summary>
    public static IEnumerable<JsonElement> ReadList(JsonElement root)
    {
        var data = Data(root);
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var page) && page.ValueKind == JsonValueKind.Array)
            data = page;
        return data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];
    }

    public static bool LooksLikeDocument(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && (element.TryGetProperty("number", out _) || element.TryGetProperty("reference_code", out _) || element.TryGetProperty("cufe", out _));

    public static FactusDocument ToDocument(FactusDocumentKind kind, JsonElement element)
    {
        var links = element.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
        var totals = element.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        var validatedAt = String(element, "validated_at");

        return new FactusDocument
        {
            Kind = kind,
            ReferenceCode = String(element, "reference_code"),
            Number = String(element, "number"),
            Cufe = String(element, "cufe") ?? String(element, "cude") ?? String(element, "cuds"),
            IsValidated = Bool(element, "is_validated") ?? Bool(element, "status") ?? false,
            ValidatedAt = validatedAt,
            QrUrl = (links.ValueKind == JsonValueKind.Object ? String(links, "qr") : null) ?? String(element, "qr"),
            PublicUrl = links.ValueKind == JsonValueKind.Object ? String(links, "public_url") : null,
            Total = (totals.ValueKind == JsonValueKind.Object ? Decimal(totals, "total") : null) ?? Decimal(element, "total"),
            DianMessages = element.TryGetProperty("errors", out var errors) ? Messages(errors) : [],
            RawJson = element.GetRawText(),
        };
    }

    /// <summary>Errores de validación (Laravel: <c>{message, errors:{campo:[…]}}</c>, o dentro de <c>data</c>).</summary>
    public static List<FactusMessage> ValidationMessages(JsonElement root)
    {
        var result = new List<FactusMessage>();
        if (root.ValueKind != JsonValueKind.Object)
            return result;

        if (root.TryGetProperty("errors", out var errors))
            result.AddRange(Messages(errors));
        else if (Data(root) is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("errors", out var dataErrors))
            result.AddRange(Messages(dataErrors));

        if (result.Count == 0 && String(root, "message") is { } message)
            result.Add(new FactusMessage("message", message));
        return result;
    }

    public static string? Message(JsonElement root) => root.ValueKind == JsonValueKind.Object ? String(root, "message") : null;

    public static IReadOnlyList<FactusMessage> Messages(JsonElement errors)
    {
        var result = new List<FactusMessage>();
        switch (errors.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in errors.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                        result.AddRange(property.Value.EnumerateArray().Select(v => new FactusMessage(property.Name, Text(v))));
                    else
                        result.Add(new FactusMessage(property.Name, Text(property.Value)));
                }
                break;
            case JsonValueKind.Array:
                result.AddRange(errors.EnumerateArray().Select(v => new FactusMessage(RuleOf(Text(v)), Text(v))));
                break;
            case JsonValueKind.String:
                result.Add(new FactusMessage(RuleOf(errors.GetString()!), errors.GetString()!));
                break;
        }
        return result;
    }

    public static FactusNumberingRange? ToRange(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || Long(element, "id") is not { } id)
            return null;

        return new FactusNumberingRange
        {
            Id = (int)id,
            Document = String(element, "document"),
            Prefix = String(element, "prefix"),
            From = Long(element, "from") ?? 0,
            To = Long(element, "to") ?? 0,
            Current = Long(element, "current") ?? 0,
            ResolutionNumber = String(element, "resolution_number"),
            StartDate = Date(element, "start_date"),
            EndDate = Date(element, "end_date"),
            TechnicalKey = String(element, "technical_key"),
            IsExpired = Bool(element, "is_expired") ?? false,
            IsActive = Bool(element, "is_active") ?? true,
        };
    }

    public static string? String(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static bool? Bool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var n) ? n == 1 : null,
            JsonValueKind.String => value.GetString() switch
            {
                "1" or "true" or "True" => true,
                "0" or "false" or "False" => false,
                _ => null,
            },
            _ => null,
        };
    }

    private static long? Long(JsonElement element, string name) =>
        String(element, name) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static decimal? Decimal(JsonElement element, string name) =>
        String(element, name) is { } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static DateOnly? Date(JsonElement element, string name)
    {
        if (String(element, name) is not { } text)
            return null;
        var datePart = text.Length > 10 ? text[..10] : text;
        return DateOnly.TryParseExact(datePart, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>"Regla: FAK24, Rechazo: …" → "FAK24".</summary>
    private static string RuleOf(string message)
    {
        const string prefix = "Regla:";
        var start = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return "dian";
        var rest = message[(start + prefix.Length)..].TrimStart();
        var end = rest.IndexOfAny([',', ' ']);
        return end > 0 ? rest[..end] : rest;
    }
}
