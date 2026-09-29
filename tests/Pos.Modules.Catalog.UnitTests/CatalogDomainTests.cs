using Pos.Modules.Catalog.Application;
using Pos.Modules.Catalog.Domain;
using Pos.SharedKernel.Domain;

namespace Pos.Modules.Catalog.UnitTests;

public class BarcodeTests
{
    [Theory]
    [InlineData("7702177000014", "7702177000014", BarcodeType.Ean13)]
    [InlineData("96385074", "96385074", BarcodeType.Ean8)]
    [InlineData("036000291452", "0036000291452", BarcodeType.Upca)]
    [InlineData(" abc-123 ", "ABC-123", BarcodeType.Code128)]
    public void Normaliza_y_detecta_el_tipo(string code, string normalized, BarcodeType type)
    {
        var result = Barcodes.Normalize(code);

        result.IsSuccess.ShouldBeTrue();
        result.Value.NormalizedCode.ShouldBe(normalized);
        result.Value.Type.ShouldBe(type);
    }

    [Theory]
    [InlineData("7702177000015")]
    [InlineData("96385075")]
    [InlineData("036000291453")]
    public void Un_digito_de_control_incorrecto_se_rechaza(string code) =>
        Barcodes.Normalize(code).Error.ShouldBe(CatalogErrors.InvalidCheckDigit);

    [Theory]
    [InlineData("")]
    [InlineData("con espacio")]
    [InlineData("123456789012345678901234567890123456789012345678901")]
    public void Codigos_invalidos(string code) => Barcodes.Normalize(code).Error.ShouldBe(CatalogErrors.InvalidBarcode);

    [Fact]
    public void Un_tipo_declarado_exige_su_formato()
    {
        Barcodes.Normalize("12345", BarcodeType.Ean13).Error.ShouldBe(CatalogErrors.InvalidBarcode);
        Barcodes.Normalize("7702177000014", BarcodeType.Internal).Error.ShouldBe(CatalogErrors.InvalidBarcode);
    }

    [Fact]
    public void El_UPC_se_busca_con_o_sin_el_cero()
    {
        Barcodes.NormalizeForLookup("036000291452").ShouldBe("0036000291452");
        Barcodes.NormalizeForLookup("0036000291452").ShouldBe("0036000291452");
        Barcodes.NormalizeForLookup(" sku-1 ").ShouldBe("SKU-1");
    }

    [Fact]
    public void Codigos_internos_con_prefijo_29_nodo_y_consecutivo()
    {
        var code = Barcodes.Internal(7, 123);

        code.ShouldBe("290070000123" + Barcodes.ComputeCheckDigit("290070000123"));
        Barcodes.HasValidCheckDigit(code).ShouldBeTrue();
        Barcodes.Normalize(code, BarcodeType.Internal).IsSuccess.ShouldBeTrue();
        Barcodes.InternalSku(7, 123).ShouldBe("007-000123");
        Should.Throw<ArgumentOutOfRangeException>(() => Barcodes.Internal(0, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => Barcodes.Internal(1, Barcodes.MaxInternalSequence + 1));
        Should.Throw<ArgumentException>(() => Barcodes.ComputeCheckDigit("12A"));
    }
}

public class VariableBarcodeRuleTests
{
    private static VariableBarcodeRule WeightRule() =>
        VariableBarcodeRule.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "20", VariableBarcodeContent.Weight, 3, 5, 8, 5, 3).Value;

    private static string Label(string body) => body + Barcodes.ComputeCheckDigit(body);

    [Fact]
    public void Etiqueta_de_peso_20_PPPPP_WWWWW_C()
    {
        var reading = WeightRule().Read(Label("200012301250"));

        reading.ShouldNotBeNull();
        reading.Plu.ShouldBe("123");
        reading.Value.ShouldBe(1.250m);
        reading.Content.ShouldBe(VariableBarcodeContent.Weight);
    }

    [Fact]
    public void Etiqueta_de_precio()
    {
        var rule = VariableBarcodeRule.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "23", VariableBarcodeContent.Price, 3, 5, 8, 5, 0).Value;

