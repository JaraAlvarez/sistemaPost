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

/// <summary>Emite el enlace firmado del propietario (el mismo para el correo + contraseña y para "Continuar con Google").</summary>
internal static class OwnerLinkIssuer
{
    public static async Task<(OwnerLinkResponse Response, Guid TokenId)> IssueAsync(
        ILicenseTokenSigner signer, IAuditWriter audit, IIdGenerator ids, DateTimeOffset now, License license, Organization organization,
        string email, string displayName, string via, CancellationToken cancellationToken)
    {
        var claims = new OwnerLinkClaims
        {
            TokenId = ids.NewId(),
            Email = email,
            DisplayName = displayName,
            OrganizationNit = organization.NitNumber.ToString(),
            IssuedAt = now,
            ExpiresAt = now + OwnerLinkToken.Lifetime,
        };
        var token = signer.SignOwnerLink(claims);
        await audit.WriteAsync(
            new AuditEntry("licensing", "OWNER_LINK_ISSUED", nameof(Organization), organization.Id, organization.AuditLabel,
                $"{organization.LegalName}: enlace del propietario para {claims.Email} (licencia {license.KeyPrefix}…) {via}; vence {claims.ExpiresAt:yyyy-MM-dd HH:mm} UTC.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return (new OwnerLinkResponse(token, claims.Email, claims.DisplayName, claims.ExpiresAt), claims.TokenId);
    }
}

// ─────────────────────────────── 1. El instalador crea la solicitud ───────────────────────────────

/// <summary>
/// "Continuar con Google" del instalador premium (Fase 13-B, parte E). Google no permite su ingreso dentro de un WebView incrustado, así
/// que el instalador pide aquí una solicitud con la clave (misma búsqueda en tiempo constante y mismos límites por licencia que la vista
/// previa), abre <c>BrowserUrl</c> en el navegador del sistema y consulta con <see cref="PollOwnerLinkRequestCommand"/>. Anónima por
/// diseño: la autentica la clave; quien autoriza es el dueño con su sesión del portal.
/// </summary>
/// <param name="Request">Clave de licencia escrita en el instalador.</param>
/// <param name="IpAddress">IP del instalador (auditoría).</param>
/// <param name="PublicBaseUrl">Esquema, host y ruta base de la petición (la página del portal es del MISMO servidor).</param>
public sealed record StartOwnerLinkRequestCommand(OwnerLinkRequestStart Request, IPAddress? IpAddress, string PublicBaseUrl)
    : ICommand<OwnerLinkRequestStarted>;

internal sealed class StartOwnerLinkRequestHandler(
    ILicensingStore store,
    ILicenseThrottle throttle,
    ILicenseTokenSigner signer,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<StartOwnerLinkRequestCommand, OwnerLinkRequestStarted>
{
    /// <summary>Página del portal que autoriza la solicitud.</summary>
    public const string PortalPath = "/cuenta/vincular-instalador";

    public async Task<Result<OwnerLinkRequestStarted>> Handle(StartOwnerLinkRequestCommand command, CancellationToken cancellationToken)
    {
        if (!signer.IsAvailable)
        {
            return LicenseApiErrors.SigningUnavailable;
        }

        var resolved = await InstallerKeys.ResolveAsync(store, throttle, ids, clock, command.Request?.LicenseKey, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var (license, _, organization) = resolved.Value;
        var now = clock.UtcNow;
        await store.LockLicenseAsync(license.Id, cancellationToken);
        if (await store.CountOpenOwnerLinkRequestsAsync(license.Id, now, cancellationToken) >= InstallerSignInRules.MaxOpenPerLicense)
        {
            return LicenseApiErrors.TooManyRequests;
        }

        var pollToken = InstallerSignInRules.NewPollToken();
        var request = InstallerSignInRequest.Create(ids.NewId(), license.Id, organization.Id, pollToken, command.IpAddress, now);
        store.Add(request);
        await audit.WriteAsync(
            new AuditEntry("licensing", "OWNER_LINK_REQUEST_CREATED", nameof(Organization), organization.Id, organization.AuditLabel,
                $"{organization.LegalName}: el instalador pidió entrar con la cuenta del portal en el navegador (código {request.UserCode}, licencia {license.KeyPrefix}…) desde {command.IpAddress?.ToString() ?? "IP desconocida"}."),
            cancellationToken);

        var browserUrl = $"{command.PublicBaseUrl.TrimEnd('/')}{PortalPath}?r={request.Id:D}";
        return new OwnerLinkRequestStarted(
            request.Id, pollToken, browserUrl, request.UserCode, (int)InstallerSignInRules.Lifetime.TotalSeconds, request.PollIntervalSeconds);
    }
}

// ─────────────────────────────── 2. El instalador consulta ───────────────────────────────

/// <summary>
/// Consulta del instalador con su secreto. PENDING mientras el dueño no decide; APPROVED entrega el enlace firmado UNA sola vez (la
/// solicitud queda usada); consultar antes del intervalo responde <see cref="LicenseErrorCodes.OwnerLinkSlowDown"/> (429) y alarga el
/// intervalo. Es un <see cref="Outcome{T}"/>: la hora de la consulta y el intervalo se guardan aunque se rechace.
/// </summary>
public sealed record PollOwnerLinkRequestCommand(Guid RequestId, OwnerLinkPoll Request, IPAddress? IpAddress) : ICommand<Outcome<OwnerLinkPollResponse>>;

internal sealed class PollOwnerLinkRequestHandler(
    ILicensingStore store,
    ILicenseTokenSigner signer,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<PollOwnerLinkRequestCommand, Outcome<OwnerLinkPollResponse>>
{
    public async Task<Result<Outcome<OwnerLinkPollResponse>>> Handle(PollOwnerLinkRequestCommand command, CancellationToken cancellationToken)
    {
        if (!signer.IsAvailable)
        {
            return LicenseApiErrors.SigningUnavailable;
        }

        // Solicitud inexistente o secreto ajeno: la misma respuesta (no se confirma que el id exista).
        var request = InstallerSignInRules.IsPollToken(command.Request?.PollToken)
            ? await store.GetOwnerLinkRequestForUpdateAsync(command.RequestId, cancellationToken)
            : null;
        if (request is null || !request.MatchesPollToken(command.Request!.PollToken))
        {
            return LicenseApiErrors.OwnerLinkRequestNotFound;
        }

        if (request.Status == InstallerSignInStatus.Consumed)
        {
            return LicenseApiErrors.OwnerLinkRequestUsed;
        }

        var now = clock.UtcNow;
        if (request.IsExpiredAt(now))
        {
            return Outcome.Ok(new OwnerLinkPollResponse(OwnerLinkRequestStatuses.Expired, request.PollIntervalSeconds));
        }

        if (request.RegisterPoll(now) == InstallerSignInPollCheck.SlowDown)
        {
            return Outcome.Fail<OwnerLinkPollResponse>(LicenseApiErrors.OwnerLinkSlowDown);
        }

        switch (request.Status)
        {
            case InstallerSignInStatus.Pending:
                return Outcome.Ok(new OwnerLinkPollResponse(OwnerLinkRequestStatuses.Pending, request.PollIntervalSeconds));
            case InstallerSignInStatus.Denied:
                return Outcome.Ok(new OwnerLinkPollResponse(OwnerLinkRequestStatuses.Denied, request.PollIntervalSeconds));
        }

        // Autorizada: la licencia y la empresa deben seguir vigentes al entregar el enlace.
        var license = await store.GetLicenseAsync(request.LicenseId, cancellationToken);
        var organization = await store.GetOrganizationAsync(request.OrganizationId, cancellationToken);
        if (license is not { IsActive: true } || organization is not { Status: RecordStatus.Active })
        {
            return Outcome.Fail<OwnerLinkPollResponse>(LicenseApiErrors.KeyInvalid);
        }

        var link = await OwnerLinkIssuer.IssueAsync(
            signer, audit, ids, now, license, organization, request.ApprovedEmail!, request.ApprovedDisplayName!,
            $"con \"Continuar con Google\" del instalador (código {request.UserCode}, autorizado en el portal; recogido desde {command.IpAddress?.ToString() ?? "IP desconocida"})",
            cancellationToken);
        request.Consume(link.TokenId, now);
        return Outcome.Ok(new OwnerLinkPollResponse(OwnerLinkRequestStatuses.Approved, request.PollIntervalSeconds, link.Response));
    }
}

// ─────────────────────────────── 3. El dueño autoriza en el portal ───────────────────────────────

/// <summary>Lo que ve el usuario del portal en la página de autorización.</summary>
public enum OwnerLinkRequestPageState
{
    /// <summary>No existe (o el identificador está mal escrito).</summary>
    NotFound,

    /// <summary>El usuario no es CLIENTE de la cuenta dueña: no se muestran los datos de la empresa.</summary>
    NotOwner,

    Pending,
    Approved,
    Denied,

    /// <summary>El instalador ya recogió el enlace.</summary>
    Used,

    Expired,
}

/// <summary>Datos de la página (la empresa solo si el usuario es su dueño).</summary>
public sealed record OwnerLinkRequestView(
    OwnerLinkRequestPageState State, string? LegalName = null, string? Nit = null, string? UserCode = null, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Abre (<c>Approve = null</c>), autoriza (<c>true</c>) o cancela (<c>false</c>) la solicitud con la sesión del portal en curso. Solo un
/// usuario CLIENTE de la cuenta dueña de la empresa ve los datos y decide (las mismas reglas que <c>/v1/owner-links</c>); los demás ven
/// un aviso sin datos y el intento queda en la auditoría. Es un comando: el rechazo y la decisión se registran en la misma transacción.
/// </summary>
public sealed record DecideOwnerLinkRequestCommand(Guid RequestId, bool? Approve, IPAddress? IpAddress) : ICommand<Outcome<OwnerLinkRequestView>>;

internal sealed class DecideOwnerLinkRequestHandler(
    ILicensingStore store,
    IPortalUserContext user,
    IPortalCustomerVerifier customers,
    IAuditWriter audit,
    IClock clock) : ICommandHandler<DecideOwnerLinkRequestCommand, Outcome<OwnerLinkRequestView>>
{
    public async Task<Result<Outcome<OwnerLinkRequestView>>> Handle(DecideOwnerLinkRequestCommand command, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } userId)
        {
            return Error.Unauthorized("AUTH.REQUIRED", "Inicie sesión para continuar.");
        }

        var request = await store.GetOwnerLinkRequestForUpdateAsync(command.RequestId, cancellationToken);
        var organization = request is null ? null : await store.GetOrganizationAsync(request.OrganizationId, cancellationToken);
        if (request is null || organization is null)
        {
            return Outcome.Ok(new OwnerLinkRequestView(OwnerLinkRequestPageState.NotFound));
        }

        var check = await customers.VerifySignedInAsync(userId, organization.AccountId, organization.AuditLabel, command.IpAddress, cancellationToken);
        if (check.Result != PortalCustomerCheck.Valid)
        {
            // NotOwner ya lo registra el verificador (PORTAL_OWNER_LINK_REJECTED); aquí, el usuario deshabilitado o bloqueado.
            if (check.Result != PortalCustomerCheck.NotOwner)
            {
                await audit.WriteAsync(
                new AuditEntry("licensing", "OWNER_LINK_REQUEST_REJECTED", nameof(Organization), organization.Id, organization.AuditLabel,
                    $"{user.DisplayName ?? "Un usuario"} abrió la autorización del instalador (código {request.UserCode}) con un usuario deshabilitado o bloqueado ({organization.LegalName}).",
                    Severity: AuditSeverity.Warning),
                cancellationToken);
            }

            return Outcome.Ok(new OwnerLinkRequestView(OwnerLinkRequestPageState.NotOwner));
        }

        var now = clock.UtcNow;
        if (command.Approve is { } approve && request.IsPendingAt(now))
        {
            var decided = approve
                ? request.Approve(userId, check.Email!, check.DisplayName!, command.IpAddress, now)
                : request.Deny(userId, command.IpAddress, now);
            if (decided.IsFailure)
            {
                return Outcome.Fail<OwnerLinkRequestView>(decided.Error);
            }

            await audit.WriteAsync(
                new AuditEntry("licensing", approve ? "OWNER_LINK_REQUEST_APPROVED" : "OWNER_LINK_REQUEST_DENIED", nameof(Organization), organization.Id,
                    organization.AuditLabel,
                    approve
                        ? $"{check.Email} autorizó en el portal la instalación de BusinessPost para {organization.LegalName} (código {request.UserCode}) desde {command.IpAddress?.ToString() ?? "IP desconocida"}."
                        : $"{check.Email} canceló en el portal la solicitud del instalador para {organization.LegalName} (código {request.UserCode}) desde {command.IpAddress?.ToString() ?? "IP desconocida"}.",
                    Severity: approve ? AuditSeverity.Warning : AuditSeverity.Info),
                cancellationToken);
        }

        var state = request.Status switch
        {
            InstallerSignInStatus.Consumed => OwnerLinkRequestPageState.Used,
            _ when request.IsExpiredAt(now) => OwnerLinkRequestPageState.Expired,
            InstallerSignInStatus.Approved => OwnerLinkRequestPageState.Approved,
            InstallerSignInStatus.Denied => OwnerLinkRequestPageState.Denied,
            _ => OwnerLinkRequestPageState.Pending,
        };
        return Outcome.Ok(new OwnerLinkRequestView(state, organization.LegalName, organization.NitNumber.ToString(), request.UserCode, request.ExpiresAt));
    }
}
