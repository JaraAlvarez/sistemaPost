using System.Net;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

/// <summary>
/// Clave escrita en el instalador premium (Fase 13-B): misma búsqueda que la activación (prefijo visible + comparación en tiempo constante
/// con TODAS las candidatas; una clave inexistente y una revocada reciben la misma respuesta) y el mismo límite por licencia.
/// </summary>
/// <summary>Empresa de la cuenta del usuario del portal para el primer ingreso sin clave: la que tenga licencia vigente o, si no, la primera.</summary>
internal static class AccountStores
{
    public static async Task<(Organization? Organization, License? License)> ChooseAsync(
        ILicensingStore store, Guid? accountId, DateTimeOffset now, IIdGenerator ids, CancellationToken cancellationToken)
    {
        if (accountId is not { } id)
        {
            return (null, null);
        }

        Organization? organization = null;
        foreach (var candidate in (await store.GetOrganizationsOfAccountAsync(id, cancellationToken)).Where(o => o.Status == RecordStatus.Active))
        {
            organization ??= candidate;
            if (await store.GetActiveLicenseAsync(candidate.Id, cancellationToken) is not { } active)
            {
                continue;
            }

            var subscription = await store.GetSubscriptionAsync(active.SubscriptionId, cancellationToken);
            subscription?.Refresh(new ChangeContext(SystemActor.Id, now, ids));
            if (subscription is { AllowsActivation: true })
            {
                return (candidate, active);
            }
        }

        return (organization, null);
    }
}

internal static class InstallerKeys
{
    public static async Task<Result<(License License, Subscription Subscription, Organization Organization)>> ResolveAsync(
        ILicensingStore store, ILicenseThrottle throttle, IIdGenerator ids, IClock clock, string? licenseKey, CancellationToken cancellationToken)
    {
        if (!LicenseKey.TryNormalize(licenseKey, out var key))
        {
            return LicenseApiErrors.KeyFormatInvalid;
        }

        License? license = null;
        foreach (var candidate in await store.FindLicensesByPrefixAsync(LicenseKey.VisiblePrefix(key), cancellationToken))
        {
            if (candidate.Matches(key))
            {
                license ??= candidate;
            }
        }

        if (license is not { IsActive: true })
        {
            return LicenseApiErrors.KeyInvalid;
        }

        if (!throttle.TryAcquire(license.Id))
        {
            return LicenseApiErrors.TooManyRequests;
        }

        var subscription = (await store.GetSubscriptionAsync(license.SubscriptionId, cancellationToken))!;
        var organization = (await store.GetOrganizationAsync(license.OrganizationId, cancellationToken))!;
        subscription.Refresh(new ChangeContext(SystemActor.Id, clock.UtcNow, ids));

        // Solo una suscripción al día o en prueba: con otra no se puede instalar (la activación también la rechazaría).
        if (subscription.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Trial) || organization.Status != RecordStatus.Active)
        {
            return LicenseApiErrors.SubscriptionInactive;
        }

        return (license, subscription, organization);
    }
}

// ─────────────────────────────── Vista previa de la clave ───────────────────────────────

/// <summary>
/// "Activa tu licencia" del instalador premium: con la clave, la nube devuelve la razón social, el NIT, la edición y el vencimiento para
/// que el cliente no los vuelva a escribir. Nada secreto: ni ids, ni la clave, ni contactos. Anónima por diseño (la autentica la clave),
/// con límite por IP (host) y por licencia. Es un comando porque actualiza el estado de la suscripción si ya venció.
/// </summary>
public sealed record PreviewLicenseCommand(LicensePreviewRequest Request) : ICommand<LicensePreviewResponse>;

