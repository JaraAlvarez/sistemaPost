using Pos.Modules.Catalog.Application;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.Printing;

namespace Pos.Modules.Catalog.UnitTests;

/// <summary>Codificación EAN-13, EAN-8 y CODE128 de las etiquetas (tablas escritas aquí aparte, según la norma, para no copiar el código).</summary>
public class BarcodeSymbologyTests
{
    // Norma GS1: juego A (impar), juego B (par) y juego C (derecha) de cada dígito.
    private static readonly string[] SetA = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    private static readonly string[] SetB = ["0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111"];
    private static readonly string[] SetC = ["1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100"];

    [Theory]
    [InlineData("590123412345", '7')]
    [InlineData("400638133393", '1')]
    [InlineData("770217700001", '4')]
    [InlineData("9638507", '4')]
    [InlineData("290010000001", '7')]
    public void Digito_de_control_GTIN(string body, char check) => Barcodes.ComputeCheckDigit(body).ShouldBe(check);

    [Fact]
    public void EAN13_produce_los_95_modulos_de_la_norma()
    {
        // 5 901234 123457: el primer dígito (5) define la paridad LGGLLG de la mitad izquierda.
        var expected = "101" + SetA[9] + SetB[0] + SetB[1] + SetA[2] + SetA[3] + SetB[4] + "01010"
            + SetC[1] + SetC[2] + SetC[3] + SetC[4] + SetC[5] + SetC[7] + "101";

        var drawing = BarcodeSymbology.Encode("5901234123457");

        drawing.ShouldNotBeNull();
        drawing.Symbol.ShouldBe(BarcodeSymbol.Ean13);
        Bits(drawing.Modules).ShouldBe(expected);
        drawing.Modules.Count.ShouldBe(95);
        drawing.TotalModules.ShouldBe(95 + 22);
    }

    [Fact]
    public void EAN13_que_empieza_por_cero_usa_solo_el_juego_A_a_la_izquierda()
    {
        var bits = Bits(BarcodeSymbology.Ean13("0036000291452"));

        bits[3..45].ShouldBe(SetA[0] + SetA[3] + SetA[6] + SetA[0] + SetA[0] + SetA[0]);
    }

    [Fact]
    public void EAN8_produce_67_modulos()
    {
        var expected = "101" + SetA[9] + SetA[6] + SetA[3] + SetA[8] + "01010" + SetC[5] + SetC[0] + SetC[7] + SetC[4] + "101";

        var drawing = BarcodeSymbology.Encode("96385074");

        drawing!.Symbol.ShouldBe(BarcodeSymbol.Ean8);
        Bits(drawing.Modules).ShouldBe(expected);
    }