        rule.Read(Label("230000106225"))!.Value.ShouldBe(6225m);
        rule.Read(Label("230000006225"))!.Plu.ShouldBe("0");
    }

    [Fact]
    public void No_lee_otros_prefijos_digitos_malos_ni_reglas_inactivas()
    {
        var rule = WeightRule();
        rule.Read(Label("210012301250")).ShouldBeNull();
        rule.Read("2000123012500").ShouldBeNull();
        rule.Read("20001230125").ShouldBeNull();
        rule.Deactivate();
        rule.Read(Label("200012301250")).ShouldBeNull();
        rule.Activate();
        rule.Status.ShouldBe(MasterStatus.Active);
        rule.AuditLabel.ShouldContain("20");
    }

    [Theory]
    [InlineData("29", 3, 5, 8, 5, 3)]
    [InlineData("30", 3, 5, 8, 5, 3)]
    [InlineData("20", 2, 5, 8, 5, 3)]
    [InlineData("20", 3, 6, 8, 5, 3)]
    [InlineData("20", 3, 5, 9, 5, 3)]
    [InlineData("20", 3, 5, 8, 5, 5)]
    public void Reglas_invalidas(string prefix, short pluStart, short pluLength, short valueStart, short valueLength, short decimals)
    {
        var result = VariableBarcodeRule.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), prefix, VariableBarcodeContent.Weight, pluStart, pluLength,
            valueStart, valueLength, decimals);

        result.IsFailure.ShouldBeTrue();
    }
}

public class ProductTests
{
    private static ProductData Data(
        string sku = "LECHE-1", string unit = "UND", string dimension = "UNIT", SaleMode mode = SaleMode.Unit, bool scale = false, string? plu = null,
        ProductType type = ProductType.Stockable, bool lots = false, bool expiry = false, decimal? content = null, string? contentUnit = null) =>
        new(sku, " Leche entera Alquería 1100 ml ", null, null, Guid.CreateVersion7(), null, unit, dimension, mode, type, scale, false, false, lots, expiry, plu,
            content, contentUnit);

    [Fact]
    public void Un_producto_normaliza_sku_nombre_corto_y_texto_de_busqueda()
    {
        var product = Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Data(sku: " leche-1 ")).Value;

