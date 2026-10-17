using Pos.SharedKernel.Text;

namespace Pos.Modules.Organization.Domain;

/// <summary>
/// Tipo de negocio de la empresa (Fase 7-C parte A, docs/fases/fase-07c-propuesta.md §1). NO es un plan de precios (ADR-0015): solo
/// ordena pantallas, menús y valores iniciales. En la API viaja como MAYÚSCULAS_CON_GUION (<c>FRESH_MARKET</c>).
/// </summary>
public enum BusinessProfile
{
    /// <summary>Supermercado o minimercado.</summary>
    Supermarket,

    /// <summary>Fruver o carnicería.</summary>
    FreshMarket,

    /// <summary>Droguería.</summary>
    Pharmacy,

    /// <summary>Ropa, calzado o deportes.</summary>
    Apparel,

    /// <summary>Ferretería, papelería o miscelánea.</summary>
    Hardware,

    /// <summary>Otro tipo de negocio: lo básico.</summary>
    Other,
}

/// <summary>
/// Función activable del negocio. Apagarla OCULTA pantallas y campos; nunca borra datos. Todas existen en las dos ediciones.
/// </summary>
public enum BusinessFeature
{
    /// <summary>Báscula conectada a la caja, PLU, tara y etiquetas de báscula.</summary>
    Scale,

    /// <summary>Lotes y fechas de vencimiento en los productos.</summary>
    LotsExpiry,

    /// <summary>Tallas y colores (próximamente: se guarda, la interfaz la muestra deshabilitada).</summary>
    Variants,

    /// <summary>Venta por metro o fracción: cantidades con decimales en productos por unidad.</summary>
    FractionalSale,

    /// <summary>Precio abierto: el cajero escribe el precio al vender.</summary>
    OpenPrice,

    /// <summary>Promociones y descuentos automáticos.</summary>
    Promotions,

    /// <summary>Compras, proveedores y cuentas por pagar.</summary>
    Purchasing,
}

/// <summary>Valores iniciales de cada tipo de negocio y lectura/escritura de los códigos.</summary>
public static class BusinessProfiles
{
    /// <summary>Tipo que se usa cuando el instalador no lo envía (instaladores anteriores a 7-C): el producto nació para supermercados.</summary>
    public const BusinessProfile Default = BusinessProfile.Supermarket;

    /// <summary>Funciones que aún no existen: se guardan, pero la interfaz las muestra deshabilitadas.</summary>
    public static IReadOnlySet<BusinessFeature> ComingSoon { get; } = new HashSet<BusinessFeature> { BusinessFeature.Variants };

    /// <summary>
    /// Lo que tenían las empresas que ya existían antes de los perfiles (migración V2026.10.043): todo lo que se veía sigue visible.
    /// </summary>
    public static IReadOnlySet<BusinessFeature> Legacy { get; } = new HashSet<BusinessFeature>
    {
        BusinessFeature.Scale, BusinessFeature.LotsExpiry, BusinessFeature.FractionalSale, BusinessFeature.OpenPrice,
        BusinessFeature.Promotions, BusinessFeature.Purchasing,
    };

    public static IReadOnlyList<BusinessProfile> AllProfiles { get; } = Enum.GetValues<BusinessProfile>();

    public static IReadOnlyList<BusinessFeature> AllFeatures { get; } = Enum.GetValues<BusinessFeature>();

    /// <summary>Funciones que enciende cada tipo de negocio (tabla del informe 7-C parte A).</summary>
    public static IReadOnlySet<BusinessFeature> Preset(BusinessProfile profile) => profile switch
    {
        BusinessProfile.Supermarket => Set(BusinessFeature.Scale, BusinessFeature.LotsExpiry, BusinessFeature.Promotions, BusinessFeature.Purchasing),
        BusinessProfile.FreshMarket => Set(BusinessFeature.Scale, BusinessFeature.Promotions, BusinessFeature.Purchasing),
        BusinessProfile.Pharmacy => Set(BusinessFeature.LotsExpiry, BusinessFeature.Promotions, BusinessFeature.Purchasing),
        BusinessProfile.Apparel => Set(BusinessFeature.Variants, BusinessFeature.Promotions, BusinessFeature.Purchasing),
        BusinessProfile.Hardware => Set(BusinessFeature.FractionalSale, BusinessFeature.OpenPrice, BusinessFeature.Promotions, BusinessFeature.Purchasing),
        _ => Set(BusinessFeature.Promotions, BusinessFeature.Purchasing),
    };

    /// <summary>Acepta <c>FRESH_MARKET</c>, <c>FreshMarket</c> o <c>freshMarket</c>.</summary>
    public static bool TryParseProfile(string? code, out BusinessProfile profile) =>
        Enum.TryParse(EnumText.ToEnumName(code), ignoreCase: false, out profile) && Enum.IsDefined(profile) && !string.IsNullOrWhiteSpace(code)
        && !char.IsAsciiDigit(code.Trim()[0]);

    /// <summary>Acepta <c>LOTS_EXPIRY</c>, <c>LotsExpiry</c> o <c>lotsExpiry</c>.</summary>
    public static bool TryParseFeature(string? code, out BusinessFeature feature) =>
        Enum.TryParse(EnumText.ToEnumName(code), ignoreCase: false, out feature) && Enum.IsDefined(feature) && !string.IsNullOrWhiteSpace(code)
        && !char.IsAsciiDigit(code.Trim()[0]);

    /// <summary>Texto guardado en la base de datos: códigos ordenados y separados por coma (<c>LOTS_EXPIRY,SCALE</c>).</summary>
    public static string Format(IEnumerable<BusinessFeature> features) =>
        string.Join(',', features.Distinct().Select(f => f.UpperSnake()).Order(StringComparer.Ordinal));

    /// <summary>Lee el texto guardado; los códigos desconocidos (de una versión más nueva) se ignoran.</summary>
    public static IReadOnlySet<BusinessFeature> Parse(string? text)
    {
        var set = new HashSet<BusinessFeature>();
        foreach (var code in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseFeature(code, out var feature))
            {
                set.Add(feature);
            }
        }

        return set;
    }

    private static HashSet<BusinessFeature> Set(params BusinessFeature[] features) => [.. features];
}
