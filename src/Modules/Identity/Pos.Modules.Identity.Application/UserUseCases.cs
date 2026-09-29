using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Identity.Application;

public sealed record RoleAssignmentInput(Guid RoleId, Guid? BranchId);

public sealed record OverrideInput(string PermissionCode, OverrideEffect Effect, Guid? BranchId, string Reason);

// ───────────────────────────── Consultas ─────────────────────────────

public sealed record ListUsersQuery : IQuery<IReadOnlyList<UserDto>>;

internal sealed class ListUsersHandler(IIdentityStore store) : IQueryHandler<ListUsersQuery, IReadOnlyList<UserDto>>
{
    public async Task<Result<IReadOnlyList<UserDto>>> Handle(ListUsersQuery request, CancellationToken cancellationToken) =>
        Result.Success(await store.ListUsersAsync(cancellationToken));
}

public sealed record GetUserQuery(Guid UserId) : IQuery<UserDto>;

internal sealed class GetUserHandler(IIdentityStore store) : IQueryHandler<GetUserQuery, UserDto>
{
    public async Task<Result<UserDto>> Handle(GetUserQuery request, CancellationToken cancellationToken) =>
        await store.GetUserDtoAsync(request.UserId, cancellationToken) is { } user ? user : IdentityErrors.UserNotFound;
}

// ───────────────────────────── Alta y edición ─────────────────────────────

/// <summary>Crea un usuario humano. Debe cambiar la contraseña en su primer ingreso.</summary>
public sealed record CreateUserCommand(
    string Username,
    string DisplayName,
    string? Email,
    string Password,
    string? PosCode,
    string? Pin,
    Guid? EmployeeId,
    IReadOnlyList<RoleAssignmentInput> Roles) : ICommand<UserDto>;

internal sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(60);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(200).When(x => !string.IsNullOrEmpty(x.Email));
        RuleFor(x => x.Password).NotEmpty();
        RuleFor(x => x.Pin).NotEmpty().When(x => x.PosCode is not null).WithMessage("Indique el PIN junto con el código de cajero.");
        RuleFor(x => x.Roles).NotNull();
    }
}

internal sealed class CreateUserHandler(
    IInstallationContext installation, IIdentityStore store, CredentialPolicy policy, UserMutations mutations,
    IIdGenerator ids, IClock clock) : ICommandHandler<CreateUserCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial.");
        }

        if (await store.UsernameExistsAsync(IdentityRules.NormalizeUsername(request.Username), cancellationToken))
        {
            return IdentityErrors.UsernameDuplicated;
        }

        var hash = await policy.HashNewPasswordAsync(companyId, request.Password, request.Username, cancellationToken);
        if (hash.IsFailure)
        {
            return hash.Error;
        }

        var created = User.CreateHuman(ids.NewId(), companyId, request.Username, request.DisplayName, request.Email, hash.Value, clock.UtcNow, mustChangePassword: true);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var user = created.Value;
        user.Update(request.DisplayName, request.Email, request.EmployeeId);
        store.AddPasswordHistory(ids.NewId(), user.Id, hash.Value, clock.UtcNow);

        if (request.PosCode is not null)
        {
            var pin = await policy.SetPinAsync(user, request.PosCode, request.Pin!, cancellationToken);
            if (pin.IsFailure)
            {
                return pin.Error;
            }
        }

        var roles = await mutations.ReplaceRolesAsync(user, request.Roles, checkSelf: false, cancellationToken);
        if (roles.IsFailure)
        {
            return roles.Error;
        }

        store.Add(user);
        return await mutations.ToDtoAsync(user, cancellationToken);
    }
}

public sealed record UpdateUserCommand(Guid UserId, string DisplayName, string? Email, Guid? EmployeeId) : ICommand<UserDto>;

internal sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(200).When(x => !string.IsNullOrEmpty(x.Email));
    }
}

internal sealed class UpdateUserHandler(IIdentityStore store, UserMutations mutations) : ICommandHandler<UpdateUserCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(UpdateUserCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is not { IsHuman: true })
        {
            return IdentityErrors.UserNotFound;
        }

        if (request.EmployeeId is { } employee && await store.GetEmployeeAsync(employee, cancellationToken) is null)
        {
            return IdentityErrors.EmployeeNotFound;
        }

        user.Update(request.DisplayName, request.Email, request.EmployeeId);
        return await mutations.ToDtoAsync(user, cancellationToken);
    }
}

// ───────────────────────────── Estado ─────────────────────────────

/// <summary>Activar o desactivar. Desactivar revoca sus sesiones (RN-SEC-07) y nunca deja la empresa sin administrador (RN-SEC-04).</summary>
public sealed record SetUserActiveCommand(Guid UserId, bool Active) : ICommand;