        product.Sku.ShouldBe("LECHE-1");
        product.Name.ShouldBe("Leche entera Alquería 1100 ml");
        product.ShortName.Length.ShouldBeLessThanOrEqualTo(40);
        product.SearchText.ShouldContain("alqueria");
        product.IsStockable.ShouldBeTrue();
        product.AuditLabel.ShouldContain("LECHE-1");
    }

    [Fact]
    public void Peso_permite_decimales_y_bascula_exige_kilos_y_PLU()
    {
        var weighed = Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Data(unit: "KG", dimension: "WEIGHT", mode: SaleMode.Weight, scale: true, plu: "00123")).Value;
        weighed.AllowsDecimalQuantity.ShouldBeTrue();
        weighed.PluCode.ShouldBe("123");

        Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Data(unit: "G", dimension: "WEIGHT", mode: SaleMode.Weight, scale: true, plu: "1"))
            .Error.ShouldBe(CatalogErrors.ScaleRequiresWeight);
        Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Data(scale: true, plu: "1")).Error.ShouldBe(CatalogErrors.ScaleRequiresWeight);
        Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Data(unit: "KG", dimension: "WEIGHT")).Error.ShouldBe(CatalogErrors.UnitDoesNotMatchSaleMode);
    }

    [Theory]
    [InlineData("A B")]
    [InlineData("-X")]
    [InlineData("")]
    public void SKU_invalido(string sku) => Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Data(sku: sku)).Error.ShouldBe(CatalogErrors.InvalidSku);

    [Fact]
    public void Reglas_de_servicio_lotes_contenido_y_plu()
    {
        var company = Guid.CreateVersion7();
        Product.Create(Guid.CreateVersion7(), company, Data(type: ProductType.Service, lots: true)).Error.ShouldBe(CatalogErrors.ServiceCannotTrackLots);
        Product.Create(Guid.CreateVersion7(), company, Data(expiry: true)).Error.ShouldBe(CatalogErrors.ExpiryRequiresLots);
        Product.Create(Guid.CreateVersion7(), company, Data(content: 0m, contentUnit: "ML")).Error.ShouldBe(CatalogErrors.InvalidNetContent);
        Product.Create(Guid.CreateVersion7(), company, Data(content: 1100m)).Error.ShouldBe(CatalogErrors.InvalidNetContent);
        Product.Create(Guid.CreateVersion7(), company, Data(plu: "12A")).Error.ShouldBe(CatalogErrors.InvalidPlu);
        Product.Create(Guid.CreateVersion7(), company, Data(plu: "1234567")).Error.ShouldBe(CatalogErrors.InvalidPlu);
        Product.Create(Guid.CreateVersion7(), company, Data(content: 1100m, contentUnit: "ml")).Value.NetContentUnit.ShouldBe("ML");
        Product.NormalizePlu("000").ShouldBe("0");
        Product.NormalizePlu("  ").ShouldBeNull();
    }

    [Fact]
    public void Con_movimientos_no_cambia_la_unidad_base_ni_el_tipo()
    {
        var product = Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Data()).Value;

        product.Update(Data(unit: "KG", dimension: "WEIGHT", mode: SaleMode.Weight), hasInventoryMovements: true).Error.ShouldBe(CatalogErrors.BaseUnitLocked);
        product.Update(Data(type: ProductType.Service), hasInventoryMovements: true).Error.ShouldBe(CatalogErrors.BaseUnitLocked);
        product.Update(Data(unit: "KG", dimension: "WEIGHT", mode: SaleMode.Weight), hasInventoryMovements: false).IsSuccess.ShouldBeTrue();
        product.BaseUnitCode.ShouldBe("KG");
    }

    [Fact]
    public void Descontinuar_con_existencias_se_bloquea_segun_la_empresa()
    {
        var product = Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Data()).Value;

        product.ChangeStatus(ProductStatus.Discontinued, onHand: 3m, blockDiscontinueWithStock: true).Error.ShouldBe(CatalogErrors.ProductHasStock);
        product.ChangeStatus(ProductStatus.Discontinued, onHand: 3m, blockDiscontinueWithStock: false).IsSuccess.ShouldBeTrue();
        product.ChangeStatus(ProductStatus.Active, 3m, true).IsSuccess.ShouldBeTrue();
        product.Status.ShouldBe(ProductStatus.Active);
    }

    [Fact]
    public void Presentaciones_codigos_e_impuestos_del_producto()
    {
        var product = Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Data()).Value;
        ProductPackaging.Create(Guid.CreateVersion7(), product, "Caja x24", 0m, true, true).Error.ShouldBe(CatalogErrors.InvalidFactor);
        ProductPackaging.Create(Guid.CreateVersion7(), product, "Caja x24", 1.23456m, true, true).Error.ShouldBe(CatalogErrors.InvalidFactor);
        ProductPackaging.Create(Guid.CreateVersion7(), product, " ", 6m, true, true).Error.ShouldBe(CatalogErrors.InvalidName);
        var box = ProductPackaging.Create(Guid.CreateVersion7(), product, " Caja x24 ", 24m, true, false).Value;
        box.Name.ShouldBe("Caja x24");
        box.AuditLabel.ShouldContain("x24");

        var barcode = ProductBarcode.Create(Guid.CreateVersion7(), product, box, Barcodes.Normalize("7702177000014").Value, isPrimary: true);
        barcode.PackagingId.ShouldBe(box.Id);
        barcode.SetPrimary(false);
        barcode.IsPrimary.ShouldBeFalse();
        barcode.AuditLabel.ShouldContain("7702177000014");

        var other = Product.Create(Guid.CreateVersion7(), product.CompanyId, Data(sku: "OTRO")).Value;
        Should.Throw<DomainException>(() => ProductBarcode.Create(Guid.CreateVersion7(), other, box, Barcodes.Normalize("96385074").Value, false));

        var vat = Tax.Create(Guid.CreateVersion7(), product.CompanyId, "IVA19", "IVA", TaxKind.Vat, TaxCalculation.Percentage, false, false, "01").Value;
        ProductTax.Create(Guid.CreateVersion7(), product, vat, 50m).Error.ShouldBe(CatalogErrors.FixedAmountOnlyForFixedTaxes);
        var bag = Tax.Create(Guid.CreateVersion7(), product.CompanyId, "INC_BOLSA", "Bolsa", TaxKind.BagConsumption, TaxCalculation.FixedPerUnit, false, false, null).Value;
        var productTax = ProductTax.Create(Guid.CreateVersion7(), product, bag, 66m).Value;
        productTax.ChangeFixedAmount(70m);
        productTax.FixedAmount.ShouldBe(70m);
        productTax.AuditLabel.ShouldNotBeEmpty();
    }
}

