using System.Text.Json;
using Pos.SharedKernel.Results;

namespace Pos.Application.Abstractions.Settings;

/// <summary>Excepción de configuración a guardar o eliminar.</summary>
/// <param name="CompanyId">Empresa dueña.</param>
/// <param name="Scope">Nivel (exactamente uno: Company, Branch o Terminal).</param>
/// <param name="ScopeId">Id de la empresa, sucursal o caja según <paramref name="Scope"/>.</param>
public sealed record SettingTarget(Guid CompanyId, SettingScope Scope, Guid ScopeId);

/// <summary>Valor efectivo de una clave con su origen (lo que devuelve la API).</summary>
public sealed record EffectiveSetting(
    string Key,
    string Description,
    JsonElement Value,
    SettingSource Source,
    JsonElement InheritedValue,
    SettingSource InheritedFrom,
    IReadOnlyList<SettingScope> AllowedScopes);

/// <summary>
/// Escritura de configuración: valida clave, alcance y valor; audita el valor anterior y el nuevo (RN-GEN-04) y
/// emite el evento de sincronización <c>settings.setting_changed.v1</c>. Eliminar = volver a heredar.
/// </summary>
public interface ISettingsWriter
{
    Task<Result> SetAsync(SettingTarget target, string key, JsonElement value, CancellationToken cancellationToken = default);

    Task<Result> RemoveAsync(SettingTarget target, string key, CancellationToken cancellationToken = default);

    /// <summary>Todos los valores efectivos para un contexto (con su origen).</summary>
    Task<IReadOnlyList<EffectiveSetting>> GetEffectiveAsync(SettingContext context, CancellationToken cancellationToken = default);
}