internal sealed class SetUserActiveHandler(IIdentityStore store, PrivilegeGuard guard, ISessionStore sessions, ICurrentUser current, IAuditWriter audit)
    : ICommandHandler<SetUserActiveCommand>
{
    public async Task<Result> Handle(SetUserActiveCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is not { IsHuman: true })
        {
            return IdentityErrors.UserNotFound;
        }

        var self = guard.NotSelf(user.Id);
        if (self.IsFailure)
        {
            return self;
        }

        if (request.Active)
        {
            user.Activate();
            return Result.Success();
        }

        var keeps = await guard.KeepsAnAdministratorAsync(user, cancellationToken);
        if (keeps.IsFailure)
        {
            return keeps;
        }

        user.Deactivate();
        var revoked = await sessions.RevokeAllForUserAsync(user.Id, current.UserId, "USER_DISABLED", null, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("identity", "USER_SESSIONS_REVOKED", nameof(User), user.Id, user.AuditLabel,
                $"Se cerraron {revoked} sesiones de {user.DisplayName} al desactivarlo.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }
}

public sealed record UnlockUserCommand(Guid UserId) : ICommand;

internal sealed class UnlockUserHandler(IIdentityStore store) : ICommandHandler<UnlockUserCommand>
{
    public async Task<Result> Handle(UnlockUserCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is not { IsHuman: true })
        {
            return IdentityErrors.UserNotFound;
        }

        user.Unlock();
        return Result.Success();
    }
}

/// <summary>Contraseña temporal fijada por un administrador: el usuario debe cambiarla al entrar; sus sesiones se cierran.</summary>
public sealed record ResetPasswordCommand(Guid UserId, string TemporaryPassword) : ICommand;

internal sealed class ResetPasswordHandler(IIdentityStore store, CredentialPolicy policy, PrivilegeGuard guard, ISessionStore sessions, ICurrentUser current)
    : ICommandHandler<ResetPasswordCommand>
{
    public async Task<Result> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is not { IsHuman: true })
        {
            return IdentityErrors.UserNotFound;
        }

        var self = guard.NotSelf(user.Id);
        if (self.IsFailure)
        {
            return Error.BusinessRule("IDENTITY.USE_CHANGE_PASSWORD", "Para su propia contraseña use 'cambiar contraseña'.");
        }

        var changed = await policy.ChangePasswordAsync(user, request.TemporaryPassword, mustChange: true, cancellationToken);
        if (changed.IsSuccess)
        {
            await sessions.RevokeAllForUserAsync(user.Id, current.UserId, "PASSWORD_RESET", null, cancellationToken);
        }

        return changed;
    }
}

public sealed record ResetPinCommand(Guid UserId, string PosCode, string Pin) : ICommand;

internal sealed class ResetPinHandler(IIdentityStore store, CredentialPolicy policy) : ICommandHandler<ResetPinCommand>
{
    public async Task<Result> Handle(ResetPinCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        return user is not { IsHuman: true } ? IdentityErrors.UserNotFound : await policy.SetPinAsync(user, request.PosCode, request.Pin, cancellationToken);
    }
}

// ───────────────────────────── Roles y excepciones del usuario ─────────────────────────────

public sealed record SetUserRolesCommand(Guid UserId, IReadOnlyList<RoleAssignmentInput> Roles) : ICommand<UserDto>;

internal sealed class SetUserRolesHandler(IIdentityStore store, UserMutations mutations) : ICommandHandler<SetUserRolesCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(SetUserRolesCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is not { IsHuman: true })
        {
            return IdentityErrors.UserNotFound;
        }

        var result = await mutations.ReplaceRolesAsync(user, request.Roles, checkSelf: true, cancellationToken);
        return result.IsSuccess ? await mutations.ToDtoAsync(user, cancellationToken) : result.Error;
    }
}

public sealed record SetUserOverridesCommand(Guid UserId, IReadOnlyList<OverrideInput> Overrides) : ICommand<UserDto>;

internal sealed class SetUserOverridesValidator : AbstractValidator<SetUserOverridesCommand>
{
    public SetUserOverridesValidator() =>
        RuleForEach(x => x.Overrides).ChildRules(o =>
        {
            o.RuleFor(x => x.PermissionCode).NotEmpty();
            o.RuleFor(x => x.Reason).NotEmpty().MaximumLength(250);
        });
}