    [Theory]
    [InlineData("7702177000014", BarcodeSymbol.Ean13, "7702177000014")]
    [InlineData("2900100000017", BarcodeSymbol.Ean13, "2900100000017")]
    [InlineData("036000291452", BarcodeSymbol.Ean13, "0036000291452")]
    [InlineData("96385074", BarcodeSymbol.Ean8, "96385074")]
    [InlineData("7702177000015", BarcodeSymbol.Code128, "7702177000015")]
    [InlineData("001-000123", BarcodeSymbol.Code128, "001-000123")]
    [InlineData(" ARROZ-500 ", BarcodeSymbol.Code128, "ARROZ-500")]
    public void Elige_la_simbologia(string code, BarcodeSymbol symbol, string text)
    {
        var drawing = BarcodeSymbology.Encode(code);

        drawing.ShouldNotBeNull();
        drawing.Symbol.ShouldBe(symbol);
        drawing.Text.ShouldBe(text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("AÑO")]
    public void Sin_texto_o_fuera_de_ASCII_no_hay_codigo(string? code) => BarcodeSymbology.Encode(code).ShouldBeNull();

    [Fact]
    public void La_tabla_CODE128_es_valida()
    {
        var table = BarcodeSymbology.Code128Widths;

        table.Count.ShouldBe(107);
        table.Distinct().Count().ShouldBe(107);
        for (var i = 0; i < table.Count; i++)
        {
            var widths = table[i].Select(c => c - '0').ToArray();
            widths.Sum().ShouldBe(i == BarcodeSymbology.Stop ? 13 : 11, $"símbolo {i}");
            (widths.Where((_, index) => index % 2 == 0).Sum() % 2).ShouldBe(0, $"las barras del símbolo {i} suman par");
        }
    }

    [Fact]
    public void CODE128_B_calcula_el_digito_de_control()
    {
        // Start B (104) + P J J 1 2 3 C: 104 + 48·1 + 42·2 + 42·3 + 17·4 + 18·5 + 19·6 + 35·7 = 879 → 879 mod 103 = 55.
        BarcodeSymbology.Code128Values("PJJ123C").ShouldBe([104, 48, 42, 42, 17, 18, 19, 35, 55, 106]);
    }

    [Fact]
    public void CODE128_usa_el_subconjunto_C_para_los_digitos()
    {
        // (105 + 12·1 + 34·2 + 56·3) mod 103 = 353 mod 103 = 44.
        BarcodeSymbology.Code128Values("123456").ShouldBe([105, 12, 34, 56, 44, 106]);

        var mixed = BarcodeSymbology.Code128Values("AB12345678")!;
        mixed.Take(8).ShouldBe([104, 33, 34, BarcodeSymbology.CodeC, 12, 34, 56, 78]);
        mixed[^1].ShouldBe(BarcodeSymbology.Stop);
    }

    [Theory]
    [InlineData("001-000123")]
    [InlineData("ARROZ-500")]
    [InlineData("12345678")]
    [InlineData("AB12345678x")]
    public void CODE128_se_lee_de_vuelta(string text)
    {
        var drawing = BarcodeSymbology.Encode(text)!;
        var values = BarcodeSymbology.Code128Values(text)!;

        drawing.Modules.Count.ShouldBe((11 * (values.Count - 1)) + 13);
        drawing.Modules[0].ShouldBeTrue();
        drawing.Modules[^1].ShouldBeTrue();
        Decode(drawing.Modules).ShouldBe(text);
    }

    private static string Bits(IReadOnlyList<bool> modules) => new([.. modules.Select(m => m ? '1' : '0')]);

    /// <summary>Lector CODE128 mínimo: de los módulos a los anchos, de los anchos a los valores; verifica el control y arma el texto.</summary>
    private static string Decode(IReadOnlyList<bool> modules)
    {
        var runs = new List<int>();
        var count = 1;
        for (var i = 1; i < modules.Count; i++)
        {
            if (modules[i] == modules[i - 1])
            {
                count++;
            }
            else
            {
                runs.Add(count);
                count = 1;
            }
        }

        runs.Add(count);
        var values = new List<int>();
        for (var i = 0; i + 6 <= runs.Count - 7; i += 6)
        {
            values.Add(BarcodeSymbology.Code128Widths.ToList().IndexOf(string.Concat(runs.Skip(i).Take(6))));
        }

        string.Concat(runs.Skip(runs.Count - 7)).ShouldBe("2331112");
        var checksum = values[0];
        for (var i = 1; i < values.Count - 1; i++)
        {
            checksum += values[i] * i;
        }

        (checksum % 103).ShouldBe(values[^1]);
        var text = new System.Text.StringBuilder();
        var set = values[0] == BarcodeSymbology.StartC ? 'C' : 'B';
        foreach (var value in values.Skip(1).SkipLast(1))
        {
            if (value == BarcodeSymbology.CodeC)
            {
                set = 'C';
            }
            else if (value == BarcodeSymbology.CodeB)
            {
                set = 'B';
            }
            else
            {
                text.Append(set == 'C' ? value.ToString("00", System.Globalization.CultureInfo.InvariantCulture) : ((char)(value + 32)).ToString());
            }
        }

        return text.ToString();
    }
}

/// <summary>Reparto de etiquetas en páginas.</summary>
public class LabelLayoutTests
{
    [Fact]
    public void Las_plantillas_caben_en_su_pagina()
    {
        foreach (var template in LabelLayout.Templates)
        {
            template.Fits.ShouldBeTrue(template.Name);
        }

        LabelLayout.Get(LabelTemplateKind.Shelf3x8).PerPage.ShouldBe(24);
        LabelLayout.Get(LabelTemplateKind.Shelf2x7).PerPage.ShouldBe(14);
        LabelLayout.Get(LabelTemplateKind.Sticker40x25).PerPage.ShouldBe(1);
        LabelLayout.Get(LabelTemplateKind.Sticker50x30).IsSticker.ShouldBeTrue();
    }

    [Theory]
    [InlineData(LabelTemplateKind.Shelf3x8, 0, 1)]
    [InlineData(LabelTemplateKind.Shelf3x8, 1, 1)]
    [InlineData(LabelTemplateKind.Shelf3x8, 24, 1)]
    [InlineData(LabelTemplateKind.Shelf3x8, 25, 2)]
    [InlineData(LabelTemplateKind.Shelf3x8, 48, 2)]
    [InlineData(LabelTemplateKind.Shelf3x8, 49, 3)]
    [InlineData(LabelTemplateKind.Shelf2x7, 15, 2)]
    [InlineData(LabelTemplateKind.Sticker40x25, 5, 5)]
    public void Cuenta_las_paginas(LabelTemplateKind kind, int labels, int pages) =>
        LabelLayout.PageCount(LabelLayout.Get(kind), labels).ShouldBe(pages);

