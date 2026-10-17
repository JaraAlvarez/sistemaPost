namespace Pos.Modules.Organization.Contracts;

/// <summary>
/// Códigos y textos de los tipos de negocio y de las funciones activables (Fase 7-C parte A). Los usan la interfaz (Configuración →
/// Funciones del negocio) y los demás módulos. Los códigos viajan como MAYÚSCULAS_CON_GUION.
/// </summary>
public static class BusinessCatalog
{
    public const string Supermarket = "SUPERMARKET";
    public const string FreshMarket = "FRESH_MARKET";
    public const string Pharmacy = "PHARMACY";
    public const string Apparel = "APPAREL";
    public const string Hardware = "HARDWARE";
    public const string Other = "OTHER";

    public const string Scale = "SCALE";
    public const string LotsExpiry = "LOTS_EXPIRY";
    public const string Variants = "VARIANTS";
    public const string FractionalSale = "FRACTIONAL_SALE";
    public const string OpenPrice = "OPEN_PRICE";
    public const string Promotions = "PROMOTIONS";
    public const string Purchasing = "PURCHASING";

    /// <summary>Tipos de negocio en el orden en que se muestran.</summary>
    public static IReadOnlyList<BusinessProfileInfo> Profiles { get; } =
    [
        new(Supermarket, "Supermercado / minimercado", "Báscula, lotes y vencimientos, promociones y compras."),
        new(FreshMarket, "Fruver / carnicería", "Báscula conectada y de etiqueta, venta por kilo o libra, tara."),
        new(Pharmacy, "Droguería", "Lotes y fechas de vencimiento en cada producto."),
        new(Apparel, "Ropa / calzado / deportes", "Tallas y colores (en una próxima versión) y cambios."),
        new(Hardware, "Ferretería / papelería / miscelánea", "Venta por metro o fracción y precio abierto."),
        new(Other, "Otro tipo de negocio", "Lo básico: ventas, inventario, compras y promociones."),
    ];

    /// <summary>Funciones activables en el orden en que se muestran, con su explicación de una línea.</summary>
    public static IReadOnlyList<BusinessFeatureInfo> Features { get; } =
    [
        new(Scale, "Báscula y PLU", "Leer el peso de la báscula en la caja, códigos PLU, tara y etiquetas de báscula.", ComingSoon: false),
        new(LotsExpiry, "Lotes y vencimientos", "Registrar el lote y la fecha de vencimiento de los productos que lo necesitan.", ComingSoon: false),
        new(FractionalSale, "Venta por metro o fracción", "Vender con decimales productos que van por unidad (1,5 metros de cable).", ComingSoon: false),
        new(OpenPrice, "Precio abierto", "Productos sin precio fijo: el cajero escribe el precio al venderlos.", ComingSoon: false),
        new(Promotions, "Promociones", "Descuentos y ofertas automáticas en la caja (2x1, porcentaje, combos).", ComingSoon: false),
        new(Purchasing, "Compras y proveedores", "Órdenes de compra, recepción de mercancía, proveedores y cuentas por pagar.", ComingSoon: false),
        new(Variants, "Tallas y colores", "Un mismo producto en varias tallas y colores, cada uno con su existencia.", ComingSoon: true),
    ];

    public static BusinessProfileInfo? Profile(string? code) => Profiles.FirstOrDefault(p => p.Code == code);

    public static BusinessFeatureInfo? Feature(string? code) => Features.FirstOrDefault(f => f.Code == code);
}

public sealed record BusinessProfileInfo(string Code, string Name, string Description);

/// <summary><c>ComingSoon</c>: la función aún no existe; se guarda, pero la interfaz la muestra deshabilitada ("Disponible en una próxima versión").</summary>
public sealed record BusinessFeatureInfo(string Code, string Name, string Description, bool ComingSoon);

/// <summary>Tipo de negocio y funciones encendidas de la empresa (códigos de <see cref="BusinessCatalog"/>).</summary>
public sealed record BusinessFeaturesDto(string Profile, IReadOnlyList<string> EnabledFeatures)
{
    public bool Has(string feature) => EnabledFeatures.Contains(feature, StringComparer.Ordinal);
}

/// <summary>Cambia el tipo de negocio: los interruptores vuelven a los valores de ese tipo.</summary>
public sealed record ChangeBusinessProfileRequest(string Profile);

/// <summary>Funciones que quedan encendidas (las demás se apagan: se ocultan, los datos se conservan).</summary>
public sealed record SetBusinessFeaturesRequest(IReadOnlyList<string> EnabledFeatures);

/// <summary>
/// Lectura del tipo de negocio y de las funciones para los demás módulos (identidad, catálogo, licencias). Devuelve <c>null</c> mientras
/// la instalación no está configurada.
/// </summary>
public interface IBusinessFeatures
{
    Task<BusinessFeaturesDto?> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Datos de ejemplo según el tipo de negocio (Fase 7-C parte A), p. ej. categorías iniciales del catálogo. El asistente inicial los llama
/// SOLO cuando el instalador eligió el tipo de negocio, en la misma transacción. Cada módulo solo crea lo suyo si está vacío.
/// </summary>
public interface IBusinessStarterData
{
    Task SeedAsync(Guid companyId, string profile, CancellationToken cancellationToken);
}
