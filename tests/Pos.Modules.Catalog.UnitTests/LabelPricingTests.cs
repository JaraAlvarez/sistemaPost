using Pos.Modules.Catalog.Application;
using Pos.Modules.Catalog.Domain;
using Pos.Printing;

namespace Pos.Modules.Catalog.UnitTests;

/// <summary>Precio al público (igual al de la caja), oferta y precio por unidad de medida de la etiqueta.</summary>
public class LabelPricingTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Category = Guid.NewGuid();
    private static readonly Guid Brand = Guid.NewGuid();
    private static readonly LabelTax[] Iva19 = [new(19m, null)];
    private static readonly LabelTax[] IvaYBolsa = [new(19m, null), new(null, 150m)];

    [Fact]
    public void Precio_al_publico_suma_impuestos_si_la_lista_no_los_incluye()
    {
        LabelPricing.RegularPrice(10_000m, includesTax: true, Iva19).ShouldBe(10_000m);
        LabelPricing.RegularPrice(10_000m, includesTax: false, Iva19).ShouldBe(11_900m);
        LabelPricing.RegularPrice(2_000m, includesTax: false, [new LabelTax(null, 150m)]).ShouldBe(2_150m);
        LabelPricing.RegularPrice(2_000m, includesTax: false, IvaYBolsa).ShouldBe(2_530m);
    }

    [Fact]
    public void Precio_especial_en_lista_sin_IVA_suma_el_IVA_como_la_caja()
    {
        // Lista sin IVA: 10.000 + 19 % = 11.900; promoción a 9.000 (en términos de la lista) → la caja cobra 9.000 + 1.710 = 10.710.
        var offer = LabelPricing.BestOffer(Product, Category, Brand, 10_000m, false, Iva19, [new LabelPromotion("x", "SPECIAL_PRICE", [ForProduct()], Price: 9_000m)]);

        offer.ShouldBe(new LabelOffer(10_710m, "Precio especial"));
    }

    [Fact]
    public void Porcentaje_en_lista_sin_impuestos_no_descuenta_el_impuesto_fijo()
    {
        // 2.000 − 10 % = 1.800; IVA 19 % de 1.800 = 342; impuesto fijo 150 sin descuento → 2.292.
        var offer = LabelPricing.BestOffer(Product, Category, Brand, 2_000m, false, IvaYBolsa, [new LabelPromotion("x", "PERCENT_OFF", [ForProduct()], Percent: 10m)]);

        offer.ShouldBe(new LabelOffer(2_292m, "Ahorre 10 %"));
    }

    [Fact]
    public void Precio_por_cantidad_en_lista_sin_impuestos_muestra_el_precio_con_impuestos()
    {
        var offer = LabelPricing.BestOffer(Product, Category, Brand, 2_000m, false, IvaYBolsa,
            [new LabelPromotion("x", "QUANTITY_PRICE", [ForProduct()], Price: 1_800m, MinQuantity: 3m)]);

        offer.ShouldBe(new LabelOffer(null, "Desde 3 und: $ 2.292 c/u"));
    }

    [Fact]
    public void Precio_por_cantidad_desde_una_unidad_es_un_precio_especial()
    {
        LabelPromotion[] promotions =
        [
            new("Desde 1", "QUANTITY_PRICE", [ForProduct()], Price: 8_000m, MinQuantity: 1m),
            new("Especial", "SPECIAL_PRICE", [ForProduct()], Price: 8_500m),
        ];

        LabelPricing.BestOffer(Product, Category, Brand, 10_000m, false, Iva19, promotions).ShouldBe(new LabelOffer(9_520m, "Precio especial"));
    }

    [Fact]
    public void En_productos_por_peso_la_cantidad_va_en_kilos()
    {
        var product = Data(saleMode: SaleMode.Weight, scale: true, unit: "KG", listPrice: 20_000m, barcode: null);

        var item = LabelBuilder.ToItem(product, [], [new LabelPromotion("x", "QUANTITY_PRICE", [ForProduct()], Price: 18_000m, MinQuantity: 2m)], 1,
            TimeZoneInfo.Utc);

        item.PromoText.ShouldBe("Desde 2 kg: $ 18.000/kg");
        item.PromoPrice.ShouldBeNull();

        // Por peso, "desde 1 kg" no es precio principal: la caja no lo aplica a 0,750 kg.
        var fromOne = LabelBuilder.ToItem(product, [], [new LabelPromotion("x", "QUANTITY_PRICE", [ForProduct()], Price: 18_000m, MinQuantity: 1m)], 1,
            TimeZoneInfo.Utc);
        fromOne.PromoPrice.ShouldBeNull();
        fromOne.PromoText.ShouldBe("Desde 1 kg: $ 18.000/kg");
        fromOne.Price.ShouldBe(20_000m);
    }

    [Fact]
    public void Empate_gana_la_de_menor_Id_como_en_la_caja()
    {
        var low = new Guid("00000000-0000-0000-0000-000000000001");
        var high = new Guid("00000000-0000-0000-0000-000000000002");
        LabelPromotion[] promotions =
        [
            new("Especial", "SPECIAL_PRICE", [ForProduct()], Price: 9_000m, Id: high),
            new("Diez", "PERCENT_OFF", [ForProduct()], Percent: 10m, Id: low),
        ];

        LabelPricing.BestOffer(Product, Category, Brand, 10_000m, true, [], promotions).ShouldBe(new LabelOffer(9_000m, "Ahorre 10 %"));
    }

    [Fact]
    public void Gana_la_oferta_de_menor_precio()
    {
        LabelPromotion[] promotions =
        [
            new("Diez por ciento", "PERCENT_OFF", [ForProduct()], Percent: 10m),
            new("Precio especial", "SPECIAL_PRICE", [ForCategory()], Price: 8_500m),
        ];

        LabelPricing.BestOffer(Product, Category, Brand, 10_000m, true, Iva19, promotions).ShouldBe(new LabelOffer(8_500m, "Precio especial"));
    }

    [Fact]
    public void Porcentaje_conserva_los_centavos_y_se_exhibe_hacia_arriba()
    {
        var offer = LabelPricing.BestOffer(Product, Category, null, 3_333m, true, Iva19, [new LabelPromotion("x", "PERCENT_OFF", [ForProduct()], Percent: 15m)]);

        // 3.333 − round(499,95) = 2.833,05: la etiqueta dice $ 2.834 (nunca menos de lo que cobra la caja).
        offer.ShouldBe(new LabelOffer(2_833.05m, "Ahorre 15 %"));
        LabelPricing.Money(offer!.Price!.Value).ShouldBe("$ 2.834");
    }

    [Fact]
    public void Una_oferta_que_no_baja_el_precio_no_se_imprime() =>
        LabelPricing.BestOffer(Product, Category, Brand, 5_000m, true, [], [new LabelPromotion("x", "SPECIAL_PRICE", [ForProduct()], Price: 6_000m)]).ShouldBeNull();

    [Fact]
    public void Promocion_con_dias_u_horario_no_va_en_la_etiqueta()
    {
        LabelPromotion[] promotions = [new("Martes", "SPECIAL_PRICE", [ForProduct()], Price: 1_000m, HasSchedule: true)];

        LabelPricing.BestOffer(Product, Category, Brand, 5_000m, true, [], promotions).ShouldBeNull();
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

        LabelPricing.BestOffer(Product, Category, Brand, 10_000m, true, [], promotions).ShouldBeNull();
        LabelPricing.BestOffer(Product, Category, Brand, 10_000m, true, [],
                [new LabelPromotion("Marca", "SPECIAL_PRICE", [new LabelPromotionTarget(null, null, null, Brand)], Price: 9_000m)])
            .ShouldBe(new LabelOffer(9_000m, "Precio especial"));
    }

    [Fact]
    public void Lleve_N_pague_M_sale_como_texto() =>
        LabelPricing.BestOffer(Product, Category, Brand, 2_000m, true, [], [new LabelPromotion("x", "MULTI_BUY", [ForCategory()], BuyQuantity: 3, PayQuantity: 2)])
            .ShouldBe(new LabelOffer(null, "Lleve 3 pague 2"));

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

        var item = LabelBuilder.ToItem(product, [new LabelTax(5m, null)], [], 7, TimeZoneInfo.Utc);

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
    public void Etiqueta_con_oferta_muestra_el_precio_normal_tachado_y_hasta_cuando()
    {
        var product = Data(netContent: 500m, netUnit: "G", listPrice: 6_000m, status: ProductStatus.Inactive);
        var until = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var item = LabelBuilder.ToItem(product, [], [new LabelPromotion("x", "SPECIAL_PRICE", [ForProduct()], Price: 5_000m, ValidTo: until)], 1_000, TimeZoneInfo.Utc);
        var content = LabelBuilder.ToContent(item, null);

        item.Quantity.ShouldBe(LabelLayout.MaxCopies);
        item.IsActive.ShouldBeFalse();
        item.PromoValidUntil.ShouldBe(new DateOnly(2026, 10, 31));
        item.UnitMeasurePrice.ShouldBe("$ 10.000 por kg");
        content.PriceText.ShouldBe("$ 5.000");
        content.RegularPriceText.ShouldBe("$ 6.000");
        content.OfferText.ShouldBe("Precio especial hasta 31/10");
        content.Barcode!.Symbol.ShouldBe(BarcodeSymbol.Ean13);
        content.DateText.ShouldBeNull();
    }

    [Fact]
    public void Tiquete_de_etiquetas_para_la_impresora_de_la_caja()
    {
        var ean = LabelBuilder.ToContent(LabelBuilder.ToItem(Data(listPrice: 2_500m), [], [], 1, TimeZoneInfo.Utc), null);
        var sku = LabelBuilder.ToContent(LabelBuilder.ToItem(Data(listPrice: 2_500m, barcode: null), [], [], 1, TimeZoneInfo.Utc), null);

        var ticket = LabelPrinting.Ticket([ean, sku]);

        ticket.Cut.ShouldBeTrue();
        ticket.OpenDrawer.ShouldBeFalse();
        ticket.Elements.OfType<BarcodeElement>().ShouldBe([new BarcodeElement("7702177000014", BarcodeElement.Ean13), new BarcodeElement("SKU-1", BarcodeElement.Code128)]);
        ticket.Elements.OfType<TextLine>().ShouldContain(t => t.Text == "$ 2.500" && t.DoubleSize);
    }

    private static LabelPromotionTarget ForProduct() => new(Product, null, null, null);

    private static LabelPromotionTarget ForCategory() => new(null, null, new HashSet<Guid> { Category }, null);

    private static LabelProductData Data(
        SaleMode saleMode = SaleMode.Unit, bool scale = false, string unit = "UND", decimal? netContent = null, string? netUnit = null,
        decimal? listPrice = 1_000m, bool includesTax = true, string? barcode = "7702177000014", ProductStatus status = ProductStatus.Active) =>
        new(Product, "SKU-1", "Producto de prueba", Category, Brand, unit, saleMode, scale, netContent, netUnit, status, barcode, listPrice,
            includesTax);
}
