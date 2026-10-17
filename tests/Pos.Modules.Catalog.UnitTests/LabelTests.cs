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

/// <summary>Precio al público, oferta y precio por unidad de medida de la etiqueta.</summary>
public class LabelPricingTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Category = Guid.NewGuid();
    private static readonly Guid Brand = Guid.NewGuid();

    [Fact]
    public void Precio_al_publico_suma_impuestos_si_la_lista_no_los_incluye()
    {
        LabelPricing.ConsumerPrice(10_000m, includesTax: true, 19m, 0m).ShouldBe(10_000m);
        LabelPricing.ConsumerPrice(10_000m, includesTax: false, 19m, 0m).ShouldBe(11_900m);
        LabelPricing.ConsumerPrice(2_000m, includesTax: false, 0m, 150m).ShouldBe(2_150m);
        LabelPricing.ConsumerPrice(1_234.5m, includesTax: false, 0m, 0m).ShouldBe(1_235m);
    }

    [Fact]
    public void Gana_la_oferta_de_menor_precio()
    {
        LabelPromotion[] promotions =
        [
            new("Diez por ciento", "PERCENT_OFF", [ForProduct()], Percent: 10m),
            new("Precio especial", "SPECIAL_PRICE", [ForCategory()], Price: 8_500m),
        ];

        var offer = LabelPricing.BestOffer(Product, Category, Brand, 10_000m, promotions);

        offer.ShouldBe(new LabelOffer(8_500m, "Precio especial"));
    }

    [Fact]
    public void Porcentaje_redondea_al_peso()
    {
        var offer = LabelPricing.BestOffer(Product, Category, null, 3_333m, [new LabelPromotion("x", "PERCENT_OFF", [ForProduct()], Percent: 15m)]);

        offer.ShouldBe(new LabelOffer(2_833m, "Ahorre 15 %"));
    }

    [Fact]
    public void Una_oferta_que_no_baja_el_precio_no_se_imprime()
    {
        LabelPricing.BestOffer(Product, Category, Brand, 5_000m, [new LabelPromotion("x", "SPECIAL_PRICE", [ForProduct()], Price: 6_000m)]).ShouldBeNull();
    }

    [Fact]
    public void Solo_aplica_a_su_producto_categoria_o_marca()
    {
        var other = Guid.NewGuid();
        LabelPromotion[] promotions =
        [
            new("Otro producto", "SPECIAL_PRICE", [new LabelPromotionTarget(other, null, null, null)], Price: 1m),
            new("Otra categoría", "SPECIAL_PRICE", [new LabelPromotionTarget(null, null, new HashSet<Guid> { other }, null)], Price: 2m),
            new("Otra marca", "SPECIAL_PRICE", [new LabelPromotionTarget(null, null, null, other)], Price: 3m),
            new("Caja x24", "SPECIAL_PRICE", [new LabelPromotionTarget(Product, Guid.NewGuid(), null, null)], Price: 4m),
            new("Combo", "COMBO", [ForProduct()], Price: 5m),
        ];

        LabelPricing.BestOffer(Product, Category, Brand, 10_000m, promotions).ShouldBeNull();
        LabelPricing.BestOffer(Product, Category, Brand, 10_000m, [new LabelPromotion("Marca", "SPECIAL_PRICE", [new LabelPromotionTarget(null, null, null, Brand)], Price: 9_000m)])
            .ShouldBe(new LabelOffer(9_000m, "Precio especial"));
    }

    [Fact]
    public void Ofertas_por_cantidad_salen_como_texto()
    {
        LabelPricing.BestOffer(Product, Category, Brand, 2_000m,
                [new LabelPromotion("x", "QUANTITY_PRICE", [ForProduct()], Price: 1_800m, MinQuantity: 3m)])
            .ShouldBe(new LabelOffer(null, "Desde 3 und: $ 1.800 c/u"));
        LabelPricing.BestOffer(Product, Category, Brand, 2_000m, [new LabelPromotion("x", "MULTI_BUY", [ForCategory()], BuyQuantity: 3, PayQuantity: 2)])
            .ShouldBe(new LabelOffer(null, "Lleve 3 pague 2"));
    }

    [Theory]
    [InlineData(6_250, 500, "G", "$ 12.500 por kg")]
    [InlineData(4_800, 1, "KG", "$ 4.800 por kg")]
    [InlineData(3_000, 750, "ML", "$ 4.000 por litro")]
    [InlineData(5_000, 2, "L", "$ 2.500 por litro")]
    public void Precio_por_unidad_de_medida(decimal price, decimal content, string unit, string expected) =>
        LabelPricing.UnitMeasurePrice(price, content, unit).ShouldBe(expected);

    [Fact]
    public void Sin_contenido_neto_no_hay_precio_por_unidad()
    {
        LabelPricing.UnitMeasurePrice(1_000m, null, "G").ShouldBeNull();
        LabelPricing.UnitMeasurePrice(1_000m, 6m, "UND").ShouldBeNull();
        LabelPricing.UnitMeasurePrice(null, 500m, "G").ShouldBeNull();
    }

    [Fact]
    public void Etiqueta_de_un_producto_por_peso_sin_impuestos_incluidos()
    {
        var product = Data(saleMode: SaleMode.Weight, scale: true, unit: "KG", listPrice: 10_000m, includesTax: false, barcode: null);
        CatalogTaxInfo[] taxes = [new(Guid.NewGuid(), "IVA5", "VAT", true, 5m, null)];

        var item = LabelBuilder.ToItem(product, taxes, [], 7);

        item.Price.ShouldBe(10_500m);
        item.PriceUnit.ShouldBe("kg");
        item.IsWeighed.ShouldBeTrue();
        item.UnitMeasurePrice.ShouldBeNull();
        item.HasBarcode.ShouldBeFalse();
        item.Quantity.ShouldBe(7);

        var content = LabelBuilder.ToContent(item, new DateOnly(2026, 10, 17));
        content.PriceText.ShouldBe("$ 10.500");
        content.Barcode!.Symbol.ShouldBe(BarcodeSymbol.Code128);
        content.Barcode.Text.ShouldBe("SKU-1");
        content.DateText.ShouldBe("17/10/2026");
    }

    [Fact]
    public void Etiqueta_con_oferta_muestra_el_precio_normal_tachado()
    {
        var product = Data(netContent: 500m, netUnit: "G", listPrice: 6_000m);

        var item = LabelBuilder.ToItem(product, [], [new LabelPromotion("x", "SPECIAL_PRICE", [ForProduct()], Price: 5_000m)], 1_000);
        var content = LabelBuilder.ToContent(item, null);

        item.Quantity.ShouldBe(LabelLayout.MaxCopies);
        item.UnitMeasurePrice.ShouldBe("$ 10.000 por kg");
        content.PriceText.ShouldBe("$ 5.000");
        content.RegularPriceText.ShouldBe("$ 6.000");
        content.OfferText.ShouldBe("Precio especial");
        content.Barcode!.Symbol.ShouldBe(BarcodeSymbol.Ean13);
        content.DateText.ShouldBeNull();
    }

    [Fact]
    public void Tiquete_de_etiquetas_para_la_impresora_de_la_caja()
    {
        var content = LabelBuilder.ToContent(LabelBuilder.ToItem(Data(listPrice: 2_500m), [], [], 1), null);

        var ticket = LabelPrinting.Ticket([content, content]);

        ticket.Cut.ShouldBeTrue();
        ticket.OpenDrawer.ShouldBeFalse();
        ticket.Elements.OfType<BarcodeElement>().Select(b => b.Data).ShouldBe(["7702177000014", "7702177000014"]);
        ticket.Elements.OfType<TextLine>().ShouldContain(t => t.Text == "$ 2.500" && t.DoubleSize);
    }

    private static LabelPromotionTarget ForProduct() => new(Product, null, null, null);

    private static LabelPromotionTarget ForCategory() => new(null, null, new HashSet<Guid> { Category }, null);

    private static LabelProductData Data(
        SaleMode saleMode = SaleMode.Unit, bool scale = false, string unit = "UND", decimal? netContent = null, string? netUnit = null,
        decimal? listPrice = 1_000m, bool includesTax = true, string? barcode = "7702177000014") =>
        new(Product, "SKU-1", "Producto de prueba", Category, Brand, unit, saleMode, scale, netContent, netUnit, ProductStatus.Active, barcode, listPrice,
            includesTax, AllowsPromotions: true);
}
