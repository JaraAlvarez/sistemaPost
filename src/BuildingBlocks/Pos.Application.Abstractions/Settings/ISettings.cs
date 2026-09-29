namespace Pos.Application.Abstractions.Settings;

/// <summary>Nivel del que proviene el valor efectivo de una configuración.</summary>
public enum SettingSource
{
    Default,
    Company,
    Branch,
    Terminal,
}

/// <summary>Valor efectivo con su origen y el valor que heredaría si se eliminara la excepción.</summary>
public sealed record ResolvedSetting<T>(T Value, SettingSource Source, T InheritedValue, SettingSource InheritedFrom);

/// <summary>Lectura de configuración resuelta: caja → sucursal → empresa → valor por defecto (cacheada).</summary>
public interface ISettingsReader
{
    Task<ResolvedSetting<T>> ResolveAsync<T>(
        SettingDefinition<T> definition, SettingContext context, CancellationToken cancellationToken = default);

    async Task<T> GetAsync<T>(SettingDefinition<T> definition, SettingContext context, CancellationToken cancellationToken = default) =>
        (await ResolveAsync(definition, context, cancellationToken)).Value;
}

/// <summary>Catálogo de todas las definiciones declaradas por los módulos.</summary>
public interface ISettingsCatalog
{
    IReadOnlyCollection<SettingDefinition> All { get; }

    SettingDefinition? Find(string key);
}

/// <summary>Aporta definiciones de configuración al catálogo (cada módulo registra la suya).</summary>
public interface ISettingDefinitionProvider
{
    IEnumerable<SettingDefinition> GetDefinitions();
}
