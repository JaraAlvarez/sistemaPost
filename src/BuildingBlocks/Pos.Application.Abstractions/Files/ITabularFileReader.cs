using Pos.SharedKernel.Results;
using Pos.SharedKernel.Text;

namespace Pos.Application.Abstractions.Files;

/// <summary>
/// Contenido de una hoja de cálculo o CSV: encabezados normalizados (minúsculas, sin tildes, espacios como "_") y filas
/// con el texto de cada celda. <see cref="TabularRow.Number"/> es el número de fila tal como lo ve el usuario en Excel
/// (el encabezado es la fila 1).
/// </summary>
public sealed record TabularData(IReadOnlyList<string> Headers, IReadOnlyList<TabularRow> Rows);

public sealed record TabularRow(int Number, IReadOnlyDictionary<string, string> Cells)
{
    /// <summary>Celda recortada, o <c>null</c> si la columna no existe o está vacía.</summary>
    public string? Get(string header) =>
        Cells.TryGetValue(header, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>Lee archivos CSV (UTF-8 o Windows-1252, separador ";" o ",") y Excel (.xlsx, primera hoja).</summary>
public interface ITabularFileReader
{
    Task<Result<TabularData>> ReadAsync(Stream content, string fileName, int maxRows, CancellationToken cancellationToken);

    /// <summary>Normaliza un encabezado como lo hace el lector (para comparar nombres de columna).</summary>
    static string NormalizeHeader(string header) => TextNormalization.ForHeader(header);
}
