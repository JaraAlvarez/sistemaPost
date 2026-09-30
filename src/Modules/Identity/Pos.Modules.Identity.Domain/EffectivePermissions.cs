namespace Pos.Modules.Identity.Domain;

/// <summary>Rol asignado con sus permisos, para el cálculo.</summary>
public sealed record AssignedRole(Guid? BranchId, IReadOnlyCollection<string> Permissions);

/// <summary>Excepción de permiso, para el cálculo.</summary>
public sealed record PermissionOverrideRule(string PermissionCode, OverrideEffect Effect, Guid? BranchId);

/// <summary>
/// Permiso efectivo (doc 06, D3-06):
/// (permisos de los roles que aplican en la sucursal ∪ GRANT que aplican) − DENY que aplican, dentro del catálogo vigente.
/// Un rol o excepción sin sucursal aplica en todas. DENY siempre gana.
/// </summary>
public static class EffectivePermissions
{
    public static IReadOnlySet<string> Compute(
        IEnumerable<AssignedRole> roles,
        IEnumerable<PermissionOverrideRule> exceptions,
        Guid? branchId,
        IReadOnlySet<string> activeCatalog)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(exceptions);
        ArgumentNullException.ThrowIfNull(activeCatalog);

        bool Applies(Guid? scope) => scope is null || scope == branchId;

        var result = roles.Where(r => Applies(r.BranchId)).SelectMany(r => r.Permissions).ToHashSet(StringComparer.Ordinal);
        var applicable = exceptions.Where(e => Applies(e.BranchId)).ToList();
        result.UnionWith(applicable.Where(e => e.Effect == OverrideEffect.Grant).Select(e => e.PermissionCode));
        result.ExceptWith(applicable.Where(e => e.Effect == OverrideEffect.Deny).Select(e => e.PermissionCode));
        result.IntersectWith(activeCatalog);
        return result;
    }
}

/// <summary>Empleado: la persona detrás de un usuario (datos mínimos, sin nómina).</summary>
[SharedKernel.Domain.Audited("identity")]
public sealed class Employee : SharedKernel.Domain.AggregateRoot<Guid>,
    SharedKernel.Domain.ICompanyOwned, SharedKernel.Domain.ISoftDeletable, SharedKernel.Domain.ISyncVersioned, SharedKernel.Domain.IHasAuditLabel
{
    private Employee(Guid id, Guid companyId)
        : base(id) => CompanyId = companyId;

    public Guid CompanyId { get; private set; }

    public Guid? BranchId { get; private set; }

    public string IdentificationType { get; private set; } = string.Empty;

    public string IdentificationNumber { get; private set; } = string.Empty;

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    [SharedKernel.Domain.PersonalData(SharedKernel.Domain.PersonalDataKind.Phone)]
    public string? Phone { get; private set; }

    [SharedKernel.Domain.PersonalData(SharedKernel.Domain.PersonalDataKind.Email)]
    public string? Email { get; private set; }

    public EmployeeStatus Status { get; private set; } = EmployeeStatus.Active;

    public string AuditLabel => $"Empleado {FirstName} {LastName} ({IdentificationType} {IdentificationNumber})";

    public static Employee Create(
        Guid id, Guid companyId, Guid? branchId, string identificationType, string identificationNumber,
        string firstName, string lastName, string? phone, string? email)
    {
        var employee = new Employee(id, companyId) { IdentificationType = identificationType, IdentificationNumber = identificationNumber.Trim() };
        employee.Update(branchId, firstName, lastName, phone, email);
        return employee;
    }

    public void Update(Guid? branchId, string firstName, string lastName, string? phone, string? email)
    {
        BranchId = branchId;
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        Phone = phone?.Trim();
        Email = email?.Trim();
    }

    public void Deactivate() => Status = EmployeeStatus.Inactive;

    public void Activate() => Status = EmployeeStatus.Active;
}

public enum EmployeeStatus
{
    Active,
    Inactive,
}
