using Pos.SharedKernel.Domain;

namespace Pos.Modules.Identity.Domain;

/// <summary>Permiso concedido a un rol (tabla puente identity.role_permissions).</summary>
public sealed record RoleGrant(string PermissionCode);

/// <summary>Rol de una empresa. Los de sistema no se editan ni se borran: se clonan (Fase 3).</summary>
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

    public string AuditLabel => $"Rol {Code} · {Name}";

    public static Role CreateSystem(Guid id, Guid companyId, SystemRoleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var role = new Role(id, companyId, definition.Code, definition.Name, definition.Description, isSystem: true);
        role._permissions.AddRange(definition.Permissions.Distinct(StringComparer.Ordinal).Select(p => new RoleGrant(p)));
        return role;
    }
}

/// <summary>Usuario de la empresa. En la Fase 2 solo se crea el usuario técnico <c>system</c>; el login llega en la Fase 3.</summary>
[Audited("identity")]
public sealed class User : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const string SystemUsername = "system";
    public const string SystemDisplayName = "Sistema";

    private User(Guid id, Guid companyId, string username, string displayName, UserKind kind, UserStatus status)
        : base(id)
    {
        CompanyId = companyId;
        Username = username;
        DisplayName = displayName;
        Kind = kind;
        Status = status;
    }

    public Guid CompanyId { get; private set; }

    public string Username { get; private set; }

    public string DisplayName { get; private set; }

    public string? Email { get; private set; }

    public UserKind Kind { get; private set; }

    public UserStatus Status { get; private set; }

    public bool MustChangePassword { get; private set; }

    public string AuditLabel => $"Usuario {Username} · {DisplayName}";

    /// <summary>
    /// Usuario técnico: autor de las semillas, del asistente inicial y de los procesos automáticos.
    /// Deshabilitado y sin credenciales: nunca puede iniciar sesión (lo garantiza también un CHECK en la BD).
    /// </summary>
    public static User CreateSystem(Guid id, Guid companyId) =>
        new(id, companyId, SystemUsername, SystemDisplayName, UserKind.System, UserStatus.Disabled);
}

public enum UserKind
{
    Human,
    System,
}

public enum UserStatus
{
    Active,
    Locked,
    Disabled,
}
