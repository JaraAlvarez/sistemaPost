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
        var error = IdentityRules.CheckPin(pin, length);
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

/// <summary>RN-SEC-05: nadie concede permisos que no tiene; RN-SEC-04: siempre queda un Propietario/Administrador.</summary>
public sealed class PrivilegeGuard(ICurrentUser current, IPermissionEvaluator permissions, IIdentityStore store)
{
    public static readonly string[] AdministratorRoles = [SystemRoles.Owner, SystemRoles.Administrator];

    /// <summary>¿El usuario actual tiene todos esos permisos en el alcance indicado?</summary>
    public async Task<Result> CanGrantAsync(IEnumerable<string> permissionCodes, Guid? branchId, CancellationToken cancellationToken)
    {
        if (current.UserId is not { } actor)
        {
            return Result.Success();
        }

        var own = await permissions.GetEffectiveAsync(actor, branchId, null, cancellationToken);
        return permissionCodes.All(own.Contains) ? Result.Success() : IdentityErrors.PrivilegeEscalation;
    }

    public Result NotSelf(Guid userId) => current.UserId == userId ? IdentityErrors.SelfModification : Result.Success();

    /// <summary>¿Quedaría algún administrador activo si <paramref name="user"/> deja de serlo?</summary>
    public async Task<Result> KeepsAnAdministratorAsync(User user, CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(user, cancellationToken))
        {
            return Result.Success();
        }

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
