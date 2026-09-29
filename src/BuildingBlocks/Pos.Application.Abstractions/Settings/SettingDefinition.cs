using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pos.Application.Abstractions.Settings;

/// <summary>Niveles donde se puede guardar una excepción de una configuración.</summary>
[Flags]
public enum SettingScope
{
    None = 0,
    Company = 1,
    Branch = 2,
    Terminal = 4,
}

/// <summary>Contexto de resolución. La resolución empieza en el nivel más específico presente.</summary>
public sealed record SettingContext(Guid CompanyId, Guid? BranchId = null, Guid? PosTerminalId = null);

/// <summary>
/// Definición de una configuración: se declara UNA vez en el código del módulo; la BD solo guarda excepciones
/// (docs/fases/fase-02-propuesta.md §13 y revisión §5). El valor por defecto de una clave publicada no se cambia.
/// </summary>
public abstract partial class SettingDefinition
{
    protected SettingDefinition(string key, string description, SettingScope scopes, string managePermission)
    {
        if (!KeyPattern().IsMatch(key ?? string.Empty))
        {
            throw new ArgumentException($"Clave de configuración inválida '{key}'.", nameof(key));
        }

        if (scopes == SettingScope.None)
        {
            throw new ArgumentException("La configuración debe admitir al menos un alcance.", nameof(scopes));
        }

        Key = key!;
        Description = description;
        Scopes = scopes;
        ManagePermission = managePermission;
    }

    public string Key { get; }

    public string Description { get; }

    public SettingScope Scopes { get; }

    public string ManagePermission { get; }

    public abstract Type ValueType { get; }

    public abstract JsonElement DefaultJson { get; }

    /// <summary>Convierte y valida un valor recibido en JSON. Devuelve el mensaje de error o <c>null</c> si es válido.</summary>
    public abstract string? Validate(JsonElement value);

    [GeneratedRegex(@"^[a-z]+(\.[a-z_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>Configuración tipada.</summary>
public sealed class SettingDefinition<T> : SettingDefinition
{
    private readonly Func<T, string?>? _validator;

    public SettingDefinition(
        string key,
        T defaultValue,
        SettingScope scopes,
        string description,
        Func<T, string?>? validator = null,
        string managePermission = "settings.setting.manage")
        : base(key, description, scopes, managePermission)
    {
        _validator = validator;
        var error = validator?.Invoke(defaultValue);
        if (error is not null)
        {
            throw new ArgumentException($"El valor por defecto de '{key}' no es válido: {error}", nameof(defaultValue));
        }

        DefaultValue = defaultValue;
        DefaultJson = JsonSerializer.SerializeToElement(defaultValue, SettingJson.Options);
    }

    public T DefaultValue { get; }

    public override Type ValueType => typeof(T);

    public override JsonElement DefaultJson { get; }

    public T Deserialize(JsonElement value) =>
        value.Deserialize<T>(SettingJson.Options) ?? throw new JsonException($"Valor nulo para '{Key}'.");

    public override string? Validate(JsonElement value)
    {
        T typed;
        try
        {
            typed = Deserialize(value);
        }
        catch (JsonException)
        {
            return $"Se esperaba un valor de tipo {typeof(T).Name}.";
        }

        return _validator?.Invoke(typed);
    }
}

/// <summary>Opciones JSON de los valores de configuración (estrictas: sin conversiones implícitas de texto a número).</summary>
public static class SettingJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
