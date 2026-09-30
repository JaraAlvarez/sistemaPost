using System.Globalization;
using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using MiniExcelLibs;
using Pos.Infrastructure.Files;
using Pos.Modules.Reporting.Application;
using Pos.Modules.Reporting.Contracts;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Reporting.Infrastructure;

/// <summary>
/// Exportación de reportes (D9-09): Excel con MiniExcel (Apache-2.0), CSV nativo (separador ';', UTF-8 con BOM, coma decimal para Excel en
/// español) y PDF con PDFsharp-MigraDoc (MIT): tabla sencilla con el encabezado de la empresa, los parámetros, la fecha de generación y los
/// totales (pregunta 2 de la propuesta).
/// </summary>
internal sealed class ReportExporter(IClock clock) : IReportExporter
{
    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    public ReportFileDto Export(ReportDocument document, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(document);
        var stamp = clock.ToBusinessTime(document.GeneratedAt).ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
        var name = $"{document.Code.ToLowerInvariant()}_{stamp}";
        return format switch
        {
            ReportFormat.Csv => new($"{name}.csv", "text/csv; charset=utf-8", Csv(document), document.Rows.Count, document.Truncated),
            ReportFormat.Xlsx => new(
                $"{name}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Xlsx(document), document.Rows.Count, document.Truncated),
            ReportFormat.Pdf => new($"{name}.pdf", "application/pdf", Pdf(document), document.Rows.Count, document.Truncated),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato de archivo no soportado."),
        };
    }

    // ------------------------------------------------------------------------------------------------ CSV

    private byte[] Csv(ReportDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(';', document.Columns.Select(c => Quote(c.Label))));
        foreach (var row in document.Rows)
        {
            builder.AppendLine(string.Join(';', row.Select((v, i) => Quote(CsvValue(v, document.Columns[i].Type)))));
        }

        if (document.Columns.Any(c => c.HasTotal))
        {
            builder.AppendLine(string.Join(';', document.Totals.Select((v, i) => Quote(i == 0 && v is null ? "TOTAL" : CsvValue(v, document.Columns[i].Type)))));
        }

        if (document.Truncated)
        {
            builder.AppendLine(Quote($"ATENCIÓN: el reporte se cortó en {document.Rows.Count} filas; reduzca el rango."));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    private string CsvValue(object? value, ReportColumnType type) => value switch
    {
        null => string.Empty,
        decimal d => d.ToString(type == ReportColumnType.Quantity ? "0.####" : "0.00", Colombia),
        long l => l.ToString(CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset instant => clock.ToBusinessTime(instant).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "Sí" : "No",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string Quote(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    // ------------------------------------------------------------------------------------------------ Excel

    private byte[] Xlsx(ReportDocument document)
    {
        var rows = document.Rows.Select(r => ToExcelRow(document.Columns, r)).ToList();
        if (document.Columns.Any(c => c.HasTotal))
        {
            var totals = ToExcelRow(document.Columns, document.Totals);
            if (document.Totals[0] is null)
            {
                totals[document.Columns[0].Label] = "TOTAL";
            }

            rows.Add(totals);
        }

        var information = Information(document).Select(p => new Dictionary<string, object?> { ["Campo"] = p.Key, ["Valor"] = p.Value }).ToList();
        var sheets = new Dictionary<string, object> { ["Reporte"] = rows, ["Información"] = information };
        using var stream = new MemoryStream();
        MiniExcel.SaveAs(stream, sheets, excelType: ExcelType.XLSX);
        return stream.ToArray();
    }

    private Dictionary<string, object?> ToExcelRow(IReadOnlyList<ReportColumn> columns, object?[] values)
    {
        var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            row[columns[i].Label] = values[i] switch
            {
                DateOnly date => date.ToDateTime(TimeOnly.MinValue),
                DateTimeOffset instant => clock.ToBusinessTime(instant).DateTime,
                bool b => b ? "Sí" : "No",
                var other => other,
            };
        }

        return row;
    }

    private IEnumerable<KeyValuePair<string, string>> Information(ReportDocument document)
    {
        yield return new("Empresa", document.Header.CompanyName);
        yield return new("Identificación", document.Header.CompanyIdentification);
        yield return new("Sucursal", document.Header.BranchName);
        yield return new("Reporte", $"{document.Title} ({document.Code})");
        yield return new("Grupo", document.Group);
        yield return new("Generado", clock.ToBusinessTime(document.GeneratedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        foreach (var parameter in document.Parameters)
        {
            yield return parameter;
        }

        yield return new("Filas", document.Rows.Count.ToString(CultureInfo.InvariantCulture));
        if (document.Truncated)
        {
            yield return new("Atención", "El reporte se cortó en el máximo de filas permitido; reduzca el rango.");
        }

        if (document.Note is { } note)
        {
            yield return new("Nota", note);
        }
    }

    // ------------------------------------------------------------------------------------------------ PDF

    private byte[] Pdf(ReportDocument document)
    {
        PdfFonts.EnsureConfigured();
        var pdf = new Document();
        pdf.Info.Title = document.Title;
        var normal = pdf.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.Family;
        normal.Font.Size = document.Columns.Count > 12 ? 6 : 8;

        var section = pdf.AddSection();
        section.PageSetup = pdf.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.Letter;
        section.PageSetup.Orientation = document.Columns.Count > 5 ? Orientation.Landscape : Orientation.Portrait;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.2);
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(1.2);

        var title = section.AddParagraph(document.Header.CompanyName);
        title.Format.Font.Size = 12;
        title.Format.Font.Bold = true;
        section.AddParagraph($"{document.Header.CompanyIdentification} · {document.Header.BranchName}");
        var subtitle = section.AddParagraph(document.Title);
        subtitle.Format.Font.Size = 11;
        subtitle.Format.Font.Bold = true;
        subtitle.Format.SpaceBefore = Unit.FromPoint(4);
        var parameters = string.Join("   ", document.Parameters.Select(p => $"{p.Key}: {p.Value}"));
        section.AddParagraph(
            $"Generado: {clock.ToBusinessTime(document.GeneratedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}   {parameters}   Filas: {document.Rows.Count}");
        if (document.Truncated)
        {
            section.AddParagraph("ATENCIÓN: el reporte se cortó en el máximo de filas permitido; reduzca el rango.").Format.Font.Bold = true;
        }

        var footer = section.Footers.Primary.AddParagraph();
        footer.AddText($"{document.Code} · Página ");
        footer.AddPageField();
        footer.AddText(" de ");
        footer.AddNumPagesField();
        footer.Format.Alignment = ParagraphAlignment.Right;
        footer.Format.Font.Size = 7;

        var available = (section.PageSetup.Orientation == Orientation.Landscape ? 27.94 : 21.59) - 2.4;
        var weights = document.Columns.Select(c => Weight(c.Type)).ToArray();
        var scale = available / weights.Sum();
        var table = section.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = Colors.Gray;
        table.Format.SpaceBefore = Unit.FromPoint(1);
        table.TopPadding = table.BottomPadding = Unit.FromPoint(1);
        foreach (var weight in weights)
        {
            table.AddColumn(Unit.FromCentimeter(weight * scale));
        }

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Format.Font.Bold = true;
        head.Shading.Color = Colors.LightGray;
        for (var i = 0; i < document.Columns.Count; i++)
        {
            head.Cells[i].AddParagraph(document.Columns[i].Label);
        }

        foreach (var values in document.Rows)
        {
            AddRow(table, document.Columns, values, bold: false);
        }

        if (document.Columns.Any(c => c.HasTotal))
        {
            var totals = (object?[])document.Totals.Clone();
            totals[0] ??= "TOTAL";
            AddRow(table, document.Columns, totals, bold: true);
        }

        if (document.Note is { } note)
        {
            var paragraph = section.AddParagraph(note);
            paragraph.Format.SpaceBefore = Unit.FromPoint(6);
            paragraph.Format.Font.Italic = true;
        }

        var renderer = new PdfDocumentRenderer { Document = pdf };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, false);
        return stream.ToArray();
    }

    private void AddRow(Table table, IReadOnlyList<ReportColumn> columns, object?[] values, bool bold)
    {
        var row = table.AddRow();
        row.Format.Font.Bold = bold;
        for (var i = 0; i < columns.Count; i++)
        {
            var paragraph = row.Cells[i].AddParagraph(PdfValue(values[i], columns[i].Type));
            if (columns[i].Type is ReportColumnType.Count or ReportColumnType.Quantity or ReportColumnType.Money or ReportColumnType.Percent)
            {
                paragraph.Format.Alignment = ParagraphAlignment.Right;
            }
        }
    }

    private string PdfValue(object? value, ReportColumnType type) => value switch
    {
        null => string.Empty,
        decimal d when type == ReportColumnType.Percent => d.ToString("N2", Colombia) + " %",
        decimal d when type == ReportColumnType.Quantity => d.ToString("#,##0.####", Colombia),
        decimal d => d.ToString("N2", Colombia),
        long l => l.ToString("N0", Colombia),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset instant => clock.ToBusinessTime(instant).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        bool b => b ? "Sí" : "No",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static double Weight(ReportColumnType type) => type switch
    {
        ReportColumnType.Text => 3.2,
        ReportColumnType.DateTime => 2.4,
        ReportColumnType.Date => 1.8,
        ReportColumnType.Count => 1.3,
        ReportColumnType.Boolean => 1.2,
        _ => 2.0,
    };
}