public class CategoryTests
{
    private static readonly Guid Company = Guid.CreateVersion7();

    [Fact]
    public void Arbol_de_hasta_cuatro_niveles_con_ruta_materializada()
    {
        var root = Category.Create(Guid.CreateVersion7(), Company, " Lácteos ", null).Value;
        var l2 = Category.Create(Guid.CreateVersion7(), Company, "Leches", root).Value;
        var l3 = Category.Create(Guid.CreateVersion7(), Company, "Enteras", l2).Value;
        var l4 = Category.Create(Guid.CreateVersion7(), Company, "UHT", l3).Value;

        root.Name.ShouldBe("Lácteos");
        l4.Level.ShouldBe((short)4);
        l4.Path.ShouldBe($"/{root.Id:D}/{l2.Id:D}/{l3.Id:D}/{l4.Id:D}/");
        Category.Create(Guid.CreateVersion7(), Company, "Quinto", l4).Error.ShouldBe(CatalogErrors.CategoryTooDeep);
        Category.Create(Guid.CreateVersion7(), Company, " ", null).Error.ShouldBe(CatalogErrors.InvalidName);
        root.AuditLabel.ShouldContain("Lácteos");
    }

    [Fact]
    public void Mover_con_subarbol_sin_ciclos_ni_exceso_de_niveles()
    {
        var a = Category.Create(Guid.CreateVersion7(), Company, "A", null).Value;
        var b = Category.Create(Guid.CreateVersion7(), Company, "B", a).Value;
        var c = Category.Create(Guid.CreateVersion7(), Company, "C", b).Value;
        var x = Category.Create(Guid.CreateVersion7(), Company, "X", null).Value;

        b.MoveTo(c, subtreeHeight: 2).Error.ShouldBe(CatalogErrors.CategoryCycle);
        b.MoveTo(b, 2).Error.ShouldBe(CatalogErrors.CategoryCycle);

        var deep = Category.Create(Guid.CreateVersion7(), Company, "Y", Category.Create(Guid.CreateVersion7(), Company, "Z", x).Value).Value;
        b.MoveTo(deep, 2).Error.ShouldBe(CatalogErrors.CategoryTooDeep);

        var oldPath = b.MoveTo(x, 2).Value;
        c.Rebase(oldPath, b.Path, 0);
        b.ParentId.ShouldBe(x.Id);
        c.Path.ShouldStartWith(x.Path);
        c.Level.ShouldBe((short)3);
        Should.Throw<DomainException>(() => a.Rebase(oldPath, b.Path, 0));

        b.MoveTo(null, 2).IsSuccess.ShouldBeTrue();
        b.Level.ShouldBe((short)1);
        b.Rename("B2", 5).IsSuccess.ShouldBeTrue();
        b.Rename("", 5).IsFailure.ShouldBeTrue();
        b.Deactivate();
        b.Status.ShouldBe(MasterStatus.Inactive);
        b.Activate();
    }

    [Fact]
    public void Marcas()
    {
        var brand = Brand.Create(Guid.CreateVersion7(), Company, " Alquería ").Value;
        brand.Name.ShouldBe("Alquería");
        brand.Rename(new string('x', 81)).Error.ShouldBe(CatalogErrors.InvalidName);
        brand.Rename("Colanta").IsSuccess.ShouldBeTrue();
        brand.Deactivate();
        brand.Status.ShouldBe(MasterStatus.Inactive);
        brand.Activate();
        brand.AuditLabel.ShouldContain("Colanta");
        Brand.Create(Guid.CreateVersion7(), Company, "").IsFailure.ShouldBeTrue();
    }
}

