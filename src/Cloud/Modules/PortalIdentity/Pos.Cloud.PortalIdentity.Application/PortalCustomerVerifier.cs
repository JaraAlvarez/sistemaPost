using System.Net;
using Pos.Application.Abstractions.Auditing;
using Pos.Cloud.Abstractions;
using Pos.Cloud.PortalIdentity.Domain;

namespace Pos.Cloud.PortalIdentity.Application;

/// <summary>
/// Cuenta del propietario = cuenta del portal (Fase 13-B, D13B-09). El instalador premium envía el correo y la contraseña del portal del
/// dueño; aquí se validan con las MISMAS reglas que el ingreso al portal: contraseña en tiempo constante (también si el correo no existe),
/// intentos fallidos que bloquean, el código TOTP si el usuario lo tiene activo (o si el portal lo exige) y nada de contraseñas temporales.
/// Solo se acepta un usuario CLIENTE atado a la cuenta dueña de la empresa de la clave. Todo queda en la auditoría.
/// </summary>
public sealed class PortalCustomerVerifier(PortalAuthServices auth, IAuditWriter audit) : IPortalCustomerVerifier
{
    public async Task<PortalCustomerVerification> VerifyAsync(
        string email, string password, string? totpCode, Guid accountId, string organizationLabel, IPAddress? ipAddress, CancellationToken cancellationToken)
    {
        var now = auth.Clock.UtcNow;
        var origin = $"desde el instalador ({ipAddress?.ToString() ?? "IP desconocida"})";
        var normalized = PortalUser.NormalizeEmail(email);
        var user = PortalUser.IsValidEmail(normalized) ? await auth.Store.FindUserByEmailAsync(normalized, cancellationToken) : null;
        var passwordOk = await auth.VerifyPasswordAsync(user, password, cancellationToken);

        if (user is null || user.Kind != PortalUserKind.Human)
        {
            await audit.WriteAsync(
                new AuditEntry("portal", "PORTAL_OWNER_LINK_FAILED", nameof(PortalUser), null, null,
                    $"Cuenta del propietario para {organizationLabel}: el correo {Shown(normalized)} no es un usuario del portal ({origin}).",
                    Severity: AuditSeverity.Warning),
                cancellationToken);
            return new(PortalCustomerCheck.InvalidCredentials);
        }

        if (user.IsLockedAt(now))
        {
            await auth.AuditAsync(user, "PORTAL_OWNER_LINK_FAILED", $"{user.Email} intentó vincular su cuenta mientras está bloqueado ({origin}).",
                AuditSeverity.Warning, cancellationToken);
            return new(PortalCustomerCheck.Locked);
        }

        if (!passwordOk || !user.CanSignIn(now))
        {
            var error = await auth.FailAsync(user, "PORTAL_OWNER_LINK_FAILED",
                $"Cuenta del propietario para {organizationLabel}: contraseña incorrecta o usuario deshabilitado ({user.Email}, {origin}).", cancellationToken);
            return new(error.Code == PortalIdentityErrors.UserLocked.Code ? PortalCustomerCheck.Locked : PortalCustomerCheck.InvalidCredentials);
        }

        if (user.Role != PortalRole.Customer || user.ResellerAccountId != accountId)
        {
            // La contraseña es correcta: no cuenta como intento fallido, pero queda registrado.
            await auth.AuditAsync(user, "PORTAL_OWNER_LINK_REJECTED",
                $"{user.Email} no es un usuario Cliente de la cuenta dueña de {organizationLabel} ({origin}).", AuditSeverity.Warning, cancellationToken);
            return new(PortalCustomerCheck.NotOwner);
        }

        if (user.MustChangePassword)
        {
            await auth.AuditAsync(user, "PORTAL_OWNER_LINK_REJECTED",
                $"{user.Email} todavía tiene la contraseña temporal; debe cambiarla en el portal antes de instalar ({origin}).", AuditSeverity.Info, cancellationToken);
            return new(PortalCustomerCheck.PasswordChangeRequired);
        }

        if (user.TotpEnabled || auth.Options.RequireTotp)
        {
            var secret = user.TotpSecretProtected is { } stored && user.TotpEnabled ? auth.Protector.Unprotect(stored) : null;
            if (secret is null || string.IsNullOrWhiteSpace(totpCode))
            {
                return new(PortalCustomerCheck.TotpRequired);
            }

            var step = Totp.Verify(secret, totpCode, now, user.TotpLastStep);
            if (step is null)
            {
                var error = await auth.FailAsync(user, "PORTAL_TOTP_FAILED", $"Código de verificación incorrecto al vincular la cuenta ({user.Email}, {origin}).", cancellationToken);
                return new(error.Code == PortalIdentityErrors.UserLocked.Code ? PortalCustomerCheck.Locked : PortalCustomerCheck.TotpRequired);
            }

            user.UseTotpStep(step.Value);
        }

        user.Unlock();
        await auth.AuditAsync(user, "PORTAL_OWNER_LINK_ISSUED",
            $"{user.Email} validó su cuenta para ser el Propietario del POS de {organizationLabel} ({origin}).", AuditSeverity.Warning, cancellationToken);
        return new(PortalCustomerCheck.Valid, user.Email, user.DisplayName);
    }

    public async Task<PortalCustomerVerification> VerifySignedInAsync(
        Guid userId, Guid accountId, string organizationLabel, IPAddress? ipAddress, CancellationToken cancellationToken)
    {
        var now = auth.Clock.UtcNow;
        var origin = $"en el navegador, para el instalador ({ipAddress?.ToString() ?? "IP desconocida"})";
        var user = await auth.Store.GetUserAsync(userId, cancellationToken);
        if (user is null || user.Kind != PortalUserKind.Human || !user.CanSignIn(now))
        {
            return new(PortalCustomerCheck.InvalidCredentials);
        }

        if (user.Role != PortalRole.Customer || user.ResellerAccountId != accountId)
        {
            await auth.AuditAsync(user, "PORTAL_OWNER_LINK_REJECTED",
                $"{user.Email} no es un usuario Cliente de la cuenta dueña de {organizationLabel} ({origin}).", AuditSeverity.Warning, cancellationToken);
            return new(PortalCustomerCheck.NotOwner);
        }

        return new(PortalCustomerCheck.Valid, user.Email, user.DisplayName);
    }

    private static string Shown(string email) => email.Length == 0 ? "(sin correo)" : email.Length > 120 ? email[..120] : email;
}
