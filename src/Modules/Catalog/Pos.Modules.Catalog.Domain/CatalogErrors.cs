using Pos.SharedKernel.Results;

namespace Pos.Modules.Catalog.Domain;

/// <summary>Errores de negocio del catálogo con código estable (contrato con la UI y la documentación).</summary>
public static class CatalogErrors
{
    public static readonly Error ProductNotFound = Error.NotFound("CATALOG.PRODUCT_NOT_FOUND", "El producto no existe.");

    public static readonly Error CategoryNotFound = Error.NotFound("CATALOG.CATEGORY_NOT_FOUND", "La categoría no existe.");

    public static readonly Error BrandNotFound = Error.NotFound("CATALOG.BRAND_NOT_FOUND", "La marca no existe.");

    public static readonly Error TaxNotFound = Error.NotFound("CATALOG.TAX_NOT_FOUND", "El impuesto no existe.");

    public static readonly Error PriceListNotFound = Error.NotFound("CATALOG.PRICE_LIST_NOT_FOUND", "La lista de precios no existe.");

    public static readonly Error PackagingNotFound = Error.NotFound("CATALOG.PACKAGING_NOT_FOUND", "La presentación no existe en este producto.");

    public static readonly Error BarcodeNotFound = Error.NotFound("CATALOG.BARCODE_NOT_FOUND", "El código de barras no existe en este producto.");

    public static readonly Error PriceNotFound = Error.NotFound("CATALOG.PRICE_NOT_FOUND", "El precio no existe.");

    public static readonly Error BarcodeRuleNotFound = Error.NotFound("CATALOG.BARCODE_RULE_NOT_FOUND", "La regla de código de báscula no existe.");

    public static readonly Error UnitNotFound = Error.Validation("CATALOG.UNKNOWN_UNIT", "La unidad de medida no existe.");

    public static readonly Error CodeNotFound = Error.NotFound("CATALOG.CODE_NOT_FOUND", "Ningún producto tiene ese código.");

    public static readonly Error SkuDuplicated = Error.Conflict("CATALOG.SKU_DUPLICATED", "Ya existe un producto con ese SKU.");

    public static readonly Error BarcodeDuplicated =
        Error.Conflict("CATALOG.BARCODE_DUPLICATED", "Ese código de barras ya pertenece a otro producto o presentación.");

    public static readonly Error PluDuplicated = Error.Conflict("CATALOG.PLU_DUPLICATED", "Ya existe un producto con ese PLU de báscula.");

    public static readonly Error CategoryNameDuplicated =
        Error.Conflict("CATALOG.CATEGORY_NAME_DUPLICATED", "Ya existe una categoría con ese nombre en el mismo nivel.");

    public static readonly Error BrandNameDuplicated = Error.Conflict("CATALOG.BRAND_NAME_DUPLICATED", "Ya existe una marca con ese nombre.");

    public static readonly Error TaxCodeDuplicated = Error.Conflict("CATALOG.TAX_CODE_DUPLICATED", "Ya existe un impuesto con ese código.");

    public static readonly Error PriceListCodeDuplicated =
        Error.Conflict("CATALOG.PRICE_LIST_CODE_DUPLICATED", "Ya existe una lista de precios con ese código.");

    public static readonly Error PackagingNameDuplicated =
        Error.Conflict("CATALOG.PACKAGING_NAME_DUPLICATED", "El producto ya tiene una presentación con ese nombre.");

    public static readonly Error ProductTaxDuplicated = Error.Conflict("CATALOG.PRODUCT_TAX_DUPLICATED", "El producto ya tiene ese impuesto.");

    public static readonly Error BarcodeRulePrefixDuplicated =
        Error.Conflict("CATALOG.BARCODE_RULE_PREFIX_DUPLICATED", "Ya existe una regla de báscula con ese prefijo.");

    public static readonly Error PeriodOverlap =
        Error.Conflict("CATALOG.PERIOD_OVERLAP", "La vigencia se cruza con otra existente para la misma combinación.");

    public static readonly Error InvalidSku = Error.Validation(
        "CATALOG.INVALID_SKU", "El SKU admite de 1 a 40 letras, dígitos o . _ / - (sin espacios) y debe empezar por letra o dígito.");

    public static readonly Error InvalidName = Error.Validation("CATALOG.INVALID_NAME", "El nombre es obligatorio y no puede superar el largo permitido.");

    public static readonly Error InvalidBarcode = Error.Validation(
        "CATALOG.INVALID_BARCODE", "El código de barras admite de 1 a 50 letras, dígitos o . _ / - .");

    public static readonly Error InvalidCheckDigit =
        Error.Validation("CATALOG.INVALID_CHECK_DIGIT", "El dígito de control del código de barras no es correcto (código mal leído o mal digitado).");

    public static readonly Error InvalidPlu = Error.Validation("CATALOG.INVALID_PLU", "El PLU de báscula tiene de 1 a 6 dígitos.");

    public static readonly Error UnitDoesNotMatchSaleMode = Error.Validation(
        "CATALOG.UNIT_SALE_MODE_MISMATCH", "La unidad base no corresponde al modo de venta (unidad, peso o volumen).");

    public static readonly Error ScaleRequiresWeight = Error.Validation(
        "CATALOG.SCALE_REQUIRES_WEIGHT", "Un producto de báscula se vende por peso en kilogramos y necesita su PLU.");

    public static readonly Error ServiceCannotTrackLots =
        Error.Validation("CATALOG.SERVICE_CANNOT_TRACK_LOTS", "Un servicio no maneja lotes ni vencimientos.");

    public static readonly Error ExpiryRequiresLots =
        Error.Validation("CATALOG.EXPIRY_REQUIRES_LOTS", "Para controlar vencimientos el producto debe manejar lotes.");

