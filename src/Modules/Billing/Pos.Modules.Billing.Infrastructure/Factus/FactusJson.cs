using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>Opciones JSON de Factus: snake_case, sin nulos (un objeto opcional enviado vuelve obligatorios sus campos).</summary>
public static class FactusJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            // Cuerpo de una petición HTTP (no HTML): tildes y eñes sin escapar, legibles en la evidencia guardada.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Texto con dos decimales y punto decimal ("10000.00"); falla si el valor tiene más de dos decimales.</summary>
    internal static string FormatAmount(decimal value)
    {
        if (decimal.Round(value, 2) != value)
            throw new JsonException($"Factus admite máximo dos decimales; el mapeo debe redondear antes de enviar ({value.ToString(CultureInfo.InvariantCulture)}).");
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    internal static decimal ParseAmount(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.GetDecimal(),
        JsonTokenType.String => decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
        _ => throw new JsonException("Se esperaba un valor numérico."),
    };
}

/// <summary>Montos, cantidades y tarifas: Factus los recibe como texto con máximo dos decimales.</summary>
public sealed class FactusDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        FactusJson.ParseAmount(ref reader);

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(FactusJson.FormatAmount(value));
    }
}

/// <summary>Versión anulable de <see cref="FactusDecimalConverter"/> (los nulos se omiten).</summary>
public sealed class FactusNullableDecimalConverter : JsonConverter<decimal?>
{
    public override bool HandleNull => false;

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : FactusJson.ParseAmount(ref reader);

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is { } amount)
            writer.WriteStringValue(FactusJson.FormatAmount(amount));
        else
            writer.WriteNullValue();
    }
}
