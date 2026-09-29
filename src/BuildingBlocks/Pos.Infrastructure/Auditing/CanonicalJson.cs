using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pos.Infrastructure.Auditing;

/// <summary>
/// Serialización canónica JSON según RFC 8785 (JCS), restringida a lo que usa la auditoría: objetos (claves ordenadas
/// por unidades UTF-16), arreglos, cadenas, enteros, booleanos y null. Los decimales y fechas se representan como
/// cadenas ANTES de llegar aquí, así el hash nunca depende del formato de números de punto flotante.
/// Un mismo contenido produce siempre los mismos bytes, lo lea .NET o lo devuelva el jsonb de PostgreSQL.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    public static string Serialize(JsonDocument? document) =>
        document is null ? "null" : Serialize(JsonNode.Parse(document.RootElement.GetRawText()));

    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;

            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(builder, property.Key);
                    builder.Append(':');
                    Write(builder, property.Value);
                }

                builder.Append('}');
                break;

            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(builder, array[i]);
                }

                builder.Append(']');
                break;

            case JsonValue value:
                WriteValue(builder, value);
                break;
        }
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(builder, value.GetValue<string>());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            case JsonValueKind.Number when TryGetInteger(value, out var integer):
                builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                throw new NotSupportedException(
                    "La auditoría solo admite números enteros; los decimales deben registrarse como texto.");
        }
    }

    /// <summary>Entero de cualquier tipo de origen (short, int, long o número leído de JSON), sin parte decimal.</summary>
    private static bool TryGetInteger(JsonValue value, out long integer)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.TryGetInt64(out integer);
    }

    /// <summary>Escapes mínimos de JCS: comillas, barra invertida y caracteres de control; el resto va literal.</summary>
    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
