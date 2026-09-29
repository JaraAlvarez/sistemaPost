using System.Globalization;
using System.Text;
using MiniExcelLibs;
using Pos.Application.Abstractions.Files;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Text;

namespace Pos.Infrastructure.Files;

/// <summary>
/// Lector de CSV y Excel para las importaciones. El CSV se acepta como lo guarda Excel en español: separador ";" (o ","),
/// UTF-8 con o sin BOM o, si no es UTF-8 válido, Windows-1252/Latin-1. El Excel se lee con MiniExcel (Apache-2.0).
/// </summary>
internal sealed class TabularFileReader : ITabularFileReader
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly char[] CsvSeparators = [';', ',', (char)9];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<Result<TabularData>> ReadAsync(Stream content, string fileName, int maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var extension = Path.GetExtension(fileName ?? string.Empty).ToUpperInvariant();
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            return Error.Validation("FILES.EMPTY", "El archivo está vacío.");
        }

        if (buffer.Length > MaxFileBytes)
        {
            return Error.Validation("FILES.TOO_LARGE", "El archivo supera el tamaño máximo de 10 MB.");
        }

        buffer.Position = 0;
        List<string[]> raw;
        try
        {
            raw = extension switch
            {
                ".XLSX" => ReadExcel(buffer),
                ".CSV" or ".TXT" => ReadCsv(Decode(buffer.ToArray())),
                _ => throw new NotSupportedException(),
            };
        }
        catch (NotSupportedException)
        {
            return Error.Validation("FILES.UNSUPPORTED_FORMAT", "Formato no admitido: use un archivo .xlsx o .csv.");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or FormatException)
        {
            return Error.Validation("FILES.UNREADABLE", "No se pudo leer el archivo: verifique que sea un Excel (.xlsx) o CSV válido.");
        }

        return Build(raw, maxRows);
    }

    private static Result<TabularData> Build(List<string[]> raw, int maxRows)
    {
        var headerIndex = raw.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (headerIndex < 0)
        {
            return Error.Validation("FILES.EMPTY", "El archivo no tiene encabezados ni filas.");
        }

        var headers = raw[headerIndex].Select(TextNormalization.ForHeader).ToArray();
        var duplicated = headers.Where(h => h.Length > 0).GroupBy(h => h, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            return Error.Validation("FILES.DUPLICATED_COLUMN", $"La columna '{duplicated.Key}' aparece más de una vez.");
        }

        var rows = new List<TabularRow>();
        for (var i = headerIndex + 1; i < raw.Count; i++)
        {
            var cells = raw[i];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (rows.Count == maxRows)
            {
                return Error.Validation("FILES.TOO_MANY_ROWS", $"El archivo supera el máximo de {maxRows} filas por importación: divídalo en varios archivos.");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < headers.Length; c++)
            {
                if (headers[c].Length > 0)
                {
                    values[headers[c]] = c < cells.Length ? cells[c] : string.Empty;
                }
            }

            rows.Add(new TabularRow(i + 1, values));
        }

        return new TabularData(headers.Where(h => h.Length > 0).ToList(), rows);
    }

    private static List<string[]> ReadExcel(Stream stream)
    {
        var result = new List<string[]>();
        foreach (IDictionary<string, object?> row in stream.Query(useHeaderRow: false, excelType: ExcelType.XLSX))
        {
            result.Add(row.Values.Select(CellText).ToArray());
        }

        return result;
    }

    private static string CellText(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        double d => d.ToString("0.############", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "SI" : "NO",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    internal static string Decode(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            span = span[3..];
        }

        try
        {
            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            // Excel en español guarda "CSV (delimitado por comas)" en Windows-1252; para letras del español coincide con Latin-1.
            return Encoding.Latin1.GetString(span);
        }
    }

    /// <summary>CSV RFC 4180 con separador detectado en el encabezado (";" si aparece más que ",").</summary>
    internal static List<string[]> ReadCsv(string text)
    {
        var firstLineEnd = text.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd < 0 ? text : text[..firstLineEnd];
        var separator = CsvSeparators.OrderByDescending(s => firstLine.Count(c => c == s)).First();

        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (c == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString());
                field.Clear();
                rows.Add([.. fields]);
                fields.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (inQuotes)
        {
            throw new FormatException("Comillas sin cerrar.");
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add([.. fields]);
        }

        return rows;
    }
}