internal sealed class SetUserOverridesHandler(IIdentityStore store, PrivilegeGuard guard, UserMutations mutations, IIdGenerator ids)
    : ICommandHandler<SetUserOverridesCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(SetUserOverridesCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is not { IsHuman: true })
        {
            return IdentityErrors.UserNotFound;
        }

        var self = guard.NotSelf(user.Id);
        if (self.IsFailure)
        {
            return self.Error;
        }

        var catalog = (await store.ListActivePermissionCodesAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (request.Overrides.FirstOrDefault(o => !catalog.Contains(o.PermissionCode)) is { } unknown)
        {
            return Error.Validation("IDENTITY.UNKNOWN_PERMISSION", $"El permiso '{unknown.PermissionCode}' no existe.");
        }

        foreach (var grant in request.Overrides.Where(o => o.Effect == OverrideEffect.Grant).GroupBy(o => o.BranchId))
        {
            var allowed = await guard.CanGrantAsync(grant.Select(g => g.PermissionCode), grant.Key, cancellationToken);
            if (allowed.IsFailure)
            {
                return allowed.Error;
            }
        }

        user.ReplaceOverrides(request.Overrides.Select(o => (o.PermissionCode, o.Effect, o.BranchId, o.Reason)), ids.NewId);
        return await mutations.ToDtoAsync(user, cancellationToken);
    }
}

/// <summary>Operaciones compartidas: asignar roles con las reglas RN-SEC-04/05 y armar el DTO desde la entidad.</summary>
public sealed class UserMutations(IIdentityStore store, PrivilegeGuard guard, ICurrentUser current, IIdGenerator ids, IClock clock)
{
    public async Task<Result> ReplaceRolesAsync(User user, IReadOnlyList<RoleAssignmentInput> assignments, bool checkSelf, CancellationToken cancellationToken)
    {
        if (checkSelf)
        {
            var self = guard.NotSelf(user.Id);
            if (self.IsFailure)
            {
                return self;
            }
        }

        var roles = await store.GetRolesAsync(assignments.Select(a => a.RoleId).Distinct(), cancellationToken);
        if (roles.Count != assignments.Select(a => a.RoleId).Distinct().Count())
        {
            return IdentityErrors.RoleNotFound;
        }

        foreach (var assignment in assignments)
        {
            var allowed = await guard.CanGrantAsync(roles.Single(r => r.Id == assignment.RoleId).PermissionCodes, assignment.BranchId, cancellationToken);
            if (allowed.IsFailure)
            {
                return allowed;
            }
        }

        var remainsAdmin = assignments.Any(a => a.BranchId is null && roles.Any(r => r.Id == a.RoleId && PrivilegeGuard.AdministratorRoles.Contains(r.Code)));
        if (!remainsAdmin)
        {
            var keeps = await guard.KeepsAnAdministratorAsync(user, cancellationToken);
            if (keeps.IsFailure)
            {
                return keeps;
            }
        }

        user.ReplaceRoles(assignments.Select(a => (a.RoleId, a.BranchId)), ids.NewId, current.UserId ?? user.Id, clock.UtcNow);
        return Result.Success();
    }

    public async Task<UserDto> ToDtoAsync(User user, CancellationToken cancellationToken)
    {
        var roles = await store.GetRolesAsync(user.Roles.Select(r => r.RoleId).Distinct(), cancellationToken);
        return new UserDto(
            user.Id, user.Username, user.DisplayName, user.Email, user.Status.ToString(), user.PosCode, user.PinHash is not null,
            user.MustChangePassword, user.EmployeeId, user.LastLoginAt,
            [.. user.Roles.Select(r => new UserRoleDto(r.RoleId, roles.FirstOrDefault(x => x.Id == r.RoleId)?.Code ?? "?", r.BranchId))],
            [.. user.Overrides.Select(o => new UserOverrideDto(o.PermissionCode, o.Effect.ToString(), o.BranchId, o.Reason))]);
    }
}

// ───────────────────────────── Propietario ─────────────────────────────

/// <summary>
/// Crea el Propietario en una instalación configurada sin él (Fase 2). Solo desde el propio servidor y solo mientras
/// no exista un Propietario activo.
/// </summary>
public sealed record CreateOwnerCommand(OwnerInput Owner) : ICommand<Guid>;

internal sealed class CreateOwnerHandler(
    IInstallationContext installation, IClientContext client, IIdentityProvisioning provisioning, IIdentityStore store)
    : ICommandHandler<CreateOwnerCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateOwnerCommand request, CancellationToken cancellationToken)
    {
        if (!client.IsLocal || client.DeviceId is not null)
        {
            return IdentityErrors.LocalOnly;
        }

        if (installation.CompanyId is not { } companyId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial.");
        }

        if (await store.HasActiveOwnerAsync(cancellationToken))
        {
            return IdentityErrors.OwnerAlreadyExists;
        }

        return await provisioning.CreateOwnerAsync(companyId, request.Owner, cancellationToken);
    }
}
