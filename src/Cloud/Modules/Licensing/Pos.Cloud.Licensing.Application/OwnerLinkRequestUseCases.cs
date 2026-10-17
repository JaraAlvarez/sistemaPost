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

        // Autorizada: la licencia, la empresa y la suscripción deben seguir vigentes al entregar el enlace (la suscripción pudo
        // vencer o suspenderse entre la solicitud y la autorización).
        var license = await store.GetLicenseAsync(request.LicenseId, cancellationToken);
        var organization = await store.GetOrganizationAsync(request.OrganizationId, cancellationToken);
        if (license is not { IsActive: true } || organization is not { Status: RecordStatus.Active })
        {
            return Outcome.Fail<OwnerLinkPollResponse>(LicenseApiErrors.KeyInvalid);
        }

        var subscription = await store.GetSubscriptionAsync(license.SubscriptionId, cancellationToken);
        subscription?.Refresh(new ChangeContext(SystemActor.Id, now, ids));
        if (subscription?.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Trial))
        {
            return Outcome.Fail<OwnerLinkPollResponse>(LicenseApiErrors.SubscriptionInactive);
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
    /// <summary>
    /// No existe, el identificador está mal escrito o el usuario no es CLIENTE de la cuenta dueña: la MISMA respuesta (a otro usuario
    /// no se le confirma que la solicitud existe ni de qué empresa es).
    /// </summary>
    Unavailable,

    /// <summary>Es el dueño, pero su ingreso no es reciente: debe volver a ingresar (contraseña o Google) para autorizar.</summary>
    ReauthenticationRequired,

    Pending,
    Approved,
    Denied,

    /// <summary>El instalador ya recogió el enlace.</summary>
    Used,

    Expired,
}

/// <summary>
/// Datos de la página (la empresa solo si el usuario es su dueño). <c>CreatedAt</c>/<c>CreatedIp</c>: cuándo y desde dónde se creó la
/// solicitud ("creada hace X min desde …"). <c>TotpRequired</c>: pedir el código del autenticador para autorizar. <c>Message</c>:
/// aviso del último intento (código incorrecto, TOTP incorrecto…).
/// </summary>
public sealed record OwnerLinkRequestView(
    OwnerLinkRequestPageState State,
    string? LegalName = null,
    string? Nit = null,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? CreatedAt = null,
    string? CreatedIp = null,
    bool TotpRequired = false,
    string? Message = null);

/// <summary>
/// Abre (<c>Approve = null</c>), autoriza (<c>true</c>) o cancela (<c>false</c>) la solicitud con la sesión del portal en curso.
/// Mitigaciones del "phishing" del flujo tipo Device Authorization (alguien con la clave crea la solicitud y le envía el enlace al
/// dueño): el dueño debe ESCRIBIR el código que muestra SU instalador (5 intentos), su ingreso debe ser reciente, se pide el TOTP si
/// lo tiene activo o el portal lo exige (también tras Google), la página muestra cuándo y desde qué IP se creó la solicitud, vence a
/// los 5 minutos y las decisiones tienen límite por usuario. Abrir la página (GET) no escribe en la auditoría; a un usuario que no es
/// el dueño se le responde lo mismo que a una solicitud inexistente.
/// </summary>
public sealed record DecideOwnerLinkRequestCommand(Guid RequestId, bool? Approve, IPAddress? IpAddress, string? UserCode = null, string? TotpCode = null)
    : ICommand<Outcome<OwnerLinkRequestView>>;

/// <summary>Avisos de la página de autorización que también revisan las pruebas.</summary>
public static class OwnerLinkRequestMessages
{
    public const string CanceledByWrongCodes =
        "La solicitud se canceló por demasiados códigos incorrectos. Vuelva a pulsar \"Continuar con Google\" en el instalador y escriba el código nuevo que muestre.";
}

