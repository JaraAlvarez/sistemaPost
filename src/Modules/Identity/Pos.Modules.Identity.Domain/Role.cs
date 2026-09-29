using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Identity.Domain;

/// <summary>Permiso concedido a un rol (tabla puente identity.role_permissions).</summary>
public sealed record RoleGrant(string PermissionCode);

/// <summary>Rol de una empresa. Los de sistema no se editan ni se borran: se clonan.</summary>
[Audited("identity")]
public sealed class Role : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private readonly List<RoleGrant> _permissions = [];

    private Role(Guid id, Guid companyId, string code, string name, string? description, bool isSystem)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Name = name;
        Description = description;
        IsSystem = isSystem;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public bool IsSystem { get; private set; }

    public IReadOnlyCollection<RoleGrant> Permissions => _permissions.AsReadOnly();

    public IReadOnlySet<string> PermissionCodes => _permissions.Select(p => p.PermissionCode).ToHashSet(StringComparer.Ordinal);

    public string AuditLabel => $"Rol {Code} · {Name}";

    public static Role CreateSystem(Guid id, Guid companyId, SystemRoleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var role = new Role(id, companyId, definition.Code, definition.Name, definition.Description, isSystem: true);
        role.SetPermissions(definition.Permissions);
        return role;
    }

    public static Result<Role> CreateCustom(Guid id, Guid companyId, string code, string name, string? description, IEnumerable<string> permissions)
    {
        if (!IdentityRules.IsValidRoleCode(code))
        {
            return IdentityErrors.InvalidRoleCode;
        }

        var role = new Role(id, companyId, code, name.Trim(), description?.Trim(), isSystem: false);
        role.SetPermissions(permissions);
        return role;
    }

    /// <summary>Copia editable de un rol (la forma de "modificar" un rol de sistema).</summary>
    public Result<Role> Clone(Guid id, string code, string name) =>
        CreateCustom(id, CompanyId, code, name, Description, _permissions.Select(p => p.PermissionCode));

    public Result Update(string name, string? description, IEnumerable<string> permissions)
    {
        if (IsSystem)
        {
            return IdentityErrors.SystemRoleImmutable;
        }

        Name = name.Trim();
        Description = description?.Trim();
        SetPermissions(permissions);
        return Result.Success();
    }

    /// <summary>Agrega los permisos nuevos de la definición de un rol de sistema (al actualizar el producto).</summary>
    public IReadOnlyList<string> AddMissingPermissions(IEnumerable<string> definition)
    {
        var missing = definition.Where(p => _permissions.All(g => g.PermissionCode != p)).Distinct(StringComparer.Ordinal).ToList();
        _permissions.AddRange(missing.Select(p => new RoleGrant(p)));
        return missing;
    }

    private void SetPermissions(IEnumerable<string> permissions)
    {
        var codes = permissions.Distinct(StringComparer.Ordinal).ToList();
        _permissions.RemoveAll(p => !codes.Contains(p.PermissionCode));
        _permissions.AddRange(codes.Where(c => _permissions.All(p => p.PermissionCode != c)).Select(c => new RoleGrant(c)));
    }
}