internal sealed class PreviewLicenseHandler(ILicensingStore store, ILicenseThrottle throttle, IIdGenerator ids, IClock clock)
    : ICommandHandler<PreviewLicenseCommand, LicensePreviewResponse>
{
    public async Task<Result<LicensePreviewResponse>> Handle(PreviewLicenseCommand command, CancellationToken cancellationToken)
    {
        var resolved = await InstallerKeys.ResolveAsync(store, throttle, ids, clock, command.Request?.LicenseKey, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var (_, subscription, organization) = resolved.Value;
        return new LicensePreviewResponse(
            organization.LegalName, organization.NitNumber.ToString(), subscription.Edition.ToCode(), subscription.Status.ToCode(), subscription.ValidUntil, organization.City,
            organization.BusinessProfile);
    }
}

// ─────────────────────────────── Cuenta del propietario ───────────────────────────────

/// <summary>
/// Cuenta del propietario = cuenta del portal (D13B-09). El instalador envía la clave y el correo y la contraseña del portal del dueño;
/// si es un usuario CLIENTE de la cuenta dueña de la empresa, la nube devuelve un enlace FIRMADO de vida corta (sin la contraseña) con el
/// que el POS crea al Propietario con ese correo. Los intentos fallidos cuentan para el bloqueo del usuario del portal (por eso el
/// resultado es un <see cref="Outcome{T}"/>: la transacción se confirma aunque se rechace).
/// </summary>
public sealed record CreateOwnerLinkCommand(OwnerLinkRequest Request, IPAddress? IpAddress) : ICommand<Outcome<OwnerLinkResponse>>;

internal sealed class CreateOwnerLinkHandler(
    ILicensingStore store,
    ILicenseThrottle throttle,
    ILicenseTokenSigner signer,
    IPortalCustomerVerifier customers,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CreateOwnerLinkCommand, Outcome<OwnerLinkResponse>>
{
    public async Task<Result<Outcome<OwnerLinkResponse>>> Handle(CreateOwnerLinkCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!signer.IsAvailable)
        {
            return Outcome.Fail<OwnerLinkResponse>(LicenseApiErrors.SigningUnavailable);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
        {
            return Outcome.Fail<OwnerLinkResponse>(LicenseApiErrors.OwnerCredentialsInvalid);
        }

        if (string.IsNullOrWhiteSpace(request.LicenseKey))
        {
            return await SinClaveAsync(command, cancellationToken);
        }

        var resolved = await InstallerKeys.ResolveAsync(store, throttle, ids, clock, request.LicenseKey, cancellationToken);
        if (resolved.IsFailure)
        {
            return Outcome.Fail<OwnerLinkResponse>(resolved.Error);
        }

        var (license, _, organization) = resolved.Value;
        var check = await customers.VerifyAsync(
            request.Email, request.Password, request.TotpCode, organization.AccountId, organization.AuditLabel, command.IpAddress, cancellationToken);
        var error = check.Result switch
        {
            PortalCustomerCheck.Valid => null,
            PortalCustomerCheck.Locked => LicenseApiErrors.OwnerLocked,
            PortalCustomerCheck.NotOwner => LicenseApiErrors.OwnerNotLinked,
            PortalCustomerCheck.TotpRequired => LicenseApiErrors.OwnerTotpRequired,
            PortalCustomerCheck.PasswordChangeRequired => LicenseApiErrors.OwnerPasswordChangeRequired,
            _ => LicenseApiErrors.OwnerCredentialsInvalid,
        };
        if (error is not null)
        {
            return Outcome.Fail<OwnerLinkResponse>(error);
        }

        var (link, _) = await OwnerLinkIssuer.IssueAsync(
            store, signer, audit, ids, clock.UtcNow, license, organization, organization.AccountId, check.Email!, check.DisplayName!,
            $"desde {command.IpAddress?.ToString() ?? "IP desconocida"}", cancellationToken);
        return Outcome.Ok(link);
    }

    /// <summary>
    /// Primer ingreso a una tienda recién instalada (instalador 1.2.1), sin clave: un usuario Cliente del portal con su correo y
    /// contraseña (mismas reglas y bloqueo). La empresa es la de su cuenta: la que tenga licencia vigente (el POS se activa con el enlace)
    /// o, si no, la primera; sin empresa, el enlace no lleva NIT y la tienda queda en la prueba.
    /// </summary>
    private async Task<Result<Outcome<OwnerLinkResponse>>> SinClaveAsync(CreateOwnerLinkCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var check = await customers.VerifyAsync(
            request.Email, request.Password, request.TotpCode, null, "una tienda nueva", command.IpAddress, cancellationToken);
        var error = check.Result switch
        {
            PortalCustomerCheck.Valid => null,
            PortalCustomerCheck.Locked => LicenseApiErrors.OwnerLocked,
            PortalCustomerCheck.NotOwner => LicenseApiErrors.OwnerNotLinked,
            PortalCustomerCheck.TotpRequired => LicenseApiErrors.OwnerTotpRequired,
            PortalCustomerCheck.PasswordChangeRequired => LicenseApiErrors.OwnerPasswordChangeRequired,
            _ => LicenseApiErrors.OwnerCredentialsInvalid,
        };
        if (error is not null)
        {
            return Outcome.Fail<OwnerLinkResponse>(error);
        }

        // Sin cuenta de cliente (p. ej. un superadministrador): queda como Propietario y la tienda sigue en la prueba.
        var accountId = check.AccountId;

        var now = clock.UtcNow;
        var (organization, license) = await AccountStores.ChooseAsync(store, accountId, now, ids, cancellationToken);
        var (link, _) = await OwnerLinkIssuer.IssueAsync(
            store, signer, audit, ids, now, license, organization, accountId, check.Email!, check.DisplayName!,
            $"en el primer ingreso a la tienda, sin clave (desde {command.IpAddress?.ToString() ?? "IP desconocida"})", cancellationToken);
        return Outcome.Ok(link);
    }
}