public class TaxAndPriceTests
{
    private static readonly Guid Company = Guid.CreateVersion7();

    [Fact]
    public void Exento_y_excluido_solo_en_IVA_porcentual_y_con_tarifa_cero()
    {
        Tax.Create(Guid.CreateVersion7(), Company, "X", "X", TaxKind.Vat, TaxCalculation.Percentage, false, false, null).Error.ShouldBe(CatalogErrors.InvalidTaxCode);
        Tax.Create(Guid.CreateVersion7(), Company, "INC", "INC", TaxKind.Consumption, TaxCalculation.Percentage, true, false, null)
            .Error.ShouldBe(CatalogErrors.ZeroRatedMustBeVat);
        Tax.Create(Guid.CreateVersion7(), Company, "IVA0", "IVA", TaxKind.Vat, TaxCalculation.Percentage, true, true, null)
            .Error.ShouldBe(CatalogErrors.ZeroRatedMustBeVat);

        var exempt = Tax.Create(Guid.CreateVersion7(), Company, "iva0_exento", "Exento", TaxKind.Vat, TaxCalculation.Percentage, true, false, " 01 ").Value;
        exempt.Code.ShouldBe("IVA0_EXENTO");
        exempt.DianCode.ShouldBe("01");
        exempt.ValidateRate(5m, null).Error.ShouldBe(CatalogErrors.InvalidTaxRate);
        TaxRate.Create(Guid.CreateVersion7(), exempt, 0m, null, new DateOnly(2017, 1, 1)).IsSuccess.ShouldBeTrue();

        var bag = Tax.Create(Guid.CreateVersion7(), Company, "INC_BOLSA", "Bolsa", TaxKind.BagConsumption, TaxCalculation.FixedPerUnit, false, false, null).Value;
        bag.ValidateRate(19m, null).IsFailure.ShouldBeTrue();
        bag.ValidateRate(null, 66m).IsSuccess.ShouldBeTrue();
        bag.Update("Bolsas plásticas", "22").IsSuccess.ShouldBeTrue();
        bag.Update(" ", null).IsFailure.ShouldBeTrue();
        bag.Deactivate();
        bag.Status.ShouldBe(MasterStatus.Inactive);
        bag.Activate();
        bag.AuditLabel.ShouldContain("INC_BOLSA");
    }

