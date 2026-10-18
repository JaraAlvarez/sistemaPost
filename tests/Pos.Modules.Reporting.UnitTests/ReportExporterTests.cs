using System.Text;
using Pos.Modules.Reporting.Application;
using Pos.Modules.Reporting.Contracts;
using Pos.Modules.Reporting.Infrastructure;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Reporting.UnitTests;

/// <summary>Los archivos de los reportes de análisis se generan (Excel, CSV y PDF ancho con letra reducida).</summary>
public class ReportExporterTests
{
    private static ReportDocument Comparison()
    {
        var definition = ReportCatalog.Find("COMPARE_BY_DAY")!;
        var columns = definition.VisibleColumns(includeCost: true);
        var row = columns.Select(c => c.Type switch
        {
            ReportColumnType.Date => (object?)new DateOnly(2026, 10, 17),
            ReportColumnType.Text => "sábado",
            ReportColumnType.Count => 12L,
            _ => (object?)1234.56m,
        }).ToArray();
        var totals = columns.Select(c => c.HasTotal && c.Type != ReportColumnType.Text && c.Type != ReportColumnType.Date ? (object?)99m : null).ToArray();
        return new ReportDocument(definition.Code, definition.Name, definition.Group, new ReportHeader("Tienda", "900123456-7", "01 · Principal"),
            [new("Desde", "2026-10-11"), new("Periodo anterior", "2026-10-04 a 2026-10-10")], columns, [row, row], totals, false, definition.Note,
            new DateTimeOffset(2026, 10, 17, 17, 0, 0, TimeSpan.Zero));
    }

    private static ReportExporter Exporter() => new(new FixedClock());

    [Fact]
    public void El_PDF_del_comparativo_se_genera()
    {
        var file = Exporter().Export(Comparison(), ReportFormat.Pdf);
        file.ContentType.ShouldBe("application/pdf");
        file.FileName.ShouldStartWith("compare_by_day_");
        Encoding.ASCII.GetString(file.Content, 0, 5).ShouldBe("%PDF-");
        file.Rows.ShouldBe(2);
    }

    [Fact]
    public void El_Excel_del_comparativo_se_genera()
    {
        var file = Exporter().Export(Comparison(), ReportFormat.Xlsx);
        file.FileName.ShouldEndWith(".xlsx");
        // Un .xlsx es un ZIP: empieza con "PK".
        file.Content[0].ShouldBe((byte)'P');
        file.Content[1].ShouldBe((byte)'K');
        file.Content.Length.ShouldBeGreaterThan(1000);
    }

    [Fact]
    public void El_CSV_lleva_los_encabezados_del_comparativo()
    {
        var text = Encoding.UTF8.GetString(Exporter().Export(Comparison(), ReportFormat.Csv).Content);
        text.ShouldContain("Var. % venta vs. anterior");
        text.ShouldContain("Var. pp margen vs. año pasado");
    }

    [Theory]
    [InlineData(5, 8)]
    [InlineData(12, 8)]
    [InlineData(13, 6)]
    [InlineData(15, 6)]
    [InlineData(16, 5)]
    [InlineData(22, 5)]
    public void Con_mas_de_15_columnas_la_letra_del_PDF_baja(int columns, double size) =>
        ReportExporter.FontSize(columns).ShouldBe(size);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 17, 17, 0, 0, TimeSpan.Zero);

        public TimeZoneInfo BusinessTimeZone => BusinessTimeZones.Colombia;
    }
}
