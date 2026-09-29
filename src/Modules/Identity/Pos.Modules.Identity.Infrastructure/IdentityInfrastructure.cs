using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Identity.Application;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Identity.Infrastructure;

/// <summary>Fila de solo lectura del catálogo <c>identity.permissions</c> (se escribe únicamente por migraciones).</summary>
internal sealed class PermissionRecord
{
    public string Code { get; set; } = string.Empty;

    public string Module { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsSensitive { get; set; }

    public bool IsDeprecated { get; set; }
}

internal sealed class IdentityModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users", "identity");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Role>(b =>
        {
            b.ToTable("roles", "identity");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasControlColumns();
            b.HasXminConcurrency();
            b.OwnsMany(x => x.Permissions, p =>
            {
                p.ToTable("role_permissions", "identity");
                p.WithOwner().HasForeignKey("RoleId");
                p.Property(x => x.PermissionCode).HasColumnName("permission_code");
                p.HasKey("RoleId", nameof(RoleGrant.PermissionCode));
            });
            b.Navigation(x => x.Permissions).HasField("_permissions");
        });

        modelBuilder.Entity<PermissionRecord>(b =>
        {
            b.ToTable("permissions", "identity");
            b.HasKey(x => x.Code);
        });
    }
}

internal sealed class IdentityStore(PosDbContext context) : IIdentityStore
{
    public void Add(User user) => context.Add(user);

    public void Add(Role role) => context.Add(role);

    public async Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken cancellationToken) =>
        await context.Set<PermissionRecord>().AsNoTracking()
            .OrderBy(p => p.Code)
            .Select(p => new PermissionDto(p.Code, p.Module, p.Description, p.IsSensitive, p.IsDeprecated))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> ListActivePermissionCodesAsync(CancellationToken cancellationToken) =>
        await context.Set<PermissionRecord>().AsNoTracking()
            .Where(p => !p.IsDeprecated)
            .OrderBy(p => p.Code)
            .Select(p => p.Code)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var roles = await context.Set<Role>().AsNoTracking().OrderBy(r => r.Code).ToListAsync(cancellationToken);
        return roles
            .Select(r => new RoleDto(r.Id, r.Code, r.Name, r.Description, r.IsSystem,
                [.. r.Permissions.Select(p => p.PermissionCode).Order(StringComparer.Ordinal)]))
            .ToList();
    }
}

internal sealed class IdentityConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_users__company_username"] = Error.Conflict("IDENTITY.USERNAME_DUPLICATED", "Ya existe un usuario con ese nombre."),
        ["ux_roles__company_code"] = Error.Conflict("IDENTITY.ROLE_CODE_DUPLICATED", "Ya existe un rol con ese código."),
        ["fk_role_permissions__permission"] = Error.Validation("IDENTITY.UNKNOWN_PERMISSION", "Uno de los permisos no existe en el catálogo."),
    };
}