    [Fact]
    public void Tarifa_con_vigencia()
    {
        var vat = Tax.Create(Guid.CreateVersion7(), Company, "IVA19", "IVA 19", TaxKind.Vat, TaxCalculation.Percentage, false, false, "01").Value;
        var rate = TaxRate.Create(Guid.CreateVersion7(), vat, 19m, null, new DateOnly(2017, 1, 1)).Value;

        rate.IsValidOn(new DateOnly(2026, 10, 1)).ShouldBeTrue();
        rate.CloseAt(new DateOnly(2027, 1, 1));
        rate.IsValidOn(new DateOnly(2027, 1, 1)).ShouldBeFalse();
        rate.ChangeValue(16m, null);
        rate.Rate.ShouldBe(16m);
        rate.AuditLabel.ShouldContain("2017-01-01");
        TaxRate.Create(Guid.CreateVersion7(), vat, 120m, null, new DateOnly(2017, 1, 1)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Planificador_de_vigencias()
    {
        var d = (int day) => new DateOnly(2026, 10, day);

        // Sin nada: queda abierto.
        ValidityPlanner.PlanInsert<DateOnly>([], d(5)).ShouldBe(new ValidityPlanner.Plan<DateOnly>(null, null, null));

        // Cierra el vigente en la fecha nueva.
        ValidityPlanner.PlanInsert<DateOnly>([(d(1), null)], d(5)).ShouldBe(new ValidityPlanner.Plan<DateOnly>(0, null, null));

        // Hay uno programado después: el nuevo termina donde empieza ese.
        ValidityPlanner.PlanInsert<DateOnly>([(d(1), d(10)), (d(10), null)], d(5)).ShouldBe(new ValidityPlanner.Plan<DateOnly>(0, null, d(10)));

        // Misma fecha de inicio: se reemplaza y conserva su fin.
        ValidityPlanner.PlanInsert<DateOnly>([(d(1), d(10)), (d(10), null)], d(10)).ShouldBe(new ValidityPlanner.Plan<DateOnly>(null, 1, null));
    }

    [Fact]
    public void Listas_y_precios()
    {
        PriceList.Create(Guid.CreateVersion7(), Company, "x", "X", true, false).IsFailure.ShouldBeTrue();
        var list = PriceList.Create(Guid.CreateVersion7(), Company, "general", "Precio general", true, isDefault: true).Value;
        list.Code.ShouldBe("GENERAL");
        list.Deactivate().Error.ShouldBe(CatalogErrors.DefaultPriceListRequired);
        list.UnsetDefault();
        list.Deactivate().IsSuccess.ShouldBeTrue();
        list.MakeDefault();
        list.Status.ShouldBe(MasterStatus.Active);
        list.Update("", true).IsFailure.ShouldBeTrue();
        list.Activate();
        list.AuditLabel.ShouldContain("GENERAL");

        var product = Product.Create(Guid.CreateVersion7(), Company, new ProductData("P1", "Producto", null, null, Guid.CreateVersion7(), null, "UND", "UNIT",
            SaleMode.Unit, ProductType.Stockable, false, false, false, false, false, null, null, null)).Value;
        var now = DateTimeOffset.UtcNow;
        ProductPrice.Create(Guid.CreateVersion7(), list, product, null, null, 1.234m, now, null).Error.ShouldBe(CatalogErrors.InvalidPrice);
        ProductPrice.Create(Guid.CreateVersion7(), list, product, null, null, -1m, now, null).Error.ShouldBe(CatalogErrors.InvalidPrice);
        var price = ProductPrice.Create(Guid.CreateVersion7(), list, product, null, null, 4980m, now.AddDays(1), null).Value;
        price.IsScheduled(now).ShouldBeTrue();
        price.IsValidAt(now.AddDays(2)).ShouldBeTrue();
        price.Reprice(5000m);
        price.CloseAt(now.AddDays(3));
        price.IsValidAt(now.AddDays(3)).ShouldBeFalse();
        price.AuditLabel.ShouldNotBeEmpty();

        var otherProduct = Product.Create(Guid.CreateVersion7(), Company, new ProductData("P2", "Otro", null, null, Guid.CreateVersion7(), null, "UND", "UNIT",
            SaleMode.Unit, ProductType.Stockable, false, false, false, false, false, null, null, null)).Value;
        var foreign = ProductPackaging.Create(Guid.CreateVersion7(), otherProduct, "Caja", 6m, true, true).Value;
        ProductPrice.Create(Guid.CreateVersion7(), list, product, foreign, null, 10m, now, null).Error.ShouldBe(CatalogErrors.PackagingNotFound);
    }
}

public class ImportParsingTests
{
    [Fact]
    public void Rutas_de_categoria_y_booleanos()
    {
        CatalogImportService.SplitPath(" Lácteos >  Leches > ").ShouldBe(["Lácteos", "Leches"]);
        CatalogImportService.SplitPath(null).ShouldBeEmpty();
        CatalogImportService.ParseBool("Sí").ShouldBeTrue();
        CatalogImportService.ParseBool("x").ShouldBeTrue();
        CatalogImportService.ParseBool("no").ShouldBeFalse();
        CatalogImportService.Same("Lácteos", "LACTEOS").ShouldBeTrue();
    }

    [Fact]
    public void Las_plantillas_traen_todas_las_columnas()
    {
        ImportColumns.Template(ImportKind.Products).ShouldStartWith(string.Join(';', ImportColumns.Products));
        ImportColumns.Template(ImportKind.Prices).ShouldStartWith(string.Join(';', ImportColumns.Prices));
    }
}
