using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Enumeraciones del dominio ↔ <c>varchar</c> + CHECK en MAYÚSCULAS_CON_GUIONES (convención: no se usan enums de
/// PostgreSQL). <c>SalesFloor</c> ↔ <c>'SALES_FLOOR'</c>.
/// </summary>
public sealed class UpperSnakeEnumConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToDb =
        Enum.GetValues<TEnum>().ToDictionary(v => v, v => PosSaveChangesInterceptor.ToUpperSnake(v.ToString()));

    private static readonly Dictionary<string, TEnum> FromDb = ToDb.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public UpperSnakeEnumConverter()
        : base(v => ToDb[v], v => FromDb[v])
    {
    }
}

public static class EnumPropertyExtensions
{
    public static PropertyBuilder<TEnum> HasUpperSnakeConversion<TEnum>(this PropertyBuilder<TEnum> property)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.HasConversion(new UpperSnakeEnumConverter<TEnum>());
    }
}
