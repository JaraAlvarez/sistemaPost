using System.Globalization;
using System.Reflection;

namespace Pos.Modules.Licensing.Infrastructure;

/// <summary>
/// Fecha de compilación del programa (atributo <c>PosBuildDate</c>, día UTC, lo pone el .csproj). Ningún inicio de demo anterior es
/// creíble: el programa no existía (docs/fases/fase-demo-por-equipo.md §2.3).
/// </summary>
internal static class LicenseBuild
{
    public static DateTimeOffset? Date { get; } =
        typeof(LicenseBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "PosBuildDate")?.Value is { } text
        && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
}
