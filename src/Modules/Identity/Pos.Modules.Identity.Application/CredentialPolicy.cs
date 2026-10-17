using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Identity.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Identity.Application;

/// <summary>Contraseñas (RN-SEC-01: longitud, frecuentes, historial) y PIN (longitud, triviales, código de cajero único).</summary>
public sealed class CredentialPolicy(IIdentityStore store, ISecretHasher hasher, ISettingsReader settings, IIdGenerator ids, IClock clock)
{
    public async Task<Result<string>> HashNewPasswordAsync(Guid companyId, string password, string username, CancellationToken cancellationToken)
    {
        var minLength = await settings.GetAsync(SecuritySettings.PasswordMinLength, new SettingContext(companyId), cancellationToken);
        var error = IdentityRules.CheckPassword(password, IdentityRules.NormalizeUsername(username), minLength);
        return error == Error.None ? await hasher.HashAsync(password, SecretKind.Password, cancellationToken) : error;
    }

    /// <summary>Cambia la contraseña validando la política y que no sea una de las últimas N.</summary>
    public async Task<Result> ChangePasswordAsync(User user, string newPassword, bool mustChange, CancellationToken cancellationToken)
    {
        var hash = await HashNewPasswordAsync(user.CompanyId, newPassword, user.Username, cancellationToken);
        if (hash.IsFailure)
        {
            return hash.Error;
        }

        var historySize = await settings.GetAsync(SecuritySettings.PasswordHistory, new SettingContext(user.CompanyId), cancellationToken);
        if (historySize > 0)
        {
            foreach (var previous in await store.RecentPasswordHashesAsync(user.Id, historySize, cancellationToken))
            {
                if (await hasher.VerifyAsync(newPassword, previous, SecretKind.Password, cancellationToken) != SecretVerification.Failed)
                {
                    return IdentityErrors.PasswordReused;
                }
            }
        }

        user.SetPassword(hash.Value, clock.UtcNow, mustChange);
        store.AddPasswordHistory(ids.NewId(), user.Id, hash.Value, clock.UtcNow);
        return Result.Success();
    }

    public async Task<Result> SetPinAsync(User user, string posCode, string pin, CancellationToken cancellationToken)
    {
        var length = await settings.GetAsync(SecuritySettings.PinLength, new SettingContext(user.CompanyId), cancellationToken);
        var error = IdentityRules.CheckPin(pin, length, posCode);
        if (error != Error.None)
        {
            return error;
        }

        if (!IdentityRules.IsValidPosCode(posCode))
        {
            return IdentityErrors.InvalidPosCode;
        }

        if (await store.PosCodeExistsAsync(posCode, user.Id, cancellationToken))
        {
            return IdentityErrors.PosCodeDuplicated;
        }

        return user.SetPin(posCode, await hasher.HashAsync(pin, SecretKind.Pin, cancellationToken), clock.UtcNow);
    }
}

/// <summary>RN-SEC-05: nadie concede permisos que no tiene; RN-SEC-04: siempre queda un Propietario/Administrador; jerarquía (DU-06).</summary>
public sealed class PrivilegeGuard(ICurrentUser current, IPermissionEvaluator permissions, IIdentityStore store)
{
    private const int MaxListedPermissions = 6;

    public static readonly string[] AdministratorRoles = [SystemRoles.Owner, SystemRoles.Administrator];

    /// <summary>¿El usuario actual tiene todos esos permisos en el alcance indicado?</summary>
    public async Task<Result> CanGrantAsync(IEnumerable<string> permissionCodes, Guid? branchId, CancellationToken cancellationToken)
    {
        if (current.UserId is not { } actor)
        {
            return Result.Success();
        }

        var own = await OwnAsync(actor, branchId, cancellationToken);
        return permissionCodes.All(own.Contains) ? Result.Success() : IdentityErrors.PrivilegeEscalation;
    }

    public Result NotSelf(Guid userId) => current.UserId == userId ? IdentityErrors.SelfModification : Result.Success();

