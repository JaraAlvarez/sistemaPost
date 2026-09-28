using Serilog.Core;
using Serilog.Events;

namespace Pos.Server.Host.Logging;

/// <summary>
/// Red de seguridad: si una propiedad de log tiene nombre de dato sensible (contraseña, PIN, token…),
/// su valor se reemplaza por <c>***</c> antes de escribirse. La regla primaria sigue siendo no registrarlos.
/// </summary>
internal sealed class SensitiveDataMaskingEnricher : ILogEventEnricher
{
    public const string Mask = "***";

    // Se buscan en cualquier parte del nombre.
    private static readonly string[] SensitiveFragments =
        ["password", "contrasena", "token", "secret", "authorization", "cardnumber", "apikey"];

    // Palabras cortas: solo al final del nombre ("Pin", "UserPin"), para no enmascarar "Mapping" o "Shipping".
    private static readonly string[] SensitiveSuffixes = ["pin", "pincode", "cvv"];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var sensitive = logEvent.Properties.Keys.Where(IsSensitive).ToList();
        foreach (var name in sensitive)
        {
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, Mask));
        }
    }

    public static bool IsSensitive(string propertyName) =>
        SensitiveFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        || SensitiveSuffixes.Any(suffix => propertyName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