    public static readonly Error InvalidNetContent =
        Error.Validation("CATALOG.INVALID_NET_CONTENT", "El contenido neto debe ser mayor que cero y llevar su unidad.");

    public static readonly Error BaseUnitLocked = Error.Conflict(
        "CATALOG.BASE_UNIT_LOCKED", "El producto ya tiene movimientos de inventario: su unidad base y su tipo no se pueden cambiar (cree un producto nuevo).");

    public static readonly Error ProductHasStock = Error.BusinessRule(
        "CATALOG.PRODUCT_HAS_STOCK", "El producto tiene existencias: ajústelas a cero antes de descontinuarlo.");

    public static readonly Error CategoryTooDeep = Error.BusinessRule("CATALOG.CATEGORY_TOO_DEEP", "Las categorías admiten hasta 4 niveles.");

    public static readonly Error CategoryCycle =
        Error.BusinessRule("CATALOG.CATEGORY_CYCLE", "Una categoría no puede quedar dentro de sí misma ni de sus subcategorías.");

    public static readonly Error CategoryInUse = Error.BusinessRule(
        "CATALOG.CATEGORY_IN_USE", "La categoría tiene subcategorías o productos: muévalos antes de eliminarla.");

    public static readonly Error BrandInUse = Error.BusinessRule("CATALOG.BRAND_IN_USE", "La marca está asignada a productos.");

    public static readonly Error InvalidTaxCode = Error.Validation("CATALOG.INVALID_TAX_CODE", "El código del impuesto tiene de 2 a 20 mayúsculas, dígitos o _.");

    public static readonly Error ZeroRatedMustBeVat =
        Error.Validation("CATALOG.ZERO_RATED_MUST_BE_VAT", "Solo un IVA porcentual puede ser exento o excluido (y no ambos).");

    public static readonly Error InvalidTaxRate = Error.Validation(
        "CATALOG.INVALID_TAX_RATE", "La tarifa no corresponde al impuesto: porcentaje de 0 a 100 o valor fijo mayor o igual a cero (exento y excluido: 0 %).");

    public static readonly Error SystemTaxImmutable =
        Error.BusinessRule("CATALOG.SYSTEM_TAX", "Los impuestos del sistema no cambian de código, tipo ni forma de cálculo.");

    public static readonly Error OneVatPerProduct = Error.BusinessRule("CATALOG.ONE_VAT_PER_PRODUCT", "Un producto tiene un solo IVA.");

    public static readonly Error FixedAmountOnlyForFixedTaxes = Error.Validation(
        "CATALOG.FIXED_AMOUNT_NOT_ALLOWED", "El valor propio por unidad solo aplica a impuestos de valor fijo.");

    public static readonly Error TaxInactive = Error.BusinessRule("CATALOG.TAX_INACTIVE", "El impuesto está inactivo.");

    public static readonly Error InvalidFactor = Error.Validation(
        "CATALOG.INVALID_FACTOR", "El factor de la presentación es mayor que cero, con hasta 4 decimales (RN-CAT-04).");

    public static readonly Error InvalidPrice = Error.Validation("CATALOG.INVALID_PRICE", "El precio es mayor o igual a cero, con hasta 2 decimales.");

    public static readonly Error PriceBelowCost = Error.BusinessRule(
        "CATALOG.PRICE_BELOW_COST", "El precio (sin impuestos) queda por debajo del costo promedio y la empresa no lo permite (RN-CAT-06).");

    public static readonly Error PriceNotScheduled =
        Error.BusinessRule("CATALOG.PRICE_NOT_SCHEDULED", "Solo se puede cancelar un precio programado que aún no ha entrado en vigencia.");

    public static readonly Error PriceInThePast =
        Error.Validation("CATALOG.PRICE_IN_THE_PAST", "La vigencia de un precio nuevo no puede empezar en el pasado.");

    public static readonly Error PackagingNotSellable = Error.BusinessRule("CATALOG.PACKAGING_NOT_SELLABLE", "La presentación no se vende.");

    public static readonly Error DefaultPriceListRequired =
        Error.BusinessRule("CATALOG.DEFAULT_PRICE_LIST", "La lista por defecto no se puede inactivar: marque otra como predeterminada primero.");

    public static readonly Error InvalidBarcodeRule = Error.Validation(
        "CATALOG.INVALID_BARCODE_RULE", "Regla inválida: prefijo de 20 a 28 y posiciones de PLU y valor dentro de los 12 primeros dígitos, sin cruzarse.");

    public static readonly Error PrefixReservedForInternalCodes =
        Error.Validation("CATALOG.PREFIX_RESERVED", "El prefijo 29 está reservado para los códigos internos que genera el sistema.");

    public static readonly Error InternalCodesExhausted =
        Error.BusinessRule("CATALOG.INTERNAL_CODES_EXHAUSTED", "Se agotaron los códigos internos de este nodo.");

    public static readonly Error ImportNotFound = Error.NotFound("CATALOG.IMPORT_NOT_FOUND", "La importación no existe.");

    public static readonly Error ImportHasErrors = Error.BusinessRule(
        "CATALOG.IMPORT_HAS_ERRORS", "El archivo tiene filas con errores: corríjalas y vuelva a subirlo. No se aplicó nada.");

    public static readonly Error ImportAlreadyApplied =
        Error.Conflict("CATALOG.IMPORT_ALREADY_APPLIED", "La importación ya se aplicó o se descartó.");

    public static readonly Error ImportMissingColumns =
        Error.Validation("CATALOG.IMPORT_MISSING_COLUMNS", "Faltan columnas obligatorias en el archivo.");
}