    [Fact]
    public void Llena_por_filas_y_pasa_a_la_hoja_siguiente()
    {
        var template = LabelLayout.Get(LabelTemplateKind.Shelf3x8);

        LabelLayout.Slot(template, 0).ShouldBe(new LabelSlot(0, 0, 0, 6m, 8.5m));
        LabelLayout.Slot(template, 1).ShouldBe(new LabelSlot(0, 1, 0, 72m, 8.5m));
        LabelLayout.Slot(template, 3).ShouldBe(new LabelSlot(0, 0, 1, 6m, 43.5m));
        LabelLayout.Slot(template, 23).ShouldBe(new LabelSlot(0, 2, 7, 138m, 253.5m));
        LabelLayout.Slot(template, 24).ShouldBe(new LabelSlot(1, 0, 0, 6m, 8.5m));
    }

    [Theory]
    [InlineData(LabelTemplateKind.Shelf3x8, "7702177000014", "0.25")]
    [InlineData(LabelTemplateKind.Shelf2x7, "7702177000014", "0.375")]
    [InlineData(LabelTemplateKind.Sticker40x25, "7702177000014", "0.25")]
    [InlineData(LabelTemplateKind.Sticker50x30, "7702177000014", "0.375")]
    [InlineData(LabelTemplateKind.Shelf3x8, "001-000123", "0.25")]
    [InlineData(LabelTemplateKind.Sticker40x25, "001-000123", "0.25")]
    public void El_modulo_es_de_puntos_enteros_y_minimo_un_cuarto_de_milimetro(LabelTemplateKind kind, string code, string expected)
    {
        var template = LabelLayout.Get(kind);
        var drawing = BarcodeSymbology.Encode(code)!;

        var module = LabelLayout.ModuleWidth(template, drawing.TotalModules);

        module.ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        (module!.Value % LabelLayout.Dot).ShouldBe(0m);
        (module.Value * drawing.TotalModules).ShouldBeLessThanOrEqualTo(LabelLayout.BarcodeAreaWidth(template));
    }

    [Fact]
    public void Un_SKU_que_no_cabe_al_lado_usa_todo_el_ancho_de_la_etiqueta()
    {
        // ARROZ-500 en CODE128 B: 154 módulos × 0,25 = 38,5 mm > 36 mm (58 %), pero cabe en los 62 mm de ancho de la etiqueta de 66 mm.
        var template = LabelLayout.Get(LabelTemplateKind.Shelf3x8);
        var fit = LabelLayout.FitBarcode(template, BarcodeSymbology.Encode("ARROZ-500")!.TotalModules);

        fit.ShouldBe(new BarcodeFit(0.375m, 62m, FullWidth: true));
        LabelLayout.FitBarcode(template, BarcodeSymbology.Encode("7702177000014")!.TotalModules)!.FullWidth.ShouldBeFalse();
    }

    [Theory]
    [InlineData(LabelTemplateKind.Shelf3x8)]
    [InlineData(LabelTemplateKind.Shelf2x7)]
    [InlineData(LabelTemplateKind.Sticker40x25)]
    public void Un_codigo_largo_que_quedaria_ilegible_no_cabe(LabelTemplateKind kind)
    {
        var drawing = BarcodeSymbology.Encode("PRODUCTO-ABCDEFGHIJKLMNOPQRSTUVWXYZ-0123")!;

        LabelLayout.ModuleWidth(LabelLayout.Get(kind), drawing.TotalModules).ShouldBeNull();
    }

    [Fact]
    public void Ninguna_etiqueta_se_sale_de_la_hoja()
    {
        foreach (var template in LabelLayout.Templates)
        {
            var slots = LabelLayout.Slots(template, (template.PerPage * 2) + 1);
            slots.Select(s => s.Page).Distinct().Count().ShouldBe(3);
            slots.ShouldAllBe(s => s.X >= 0m && s.Y >= 0m && s.X + template.LabelWidth <= template.PageWidth && s.Y + template.LabelHeight <= template.PageHeight);
            slots.Where(s => s.Page == 0).Select(s => (s.Column, s.Row)).Distinct().Count().ShouldBe(template.PerPage);
        }
    }
}

