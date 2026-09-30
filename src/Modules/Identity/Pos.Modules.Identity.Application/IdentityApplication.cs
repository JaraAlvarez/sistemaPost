using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Identity.Application;

/// <summary>Usuario system + 7 roles de sistema con los permisos vigentes del catálogo + Propietario.</summary>
public sealed class IdentityProvisioning(
    IIdentityStore store, CredentialPolicy policy, IIdentityState state, IAuditWriter audit, IIdGenerator ids, IClock clock) : IIdentityProvisioning
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

    public async Task<Result<Guid>> CreateOwnerAsync(Guid companyId, OwnerInput owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (await store.UsernameExistsAsync(IdentityRules.NormalizeUsername(owner.Username), cancellationToken))
        {
            return IdentityErrors.UsernameDuplicated;
        }

        var hash = await policy.HashNewPasswordAsync(companyId, owner.Password, owner.Username, cancellationToken);
        if (hash.IsFailure)
        {
            return hash.Error;
        }

        // El Propietario eligió su contraseña: no se le obliga a cambiarla.
        var created = User.CreateHuman(ids.NewId(), companyId, owner.Username, owner.DisplayName, owner.Email, hash.Value, clock.UtcNow, mustChangePassword: false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var user = created.Value;
        store.AddPasswordHistory(ids.NewId(), user.Id, hash.Value, clock.UtcNow);
        if (owner.PosCode is not null)
        {
            var pin = await policy.SetPinAsync(user, owner.PosCode, owner.Pin ?? string.Empty, cancellationToken);
            if (pin.IsFailure)
            {
                return pin.Error;
            }
        }

        var ownerRole = await store.GetRoleByCodeAsync(SystemRoles.Owner, cancellationToken);
        if (ownerRole is null)
        {
            // En el asistente los roles aún no están guardados: se buscan entre los agregados en esta transacción.
            return Error.Unexpected("IDENTITY.OWNER_ROLE_MISSING", "No existe el rol de sistema OWNER.");
        }

        user.ReplaceRoles([(ownerRole.Id, null)], ids.NewId, user.Id, clock.UtcNow);
        store.Add(user);
        await audit.WriteAsync(
            new AuditEntry("identity", "OWNER_CREATED", nameof(User), user.Id, user.AuditLabel,
                $"Se creó el Propietario {user.DisplayName} ({user.Username}).", Severity: AuditSeverity.Warning),
            cancellationToken);
        state.Invalidate();
        return user.Id;
    }
}

/// <summary>Todos los permisos declarados por el módulo Identity.</summary>
public sealed class IdentityPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => IdentityPermissions.All;
}

/// <summary>Al actualizar el producto, los roles de sistema reciben los permisos nuevos de su definición.</summary>
public sealed class SystemRoleSynchronizer(IIdentityStore store, IAuditWriter audit)
{
    public async Task<int> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var catalog = await store.ListActivePermissionCodesAsync(cancellationToken);
        var definitions = SystemRoles.Build(catalog).ToDictionary(d => d.Code, StringComparer.Ordinal);
        var changes = 0;
        foreach (var role in await store.GetSystemRolesAsync(cancellationToken))
        {
            if (!definitions.TryGetValue(role.Code, out var definition))
            {
                continue;
            }

            var added = role.AddMissingPermissions(definition.Permissions);
            if (added.Count == 0)
            {
                continue;
            }

            changes += added.Count;
            await store.BumpSecurityVersionForRoleAsync(role.Id, cancellationToken);
            await audit.WriteAsync(
                new AuditEntry("identity", "SYSTEM_ROLE_UPDATED", nameof(Role), role.Id, role.AuditLabel,
                    $"El rol de sistema {role.Code} recibió {added.Count} permisos nuevos de esta versión: {string.Join(", ", added)}."),
                cancellationToken);
        }

        return changes;
    }
}

// ───────────────────────────── Catálogo y roles ─────────────────────────────

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

public sealed record CreateRoleCommand(string Code, string Name, string? Description, IReadOnlyList<string> Permissions) : ICommand<RoleDto>;

internal sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Description).MaximumLength(250);
        RuleFor(x => x.Permissions).NotNull();
    }
}