    /// <summary>
    /// Jerarquía (DU-06, PU1): solo se gestiona a quien no tiene más poder que uno. El poder del usuario <paramref name="target"/> son los
    /// permisos de sus ROLES más sus excepciones GRANT, cada uno en su alcance (todas las sucursales o una), SIN restar sus excepciones DENY:
    /// un DENY es reversible por quien lo puso, así que no rebaja a nadie (si no, bastaría poner DENY al Propietario, restablecerle la
    /// contraseña y quitar el DENY). Se aplica a editar, roles, excepciones, activar/inactivar, desbloquear, restablecer contraseña o PIN y
    /// cerrar sus sesiones. <paramref name="actingUserId"/>: quien actúa si no es el de la sesión (el supervisor que autorizó, UR3).
    /// El error lista los permisos que faltan (sus descripciones del catálogo): quien gestiona usuarios ya ve los roles de cada uno.
    /// </summary>
    public async Task<Result> CanManageAsync(User target, CancellationToken cancellationToken, Guid? actingUserId = null)
    {
        if ((actingUserId ?? current.UserId) is not { } actor || actor == target.Id)
        {
            return Result.Success();
        }

        var catalog = (await store.ListActivePermissionCodesAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var roles = await store.GetRolesAsync(target.Roles.Select(r => r.RoleId).Distinct(), cancellationToken);
        var power = target.Roles
            .SelectMany(a => (roles.FirstOrDefault(r => r.Id == a.RoleId)?.PermissionCodes ?? new HashSet<string>()).Select(code => (a.BranchId, Code: code)))
            .Concat(target.Overrides.Where(o => o.Effect == OverrideEffect.Grant).Select(o => (o.BranchId, Code: o.PermissionCode)))
            .Where(p => catalog.Contains(p.Code))
            .GroupBy(p => p.BranchId);

        var missing = new List<string>();
        foreach (var scope in power)
        {
            var own = await OwnAsync(actor, scope.Key, cancellationToken);
            missing.AddRange(scope.Select(p => p.Code).Where(code => !own.Contains(code)));
        }

        return missing.Count == 0 ? Result.Success() : await OutranksAsync("Esa persona", missing, cancellationToken);
    }

    /// <summary>
    /// UR1: editar o borrar un rol personalizado exige tener TODOS sus permisos actuales (no solo los que se agregan) y poder gestionar a
    /// cada usuario que lo tiene: si no, se degradaría a un superior quitándole permisos a su rol o borrándolo.
    /// </summary>
    public async Task<Result> CanChangeRoleAsync(Role role, CancellationToken cancellationToken)
    {
        if (current.UserId is not { } actor)
        {
            return Result.Success();
        }

        var catalog = (await store.ListActivePermissionCodesAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var own = await OwnAsync(actor, null, cancellationToken);
        var missing = role.PermissionCodes.Where(catalog.Contains).Where(code => !own.Contains(code)).ToList();
        if (missing.Count > 0)
        {
            return await OutranksAsync("Ese rol", missing, cancellationToken);
        }

        foreach (var userId in await store.ListUserIdsWithRoleAsync(role.Id, cancellationToken))
        {
            if (await store.GetUserAsync(userId, cancellationToken) is { } holder)
            {
                var allowed = await CanManageAsync(holder, cancellationToken);
                if (allowed.IsFailure)
                {
                    return allowed;
                }
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Permisos del actor en un alcance. Para TODAS las sucursales (<paramref name="branchId"/> nulo) son solo los que tiene en todas: a los
    /// globales se les resta lo que niega un DENY en cualquier sucursal (UR2); si no, un DENY por sucursal se esquivaría con un objetivo global.
    /// </summary>
    private async Task<IReadOnlySet<string>> OwnAsync(Guid actor, Guid? branchId, CancellationToken cancellationToken)
    {
        var own = await permissions.GetEffectiveAsync(actor, branchId, null, cancellationToken);
        if (branchId is not null)
        {
            return own;
        }

        var actorUser = await store.GetUserAsync(actor, cancellationToken);
        var denyBranches = actorUser?.Overrides.Where(o => o.Effect == OverrideEffect.Deny && o.BranchId is not null).Select(o => o.BranchId).Distinct().ToList() ?? [];
        if (denyBranches.Count == 0)
        {
            return own;
        }

        var everywhere = own.ToHashSet(StringComparer.Ordinal);
        foreach (var branch in denyBranches)
        {
            everywhere.IntersectWith(await permissions.GetEffectiveAsync(actor, branch, null, cancellationToken));
        }

        return everywhere;
    }

    private async Task<Error> OutranksAsync(string subject, IEnumerable<string> missing, CancellationToken cancellationToken)
    {
        var catalog = (await store.ListPermissionsAsync(cancellationToken)).ToDictionary(p => p.Code, p => p.Description, StringComparer.Ordinal);
        var names = missing.Distinct(StringComparer.Ordinal).Select(code => catalog.GetValueOrDefault(code) ?? code).Order(StringComparer.CurrentCulture).ToList();
        var shown = string.Join("; ", names.Take(MaxListedPermissions)) + (names.Count > MaxListedPermissions ? $"; y {names.Count - MaxListedPermissions} más" : string.Empty);
        return Error.Forbidden(IdentityErrors.TargetOutranks.Code, $"{subject} tiene permisos que usted no tiene: {shown}.");
    }

    /// <summary>DU-07: ¿quedaría algún Propietario activo si <paramref name="user"/> deja de serlo (<paramref name="remainsOwner"/> = false)?</summary>
    public async Task<Result> KeepsAnOwnerAsync(User user, bool remainsOwner, CancellationToken cancellationToken)
    {
        if (remainsOwner || !await IsActiveOwnerAsync(user, cancellationToken))
        {
            return Result.Success();
        }

        // PU4: dos cambios simultáneos (cada uno quitando a "el otro") no deben dejar la empresa sin Propietario.
        await store.LockAdministratorsAsync(user.CompanyId, cancellationToken);
        var error = IdentityRules.CheckOwnerRemains(true, false, await store.CountActiveOwnersAsync(user.Id, cancellationToken));
        return error == Error.None ? Result.Success() : error;
    }

    private async Task<bool> IsActiveOwnerAsync(User user, CancellationToken cancellationToken)
    {
        if (user.Status != UserStatus.Active && user.Status != UserStatus.Locked)
        {
            return false;
        }

        var roles = await store.GetRolesAsync(user.Roles.Where(r => r.BranchId is null).Select(r => r.RoleId), cancellationToken);
        return roles.Any(r => r.Code == SystemRoles.Owner);
    }

    /// <summary>¿Quedaría algún administrador activo si <paramref name="user"/> deja de serlo?</summary>
    public async Task<Result> KeepsAnAdministratorAsync(User user, CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(user, cancellationToken))
        {
            return Result.Success();
        }

        await store.LockAdministratorsAsync(user.CompanyId, cancellationToken);
        return await store.CountActiveAdministratorsAsync(user.Id, cancellationToken) > 0 ? Result.Success() : IdentityErrors.LastAdministrator;
    }

    public async Task<bool> IsAdministratorAsync(User user, CancellationToken cancellationToken)
    {
        if (user.Status != UserStatus.Active && user.Status != UserStatus.Locked)
        {
            return false;
        }

        var roles = await store.GetRolesAsync(user.Roles.Where(r => r.BranchId is null).Select(r => r.RoleId), cancellationToken);
        return roles.Any(r => AdministratorRoles.Contains(r.Code));
    }
}
