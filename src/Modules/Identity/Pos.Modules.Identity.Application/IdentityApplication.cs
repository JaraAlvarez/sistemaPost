using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Identity.Application;

/// <summary>Puerto de persistencia del módulo.</summary>
public interface IIdentityStore
{
    void Add(User user);

    void Add(Role role);

    Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken cancellationToken);

    /// <summary>Códigos vigentes (no obsoletos) del catálogo de la BD.</summary>
    Task<IReadOnlyList<string>> ListActivePermissionCodesAsync(CancellationToken cancellationToken);
}

/// <summary>Usuario system + 7 roles de sistema con los permisos vigentes del catálogo.</summary>
public sealed class IdentityProvisioning(IIdentityStore store, IIdGenerator ids) : IIdentityProvisioning
{
    public async Task ProvisionCompanyAsync(Guid companyId, Guid systemUserId, CancellationToken cancellationToken = default)
    {
        store.Add(User.CreateSystem(systemUserId, companyId));

        var permissions = await store.ListActivePermissionCodesAsync(cancellationToken);
        foreach (var definition in SystemRoles.Build(permissions))
        {
            store.Add(Role.CreateSystem(ids.NewId(), companyId, definition));
        }
    }
}

/// <summary>Todos los permisos declarados por los módulos (fuente de verdad del catálogo de la BD).</summary>
public sealed class IdentityPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => IdentityPermissions.All;
}

public sealed record ListPermissionsQuery : IQuery<IReadOnlyList<PermissionDto>>;

internal sealed class ListPermissionsHandler(IIdentityStore store) : IQueryHandler<ListPermissionsQuery, IReadOnlyList<PermissionDto>>
{
    public async Task<Result<IReadOnlyList<PermissionDto>>> Handle(ListPermissionsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await store.ListPermissionsAsync(cancellationToken));
}

public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleDto>>;

internal sealed class ListRolesHandler(IIdentityStore store) : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    public async Task<Result<IReadOnlyList<RoleDto>>> Handle(ListRolesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await store.ListRolesAsync(cancellationToken));
}