internal sealed class CreateRoleHandler(
    Pos.Application.Abstractions.Installation.IInstallationContext installation, IIdentityStore store, RoleRules rules, IIdGenerator ids)
    : ICommandHandler<CreateRoleCommand, RoleDto>
{
    public async Task<Result<RoleDto>> Handle(CreateRoleCommand request, CancellationToken cancellationToken)
    {
        var check = await rules.CheckAsync(request.Code, request.Permissions, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var role = Role.CreateCustom(ids.NewId(), installation.CompanyId!.Value, request.Code, request.Name, request.Description, request.Permissions);
        if (role.IsFailure)
        {
            return role.Error;
        }

        store.Add(role.Value);
        return RoleRules.ToDto(role.Value);
    }
}

/// <summary>Copia editable de un rol (la forma de personalizar un rol de sistema).</summary>
public sealed record CloneRoleCommand(Guid SourceRoleId, string Code, string Name) : ICommand<RoleDto>;

internal sealed class CloneRoleHandler(IIdentityStore store, RoleRules rules, IIdGenerator ids) : ICommandHandler<CloneRoleCommand, RoleDto>
{
    public async Task<Result<RoleDto>> Handle(CloneRoleCommand request, CancellationToken cancellationToken)
    {
        var source = await store.GetRoleAsync(request.SourceRoleId, cancellationToken);
        if (source is null)
        {
            return IdentityErrors.RoleNotFound;
        }

        var check = await rules.CheckAsync(request.Code, source.PermissionCodes, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var clone = source.Clone(ids.NewId(), request.Code, request.Name);
        if (clone.IsFailure)
        {
            return clone.Error;
        }

        store.Add(clone.Value);
        return RoleRules.ToDto(clone.Value);
    }
}

public sealed record UpdateRoleCommand(Guid RoleId, string Name, string? Description, IReadOnlyList<string> Permissions) : ICommand<RoleDto>;

internal sealed class UpdateRoleHandler(IIdentityStore store, RoleRules rules) : ICommandHandler<UpdateRoleCommand, RoleDto>
{
    public async Task<Result<RoleDto>> Handle(UpdateRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await store.GetRoleAsync(request.RoleId, cancellationToken);
        if (role is null)
        {
            return IdentityErrors.RoleNotFound;
        }

        var check = await rules.CheckAsync(null, request.Permissions, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var updated = role.Update(request.Name, request.Description, request.Permissions);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        await store.BumpSecurityVersionForRoleAsync(role.Id, cancellationToken);
        return RoleRules.ToDto(role);
    }
}

public sealed record DeleteRoleCommand(Guid RoleId) : ICommand;

internal sealed class DeleteRoleHandler(IIdentityStore store) : ICommandHandler<DeleteRoleCommand>
{
    public async Task<Result> Handle(DeleteRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await store.GetRoleAsync(request.RoleId, cancellationToken);
        if (role is null)
        {
            return IdentityErrors.RoleNotFound;
        }

        if (role.IsSystem)
        {
            return IdentityErrors.SystemRoleImmutable;
        }

        // Borrado lógico: las asignaciones quedan, pero un rol borrado ya no concede permisos.
        await store.BumpSecurityVersionForRoleAsync(role.Id, cancellationToken);
        store.Remove(role);
        return Result.Success();
    }
}

/// <summary>Reglas comunes al crear y editar roles: código único, permisos existentes y sin escalamiento (RN-SEC-05).</summary>
public sealed class RoleRules(IIdentityStore store, PrivilegeGuard guard)
{
    public async Task<Result> CheckAsync(string? newCode, IEnumerable<string> permissions, CancellationToken cancellationToken)
    {
        if (newCode is not null)
        {
            if (!IdentityRules.IsValidRoleCode(newCode))
            {
                return IdentityErrors.InvalidRoleCode;
            }

            if (await store.RoleCodeExistsAsync(newCode, cancellationToken))
            {
                return Error.Conflict("IDENTITY.ROLE_CODE_DUPLICATED", "Ya existe un rol con ese código.");
            }
        }

        var list = permissions.Distinct(StringComparer.Ordinal).ToList();
        var catalog = (await store.ListActivePermissionCodesAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (list.FirstOrDefault(p => !catalog.Contains(p)) is { } unknown)
        {
            return Error.Validation("IDENTITY.UNKNOWN_PERMISSION", $"El permiso '{unknown}' no existe.");
        }

        return await guard.CanGrantAsync(list, null, cancellationToken);
    }

    public static RoleDto ToDto(Role role) =>
        new(role.Id, role.Code, role.Name, role.Description, role.IsSystem, [.. role.PermissionCodes.Order(StringComparer.Ordinal)]);
}

// ───────────────────────────── Sesiones ─────────────────────────────

public sealed record ListSessionsQuery : IQuery<IReadOnlyList<SessionDto>>;

internal sealed class ListSessionsHandler(ISessionStore sessions, IClock clock) : IQueryHandler<ListSessionsQuery, IReadOnlyList<SessionDto>>
{
    public async Task<Result<IReadOnlyList<SessionDto>>> Handle(ListSessionsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await sessions.ListActiveAsync(clock.UtcNow, cancellationToken));
}

public sealed record RevokeSessionCommand(Guid SessionId) : ICommand, IAllowedWhenRestricted;

internal sealed class RevokeSessionHandler(ISessionStore sessions, ICurrentUser current, IAuditWriter audit) : ICommandHandler<RevokeSessionCommand>
{
    public async Task<Result> Handle(RevokeSessionCommand request, CancellationToken cancellationToken)
    {
        if (!await sessions.RevokeAsync(request.SessionId, current.UserId, "REVOKED_BY_ADMIN", cancellationToken))
        {
            return IdentityErrors.SessionNotFound;
        }

        await audit.WriteAsync(
            new AuditEntry("identity", "SESSION_REVOKED", "Session", request.SessionId, Summary: $"{current.DisplayName} cerró una sesión.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }
}

// ───────────────────────────── Empleados ─────────────────────────────

public sealed record ListEmployeesQuery : IQuery<IReadOnlyList<EmployeeDto>>;

internal sealed class ListEmployeesHandler(IIdentityStore store) : IQueryHandler<ListEmployeesQuery, IReadOnlyList<EmployeeDto>>
{
    public async Task<Result<IReadOnlyList<EmployeeDto>>> Handle(ListEmployeesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await store.ListEmployeesAsync(cancellationToken));
}

public sealed record EmployeeInput(
    Guid? BranchId, string IdentificationType, string IdentificationNumber, string FirstName, string LastName, string? Phone, string? Email);

public sealed record CreateEmployeeCommand(EmployeeInput Employee) : ICommand<EmployeeDto>;

public sealed record UpdateEmployeeCommand(Guid EmployeeId, EmployeeInput Employee) : ICommand<EmployeeDto>;

internal sealed class EmployeeInputValidator : AbstractValidator<EmployeeInput>
{
    public EmployeeInputValidator()
    {
        RuleFor(x => x.IdentificationType).NotEmpty().MaximumLength(10);
        RuleFor(x => x.IdentificationNumber).NotEmpty().MaximumLength(30).Matches("^[0-9A-Za-z-]+$");
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(200).When(x => !string.IsNullOrEmpty(x.Email));
    }
}

internal sealed class CreateEmployeeValidator : AbstractValidator<CreateEmployeeCommand>
{
    public CreateEmployeeValidator() => RuleFor(x => x.Employee).NotNull().SetValidator(new EmployeeInputValidator());
}

internal sealed class UpdateEmployeeValidator : AbstractValidator<UpdateEmployeeCommand>
{
    public UpdateEmployeeValidator() => RuleFor(x => x.Employee).NotNull().SetValidator(new EmployeeInputValidator());
}

internal sealed class EmployeeHandlers(
    Pos.Application.Abstractions.Installation.IInstallationContext installation, IIdentityStore store, IIdGenerator ids) :
    ICommandHandler<CreateEmployeeCommand, EmployeeDto>,
    ICommandHandler<UpdateEmployeeCommand, EmployeeDto>
{
    public Task<Result<EmployeeDto>> Handle(CreateEmployeeCommand request, CancellationToken cancellationToken)
    {
        var e = request.Employee;
        var employee = Employee.Create(ids.NewId(), installation.CompanyId!.Value, e.BranchId, e.IdentificationType, e.IdentificationNumber,
            e.FirstName, e.LastName, e.Phone, e.Email);
        store.Add(employee);
        return Task.FromResult(Result.Success(ToDto(employee)));
    }

    public async Task<Result<EmployeeDto>> Handle(UpdateEmployeeCommand request, CancellationToken cancellationToken)
    {
        var employee = await store.GetEmployeeAsync(request.EmployeeId, cancellationToken);
        if (employee is null)
        {
            return IdentityErrors.EmployeeNotFound;
        }

        var e = request.Employee;
        employee.Update(e.BranchId, e.FirstName, e.LastName, e.Phone, e.Email);
        return ToDto(employee);
    }

    private static EmployeeDto ToDto(Employee e) =>
        new(e.Id, e.BranchId, e.IdentificationType, e.IdentificationNumber, e.FirstName, e.LastName, e.Phone, e.Email, e.Status.ToString());
}
