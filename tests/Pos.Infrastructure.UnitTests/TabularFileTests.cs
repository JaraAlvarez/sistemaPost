using System.Text;
using MiniExcelLibs;
using Pos.Application.Abstractions.Files;
using Pos.Infrastructure.Files;
using Pos.SharedKernel.Text;

namespace Pos.Infrastructure.UnitTests;

public class TabularFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryStream Utf8(string text, bool bom = false) =>
        new([.. (bom ? Encoding.UTF8.GetPreamble() : []), .. Encoding.UTF8.GetBytes(text)]);

    [Fact]
    public async Task CSV_de_Excel_en_espanol_con_punto_y_coma_BOM_y_comillas()
    {
        var csv = "SKU;Nombre;Categoría;Precio\r\nLECHE-1;\"Leche; entera \"\"UHT\"\"\";Lácteos > Leches;4.980\r\n\r\n;Tomate;Fruver;1,25\r\n";
        var result = await new TabularFileReader().ReadAsync(Utf8(csv, bom: true), "productos.csv", 100, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Headers.ShouldBe(["sku", "nombre", "categoria", "precio"]);
        result.Value.Rows.Count.ShouldBe(2);
        result.Value.Rows[0].Get("nombre").ShouldBe("Leche; entera \"UHT\"");
        result.Value.Rows[0].Number.ShouldBe(2);
        result.Value.Rows[1].Number.ShouldBe(4);
        result.Value.Rows[1].Get("sku").ShouldBeNull();
        result.Value.Rows[1].Get("no_existe").ShouldBeNull();
    }

    [Fact]
    public async Task CSV_con_coma_y_en_Windows_1252()
    {
        var bytes = Encoding.Latin1.GetBytes("codigo,nombre\n1,Café Águila\n");
        var result = await new TabularFileReader().ReadAsync(new MemoryStream(bytes), "p.CSV", 100, Ct);

        result.Value.Rows[0].Get("nombre").ShouldBe("Café Águila");
    }

    [Fact]
    public async Task Excel_xlsx_primera_hoja()
    {
        using var stream = new MemoryStream();
        await stream.SaveAsAsync(
            new[]
            {
                new Dictionary<string, object> { ["SKU"] = "A-1", ["Precio"] = 4980, ["Desde"] = new DateTime(2026, 10, 6) },
                new Dictionary<string, object> { ["SKU"] = "A-2", ["Precio"] = 1.25m, ["Desde"] = new DateTime(2026, 10, 6, 8, 30, 0) },
            },
            cancellationToken: Ct);
        stream.Position = 0;

        var result = await new TabularFileReader().ReadAsync(stream, "precios.xlsx", 100, Ct);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.Rows[0].Get("precio").ShouldBe("4980");
        result.Value.Rows[0].Get("desde").ShouldBe("2026-10-06");
        result.Value.Rows[1].Get("precio").ShouldBe("1.25");
        result.Value.Rows[1].Get("desde").ShouldBe("2026-10-06T08:30:00");
    }

    [Theory]
    [InlineData("", "archivo.csv", "FILES.EMPTY")]
    [InlineData("a;b\n1;2", "archivo.pdf", "FILES.UNSUPPORTED_FORMAT")]
    [InlineData("a;a\n1;2", "archivo.csv", "FILES.DUPLICATED_COLUMN")]
    [InlineData("a\n\"sin cerrar", "archivo.csv", "FILES.UNREADABLE")]
    [InlineData("a\n1\n2\n3", "archivo.csv", "FILES.TOO_MANY_ROWS")]
    [InlineData("\n\n", "archivo.csv", "FILES.EMPTY")]
    public async Task Archivos_invalidos(string content, string name, string code)
    {
        var result = await new TabularFileReader().ReadAsync(Utf8(content), name, 2, Ct);

        result.Error.Code.ShouldBe(code);
    }

    [Fact]
    public async Task Un_xlsx_danado_no_rompe_el_servidor()
    {
        var result = await new TabularFileReader().ReadAsync(Utf8("no es un zip"), "a.xlsx", 10, Ct);

        result.Error.Code.ShouldBe("FILES.UNREADABLE");
    }

    [Theory]
    [InlineData("4.980", 4980)]
    [InlineData("$ 1.250.000", 1250000)]
    [InlineData("1,25", 1.25)]
    [InlineData("0.5", 0.5)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("-3", -3)]
    [InlineData("12.75", 12.75)]
    public void Numeros_escritos_en_Colombia(string text, double expected)
    {
        DecimalParsing.TryParse(text, out var value).ShouldBeTrue();
        value.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("$")]
    [InlineData("abc")]
    public void Textos_que_no_son_numeros(string? text) => DecimalParsing.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Normalizacion_para_busquedas_y_encabezados()
    {
        TextNormalization.ForSearch("  Café   ÁGUILA  Roja ").ShouldBe("cafe aguila roja");
        TextNormalization.ForSearch(null).ShouldBe(string.Empty);
        TextNormalization.ForHeader("Código de barras").ShouldBe("codigo_de_barras");
        TextNormalization.ForHeader("Vigente-desde").ShouldBe("vigente_desde");
        ITabularFileReader.NormalizeHeader("Unidad Contenido").ShouldBe("unidad_contenido");
    }
}