internal sealed class DecideOwnerLinkRequestHandler(
    ILicensingStore store,
    IPortalUserContext user,
    IPortalCustomerVerifier customers,
    ILicenseThrottle throttle,
    IAuditWriter audit,
    IClock clock) : ICommandHandler<DecideOwnerLinkRequestCommand, Outcome<OwnerLinkRequestView>>
{
    public async Task<Result<Outcome<OwnerLinkRequestView>>> Handle(DecideOwnerLinkRequestCommand command, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } userId)
        {
            return Error.Unauthorized("AUTH.REQUIRED", "Inicie sesión para continuar.");
        }

        var deciding = command.Approve is not null;

        // Límite de decisiones por usuario del portal (también acota lo que un usuario ajeno puede escribir en la auditoría).
        if (deciding && !throttle.TryAcquire(userId))
        {
            return Outcome.Fail<OwnerLinkRequestView>(LicenseApiErrors.TooManyRequests);
        }

        var request = await store.GetOwnerLinkRequestForUpdateAsync(command.RequestId, cancellationToken);
        var organization = request is null ? null : await store.GetOrganizationAsync(request.OrganizationId, cancellationToken);
        if (request is null || organization is null)
        {
            return Outcome.Ok(new OwnerLinkRequestView(OwnerLinkRequestPageState.Unavailable));
        }

        var check = await customers.VerifySignedInAsync(
            userId, user.SessionId, organization.AccountId, organization.AuditLabel, deciding, command.IpAddress, cancellationToken);
        if (check.Result == PortalCustomerCheck.ReauthenticationRequired)
        {
            return Outcome.Ok(new OwnerLinkRequestView(OwnerLinkRequestPageState.ReauthenticationRequired));
        }

        if (check.Result != PortalCustomerCheck.Valid)
        {
            // NotOwner lo audita el verificador SOLO en un POST; inexistente y ajena se ven igual.
            return Outcome.Ok(new OwnerLinkRequestView(OwnerLinkRequestPageState.Unavailable));
        }

        var now = clock.UtcNow;
        string? message = null;
        if (command.Approve is { } approve && request.IsPendingAt(now))
        {
            message = approve
                ? await TryApproveAsync(request, organization, userId, check, command, now, cancellationToken)
                : await DenyAsync(request, organization, userId, check.Email!, command.IpAddress, now, cancellationToken);
        }

        var state = request.Status switch
        {
            InstallerSignInStatus.Consumed => OwnerLinkRequestPageState.Used,
            _ when request.IsExpiredAt(now) => OwnerLinkRequestPageState.Expired,
            InstallerSignInStatus.Approved => OwnerLinkRequestPageState.Approved,
            InstallerSignInStatus.Denied => OwnerLinkRequestPageState.Denied,
            _ => OwnerLinkRequestPageState.Pending,
        };

        // PN3: el motivo de la cancelación por intentos se deduce de lo guardado (code_attempts): también al recargar la página.
        if (state == OwnerLinkRequestPageState.Denied && message is null && request.CodeAttempts >= InstallerSignInRules.MaxCodeAttempts)
        {
            message = OwnerLinkRequestMessages.CanceledByWrongCodes;
        }
        return Outcome.Ok(new OwnerLinkRequestView(
            state, organization.LegalName, organization.NitNumber.ToString(), request.ExpiresAt, request.CreatedAt, request.CreatedIp?.ToString(),
            check.TotpRequired, message));
    }

    /// <summary>Autoriza si el código escrito coincide y (si aplica) el TOTP es correcto. Devuelve el aviso para la página o <c>null</c>.</summary>
    private async Task<string?> TryApproveAsync(
        InstallerSignInRequest request, Organization organization, Guid userId, PortalCustomerVerification check, DecideOwnerLinkRequestCommand command,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ip = command.IpAddress?.ToString() ?? "IP desconocida";
        switch (request.CheckUserCode(command.UserCode, userId, command.IpAddress, now))
        {
            case InstallerSignInCodeCheck.Malformed:
                return "Escriba el código de 8 letras y números tal como aparece en la ventana del instalador (por ejemplo, K7QF-M2XP).";
            case InstallerSignInCodeCheck.Mismatch:
                return $"El código no coincide con el del instalador. Escríbalo tal como aparece en la ventana del instalador (le quedan {request.CodeAttemptsLeft} intentos).";
            case InstallerSignInCodeCheck.Exhausted:
                await audit.WriteAsync(
                    new AuditEntry("licensing", "OWNER_LINK_REQUEST_DENIED", nameof(Organization), organization.Id, organization.AuditLabel,
                        $"Solicitud del instalador para {organization.LegalName} (código {request.UserCode}) cancelada: {check.Email} escribió {InstallerSignInRules.MaxCodeAttempts} códigos incorrectos desde {ip}.",
                        Severity: AuditSeverity.Warning),
                    cancellationToken);
                // PN3: la página muestra "cancelada"; el aviso explica por qué y qué hacer.
                return OwnerLinkRequestMessages.CanceledByWrongCodes;
            case InstallerSignInCodeCheck.Closed:
                return null;
        }

        switch (await customers.VerifySignedInTotpAsync(userId, command.TotpCode, organization.AuditLabel, command.IpAddress, cancellationToken))
        {
            case PortalCustomerCheck.Valid:
                break;
            case PortalCustomerCheck.TotpNotEnrolled:
                return "En este portal el doble factor es obligatorio: actívelo en \"Mi cuenta\" y vuelva a abrir el enlace del instalador.";
            case PortalCustomerCheck.Locked:
                return "Su usuario quedó bloqueado temporalmente por intentos fallidos. Intente más tarde.";
            default:
                return string.IsNullOrWhiteSpace(command.TotpCode)
                    ? "Escriba el código de 6 dígitos de su aplicación autenticadora."
                    : "El código de verificación no es correcto.";
        }

        var approved = request.Approve(userId, check.Email!, check.DisplayName!, command.IpAddress, now);
        if (approved.IsFailure)
        {
            return approved.Error.Message;
        }

        await audit.WriteAsync(
            new AuditEntry("licensing", "OWNER_LINK_REQUEST_APPROVED", nameof(Organization), organization.Id, organization.AuditLabel,
                $"{check.Email} autorizó en el portal la instalación de BusinessPost para {organization.LegalName} (código {request.UserCode} escrito por el dueño) desde {ip}; la solicitud se creó desde {request.CreatedIp?.ToString() ?? "IP desconocida"}.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return null;
    }

    private async Task<string?> DenyAsync(
        InstallerSignInRequest request, Organization organization, Guid userId, string email, IPAddress? ipAddress, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var denied = request.Deny(userId, ipAddress, now);
        if (denied.IsFailure)
        {
            return denied.Error.Message;
        }

        await audit.WriteAsync(
            new AuditEntry("licensing", "OWNER_LINK_REQUEST_DENIED", nameof(Organization), organization.Id, organization.AuditLabel,
                $"{email} canceló en el portal la solicitud del instalador para {organization.LegalName} (código {request.UserCode}) desde {ipAddress?.ToString() ?? "IP desconocida"}.",
                Severity: AuditSeverity.Info),
            cancellationToken);
        return null;
    }
}
