using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Identity.Contracts;

/// <summary>Permisos del módulo Identity.</summary>
public static class IdentityPermissions
{
    public const string PermissionView = "identity.permission.view";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(PermissionView, "Consultar el catálogo de permisos y los roles", isSensitive: false),
    ];
}

/// <summary>
/// Alta de la estructura de identidad de una empresa nueva (lo usa el asistente inicial del módulo Organization):
/// usuario técnico <c>system</c> y los 7 roles de sistema con sus permisos.
/// </summary>
public interface IIdentityProvisioning
{
    Task ProvisionCompanyAsync(Guid companyId, Guid systemUserId, CancellationToken cancellationToken = default);
}

/// <summary>Resumen de un rol para la API.</summary>
public sealed record RoleDto(Guid Id, string Code, string Name, string? Description, bool IsSystem, IReadOnlyList<string> Permissions);

/// <summary>Permiso del catálogo para la API.</summary>
public sealed record PermissionDto(string Code, string Module, string Description, bool IsSensitive, bool IsDeprecated);
