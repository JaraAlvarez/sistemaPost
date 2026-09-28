using Pos.Api.Abstractions;

namespace Pos.Server.Host.Modules;

/// <summary>
/// Lista explícita de módulos de negocio cargados por el servidor. Cada fase agrega aquí su módulo
/// (una línea). Se prefiere una lista explícita a descubrir módulos por reflexión: es visible,
/// ordenada y no carga código inesperado.
/// </summary>
internal static class ModuleCatalog
{
    public static IReadOnlyList<IModule> All { get; } =
    [
        // Fase 2: new OrganizationModule(), new SettingsModule(), …
    ];
}
