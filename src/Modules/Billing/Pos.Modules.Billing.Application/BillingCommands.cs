using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Billing.Application;

internal static class BillingGuards
{
    public static readonly Error SetupRequired = Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");

    public static Error TooSoon(FiscalVerifyThrottle throttle) => Error.BusinessRule(
        "BILLING.VERIFY_TOO_SOON", $"Espere unos segundos: la conexión se prueba como máximo una vez cada {throttle.Interval.TotalSeconds:0} segundos.");

    public const string AutoDisabledNotice = "La facturación electrónica quedó apagada: conéctela en Facturación → Configuración.";
}

// ─────────────────────────────── Documentos ───────────────────────────────

public sealed record GetFiscalDocumentQuery(Guid DocumentId) : IQuery<FiscalDocumentDto>;

internal sealed class GetFiscalDocumentHandler(IFiscalDocumentStore store) : IQueryHandler<GetFiscalDocumentQuery, FiscalDocumentDto>
{
    public async Task<Result<FiscalDocumentDto>> Handle(GetFiscalDocumentQuery request, CancellationToken cancellationToken) =>
        await store.GetAsync(request.DocumentId, cancellationToken) is { } document ? document.ToDto() : BillingErrors.NotFound;
}

/// <summary>Documentos de la sucursal con filtros: fechas, estado, tipo, origen y texto (número de origen, número fiscal o identificación).</summary>
public sealed record ListFiscalDocumentsQuery(
    DateOnly? From, DateOnly? To, string? Status, int? Limit, string? Type = null, string? Source = null, string? Search = null) : IQuery<IReadOnlyList<FiscalDocumentDto>>;

internal sealed class ListFiscalDocumentsHandler(IFiscalDocumentStore store, IInstallationContext installation)
    : IQueryHandler<ListFiscalDocumentsQuery, IReadOnlyList<FiscalDocumentDto>>
{
    public async Task<Result<IReadOnlyList<FiscalDocumentDto>>> Handle(ListFiscalDocumentsQuery request, CancellationToken cancellationToken)
    {
        if (installation.BranchId is not { } branch)
        {
            return BillingGuards.SetupRequired;
        }

        var status = BillingMapping.Parse<FiscalStatus>(request.Status);
        var type = BillingMapping.Parse<FiscalDocumentType>(request.Type);
        var source = BillingMapping.Parse<FiscalSource>(request.Source);
        if ((request.Status is not null && status is null) || (request.Type is not null && type is null) || (request.Source is not null && source is null))
        {
            return Error.Validation("BILLING.INVALID_FILTER", "Estado, tipo u origen desconocido.");
        }

        var filter = new DocumentFilter(
            branch, request.From, request.To, status, Math.Clamp(request.Limit ?? 200, 1, 1000), type, source,
            string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim());
        return (await store.ListAsync(filter, cancellationToken)).Select(d => d.ToDto()).ToList();
    }
}

/// <summary>Reintento manual de un documento electrónico pendiente, con error, en contingencia o rechazado.</summary>
public sealed record RetryFiscalDocumentCommand(Guid DocumentId) : ICommand<FiscalDocumentDto>, IAllowedWhenRestricted;

internal sealed class RetryFiscalDocumentHandler(
    IFiscalDocumentStore store, IOutbox outbox, FiscalQueueSignal signal, IAuditWriter audit, IActorContext actor, IIdGenerator ids, IClock clock)
    : ICommandHandler<RetryFiscalDocumentCommand, FiscalDocumentDto>
{
    public async Task<Result<FiscalDocumentDto>> Handle(RetryFiscalDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(request.DocumentId, cancellationToken);
        if (document is null)
        {
            return BillingErrors.NotFound;
        }

        var previous = document.Status;
        var retried = document.Retry(clock.UtcNow, actor.ActorId!.Value, ids.NewId);
        if (retried.IsFailure)
        {
            return retried.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_DOCUMENT_RETRIED", nameof(FiscalDocument), document.Id, document.AuditLabel,
                $"Reintento manual de {document.DocumentType.Db()} {document.SourceNumber} (estaba {previous.Db()})."),
            cancellationToken);
        outbox.Enqueue(BillingService.PendingMessage, new { DocumentId = document.Id }, OutboxDestination.Local);
        signal.Notify();
        return document.ToDto();
    }
}

/// <summary>Corrige los datos del adquirente de un documento aún no aceptado y lo deja listo para reenviar (D11B-10).</summary>
public sealed record CorrectFiscalBuyerCommand(
    Guid DocumentId,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? Email,
    string PersonType,
    string TaxRegime,
    IReadOnlyList<string>? Responsibilities,
    string? Address,
    string? MunicipalityCode,
    string? Phone) : ICommand<FiscalDocumentDto>;

internal sealed class CorrectFiscalBuyerHandler(
    IFiscalDocumentStore store, IOutbox outbox, FiscalQueueSignal signal, IAuditWriter audit, IActorContext actor, IIdGenerator ids, IClock clock)
    : ICommandHandler<CorrectFiscalBuyerCommand, FiscalDocumentDto>
{
    public async Task<Result<FiscalDocumentDto>> Handle(CorrectFiscalBuyerCommand request, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(request.DocumentId, cancellationToken);
        if (document is null)
        {
            return BillingErrors.NotFound;
        }

        var before = $"{document.BuyerIdentificationType} {document.BuyerIdentification}";
        var corrected = document.CorrectBuyer(
            new FiscalBuyerCorrection(
                request.Name ?? string.Empty, request.IdentificationType ?? string.Empty, request.IdentificationNumber ?? string.Empty, request.Email,
                new FiscalBuyerData(
                    request.PersonType?.Trim().ToUpperInvariant() ?? string.Empty, Clean(request.CheckDigit), request.TaxRegime?.Trim() ?? string.Empty,
                    [.. (request.Responsibilities ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())], Clean(request.Address),
                    Clean(request.MunicipalityCode), Clean(request.Phone))),
            clock.UtcNow, actor.ActorId!.Value, ids.NewId);
        if (corrected.IsFailure)
        {
            return corrected.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_BUYER_CORRECTED", nameof(FiscalDocument), document.Id, document.AuditLabel,
                $"Datos del adquirente de {document.SourceNumber} corregidos para reenviar el documento.",
                OldValues: new Dictionary<string, object?> { ["Identificación"] = before },
                NewValues: new Dictionary<string, object?> { ["Identificación"] = $"{document.BuyerIdentificationType} {document.BuyerIdentification}" },
                Severity: AuditSeverity.Warning),
            cancellationToken);
        outbox.Enqueue(BillingService.PendingMessage, new { DocumentId = document.Id }, OutboxDestination.Local);
        signal.Notify();
        return document.ToDto();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// ─────────────────────────────── Configuración del proveedor ───────────────────────────────

public sealed record GetBillingSettingsQuery : IQuery<BillingSettingsDto>;

internal sealed class GetBillingSettingsHandler(
    IBillingSettingsStore store, IFiscalDocumentStore documents, IFiscalProvider provider, ISettingsReader settings, IInstallationContext installation)
    : IQueryHandler<GetBillingSettingsQuery, BillingSettingsDto>
{
    public async Task<Result<BillingSettingsDto>> Handle(GetBillingSettingsQuery request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        var configured = await store.GetAsync(companyId, cancellationToken);
        var mode = await BillingService.ModeAsync(store, settings, companyId, installation.BranchId, cancellationToken);
        var held = await HeldDocuments.CountAsync(documents, configured, cancellationToken);
        return new BillingSettingsDto(
            configured?.Provider ?? provider.Name, provider.Name, mode.Db(), (configured?.Environment ?? FiscalEnvironment.Sandbox).Db(), configured?.HasCredentials ?? false,
            configured?.CredentialsUpdatedAt, configured?.LastSyncAt, configured?.LastSyncError, configured?.CredentialHint, configured?.LastVerifiedAt,
            configured?.LastVerificationOk, configured?.LastVerificationMessage, held.Total, configured?.AutoDisabledAt, held.OtherEnvironment);
    }
}

/// <summary>
/// Modo de emisión (OFF, ON_REQUEST, EVERY_SALE) y ambiente (SANDBOX, PRODUCTION). Encender exige credenciales verificadas contra el
/// proveedor en ese ambiente (<see cref="VerifyBillingConnectionCommand"/>); OFF es "Desconectar" (las credenciales se conservan).
/// </summary>
public sealed record UpdateBillingSettingsCommand(string Mode, string Environment) : ICommand<BillingSettingsDto>;

internal sealed class UpdateBillingSettingsHandler(
    IBillingSettingsStore store, IFiscalDocumentStore documents, IFiscalProvider provider, ISettingsReader settings, IInstallationContext installation,
    IAuditWriter audit)
    : ICommandHandler<UpdateBillingSettingsCommand, BillingSettingsDto>
{
    public async Task<Result<BillingSettingsDto>> Handle(UpdateBillingSettingsCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        if (BillingMapping.Parse<BillingMode>(request.Mode) is not { } mode || BillingMapping.Parse<FiscalEnvironment>(request.Environment) is not { } environment)
        {
            return Error.Validation("BILLING.INVALID_SETTINGS", "Modo (OFF, ON_REQUEST, EVERY_SALE) o ambiente (SANDBOX, PRODUCTION) desconocido.");
        }

        var configured = await store.GetAsync(companyId, cancellationToken);
        if (configured is null)
        {
            configured = BillingProviderSettings.Create(companyId, provider.Name);
            store.Add(configured);
        }

        var before = (Mode: configured.Mode, Environment: configured.Environment);
        var changed = configured.Configure(mode, environment, provider.Name);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_SETTINGS_CHANGED", "BillingProviderSettings", null, $"Facturación electrónica ({provider.Name})",
                $"Facturación electrónica: modo {before.Mode.Db()} → {mode.Db()}, ambiente {before.Environment.Db()} → {environment.Db()}.",
                OldValues: new Dictionary<string, object?> { ["Modo"] = before.Mode.Db(), ["Ambiente"] = before.Environment.Db() },
                NewValues: new Dictionary<string, object?> { ["Modo"] = mode.Db(), ["Ambiente"] = environment.Db() },
                Severity: mode == before.Mode ? AuditSeverity.Info : AuditSeverity.Warning),
            cancellationToken);
        return await new GetBillingSettingsHandler(store, documents, provider, settings, installation).Handle(new GetBillingSettingsQuery(), cancellationToken);
    }
}

/// <summary>
/// Credenciales del proveedor (D11B-08): se cifran con DPAPI al recibirlas y nunca se devuelven ni se registran. Guardarlas APAGA la
/// facturación electrónica y borra la verificación ("guardar desactivado → verificar → activar").
/// </summary>
public sealed record SetProviderCredentialsCommand(string Username, string Password, string ClientId, string ClientSecret) : ICommand<BillingSettingsDto>
{
    public override string ToString() => "SetProviderCredentialsCommand { *** }";
}

internal sealed class SetProviderCredentialsHandler(
    IBillingSettingsStore store, IFiscalDocumentStore documents, IFiscalProvider provider, IFiscalCredentialProtector protector, ISettingsReader settings,
    IInstallationContext installation, IAuditWriter audit, IClock clock)
    : ICommandHandler<SetProviderCredentialsCommand, BillingSettingsDto>
{
    public async Task<Result<BillingSettingsDto>> Handle(SetProviderCredentialsCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.ClientSecret) || request.Username.Length > 200 || request.Password.Length > 200
            || request.ClientId.Length > 200 || request.ClientSecret.Length > 500)
        {
            return BillingErrors.InvalidCredentials;
        }

        var configured = await store.GetAsync(companyId, cancellationToken);
        if (configured is null)
        {
            configured = BillingProviderSettings.Create(companyId, provider.Name);
            store.Add(configured);
        }

        var modeBefore = configured.Mode;
        configured.SetCredentials(
            protector.Protect(new FiscalCredentials(request.Username.Trim(), request.Password, request.ClientId.Trim(), request.ClientSecret.Trim())), clock.UtcNow,
            BillingProviderSettings.MaskUsername(request.Username));
        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_CREDENTIALS_CHANGED", "BillingProviderSettings", null, $"Facturación electrónica ({provider.Name})",
                $"Credenciales del proveedor {provider.Name} actualizadas (cifradas; no se registran)."
                + (modeBefore == BillingMode.Off ? string.Empty : $" La facturación electrónica se apagó (modo {modeBefore.Db()} → OFF) hasta verificarlas."),
                Severity: AuditSeverity.Critical),
            cancellationToken);
        return await new GetBillingSettingsHandler(store, documents, provider, settings, installation).Handle(new GetBillingSettingsQuery(), cancellationToken);
    }

    /// <summary>Validación de las cuatro credenciales (también la usan "probar llaves candidatas" y "cambiar llaves").</summary>
    public static bool Valid(string? username, string? password, string? clientId, string? clientSecret) =>
        !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(clientId)
        && !string.IsNullOrWhiteSpace(clientSecret) && username.Length <= 200 && password.Length <= 200 && clientId.Length <= 200 && clientSecret.Length <= 500;
}

/// <summary>
/// "Verificar conexión" con el proveedor: valida DE VERDAD las credenciales guardadas en el ambiente vigente (Factus: token nuevo con usuario
/// y clave, en un espacio de token aparte del de la cola), guarda el resultado y lo audita. Un resultado negativo no es error HTTP. Llama al
/// proveedor FUERA de la transacción (<see cref="ISelfTransactedCommand"/>) y guarda en una transacción corta, solo si mientras tanto no
/// cambiaron las llaves ni el ambiente. Permitido con la licencia restringida (obligación ante la DIAN).
/// </summary>
public sealed record VerifyBillingConnectionCommand : ICommand<FiscalConnectionVerificationDto>, ISelfTransactedCommand, IAllowedWhenRestricted;

internal sealed class VerifyBillingConnectionHandler(
    IBillingSettingsStore store, IFiscalProvider provider, IFiscalCredentialProtector protector, IInstallationContext installation, IAuditWriter audit, IClock clock,
    FiscalVerifyThrottle throttle, IShortTransactions transactions)
    : ICommandHandler<VerifyBillingConnectionCommand, FiscalConnectionVerificationDto>
{
    public async Task<Result<FiscalConnectionVerificationDto>> Handle(VerifyBillingConnectionCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        if (await store.GetAsync(companyId, cancellationToken) is not { HasCredentials: true } settings)
        {
            return BillingErrors.CredentialsRequired;
        }

        if (!throttle.TryEnter(companyId, "stored"))
        {
            return BillingGuards.TooSoon(throttle);
        }

        var environment = settings.Environment;
        var keysChangedAt = settings.CredentialsUpdatedAt;
        var check = protector.Unprotect(settings.Credentials!) is { } credentials
            ? await provider.VerifyAsync(new FiscalConnection(environment, credentials), cancellationToken)
            : new FiscalConnectionCheck(false, "No se pudieron leer las credenciales guardadas (¿se restauró la base de datos en otro equipo?): escríbalas de nuevo.");
        var now = clock.UtcNow;

        // El resultado es de ESTAS llaves en ESTE ambiente: si mientras se verificaba alguien las cambió, no se marca nada.
        var current = false;
        var recorded = false;
        void Record()
        {
            current = settings.Environment == environment && settings.CredentialsUpdatedAt == keysChangedAt;
            recorded = current && settings.RecordVerification(now, check.Ok, check.Message, check.Transient);
        }

        Record();
        var saved = await transactions.SaveAsync(Record, CancellationToken.None);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_CONNECTION_VERIFIED", "BillingProviderSettings", null, $"Facturación electrónica ({provider.Name})",
                !current
                    ? $"Verificación de {provider.Name} en {environment.Db()} descartada: las llaves o el ambiente cambiaron mientras se verificaba."
                    : $"Verificación de {provider.Name} en {environment.Db()}: {(check.Ok ? "correcta" : "con problema")}. {check.Message}"
                      + (recorded ? string.Empty : " Falla transitoria: se conserva la verificación correcta anterior."),
                NewValues: new Dictionary<string, object?> { ["Ambiente"] = environment.Db(), ["Correcta"] = check.Ok, ["Transitoria"] = check.Transient },
                Severity: check.Ok && current ? AuditSeverity.Info : AuditSeverity.Warning),
            CancellationToken.None);
        await transactions.SaveAsync(cancellationToken: CancellationToken.None);
        return current
            ? new FiscalConnectionVerificationDto(check.Ok, check.Message, environment.Db(), now, check.Transient)
            : new FiscalConnectionVerificationDto(false, "Las llaves o el ambiente cambiaron mientras se verificaba: vuelva a verificar.", environment.Db(), now, true);
    }
}

/// <summary>
/// Probar llaves CANDIDATAS sin guardarlas (PA6): no se persisten ni se devuelven; el resultado queda en la auditoría sin las llaves. Sin
/// <c>Environment</c> se prueban en el ambiente vigente. Fuera de la transacción (la llamada al proveedor puede tardar).
/// </summary>
public sealed record VerifyCandidateCredentialsCommand(string Username, string Password, string ClientId, string ClientSecret, string? Environment = null)
    : ICommand<FiscalConnectionVerificationDto>, ISelfTransactedCommand
{
    public override string ToString() => $"VerifyCandidateCredentialsCommand {{ Environment = {Environment}, Llaves = *** }}";
}

internal sealed class VerifyCandidateCredentialsHandler(
    IBillingSettingsStore store, IFiscalProvider provider, IInstallationContext installation, IAuditWriter audit, IClock clock, FiscalVerifyThrottle throttle,
    IShortTransactions transactions)
    : ICommandHandler<VerifyCandidateCredentialsCommand, FiscalConnectionVerificationDto>
{
    public async Task<Result<FiscalConnectionVerificationDto>> Handle(VerifyCandidateCredentialsCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        if (!SetProviderCredentialsHandler.Valid(request.Username, request.Password, request.ClientId, request.ClientSecret))
        {
            return BillingErrors.InvalidCredentials;
        }

        var environment = request.Environment is null
            ? (await store.GetAsync(companyId, cancellationToken))?.Environment ?? FiscalEnvironment.Sandbox
            : BillingMapping.Parse<FiscalEnvironment>(request.Environment);
        if (environment is not { } env)
        {
            return Error.Validation("BILLING.INVALID_SETTINGS", "Ambiente (SANDBOX, PRODUCTION) desconocido.");
        }

        // El freno es por juego de llaves: probar OTRAS llaves candidatas no espera (PA6).
        if (!throttle.TryEnter(companyId, CandidateCheck.Fingerprint("candidate", env, request.Username, request.Password, request.ClientId, request.ClientSecret)))
        {
            return BillingGuards.TooSoon(throttle);
        }

        var check = await CandidateCheck.RunAsync(provider, env, request.Username, request.Password, request.ClientId, request.ClientSecret, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_CONNECTION_VERIFIED", "BillingProviderSettings", null, $"Facturación electrónica ({provider.Name})",
                $"Prueba de llaves candidatas (no se guardaron) de {provider.Name} en {env.Db()}: {(check.Ok ? "correcta" : "con problema")}. {check.Message}",
                NewValues: new Dictionary<string, object?> { ["Ambiente"] = env.Db(), ["Correcta"] = check.Ok, ["Candidatas"] = true },
                Severity: check.Ok ? AuditSeverity.Info : AuditSeverity.Warning),
            CancellationToken.None);
        var saved = await transactions.SaveAsync(cancellationToken: CancellationToken.None);
        return saved.IsFailure ? saved.Error : new FiscalConnectionVerificationDto(check.Ok, check.Message, env.Db(), clock.UtcNow, check.Transient);
    }
}

/// <summary>
/// "Cambiar llaves" (PA6): prueba las llaves candidatas en el ambiente vigente SIN guardarlas (fuera de la transacción); solo si el proveedor
/// las acepta se guardan (cifradas, en una transacción corta) y la facturación sigue en su modo. Si fallan no se toca nada (<c>Ok = false</c>).
/// Las llaves nuevas mueven el corte del atraso (los documentos de antes esperan "Enviar ahora").
/// </summary>
public sealed record RotateProviderCredentialsCommand(string Username, string Password, string ClientId, string ClientSecret)
    : ICommand<FiscalConnectionVerificationDto>, ISelfTransactedCommand
{
    public override string ToString() => "RotateProviderCredentialsCommand { *** }";
}

internal sealed class RotateProviderCredentialsHandler(
    IBillingSettingsStore store, IFiscalProvider provider, IFiscalCredentialProtector protector, IInstallationContext installation, IAuditWriter audit,
    IClock clock, FiscalVerifyThrottle throttle, IShortTransactions transactions)
    : ICommandHandler<RotateProviderCredentialsCommand, FiscalConnectionVerificationDto>
{
    public async Task<Result<FiscalConnectionVerificationDto>> Handle(RotateProviderCredentialsCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        if (!SetProviderCredentialsHandler.Valid(request.Username, request.Password, request.ClientId, request.ClientSecret))
        {
            return BillingErrors.InvalidCredentials;
        }

        if (await store.GetAsync(companyId, cancellationToken) is not { HasCredentials: true } settings)
        {
            return BillingErrors.CredentialsRequired;
        }

        var environment = settings.Environment;
        var keysChangedAt = settings.CredentialsUpdatedAt;
        if (!throttle.TryEnter(companyId, CandidateCheck.Fingerprint("rotate", environment, request.Username, request.Password, request.ClientId, request.ClientSecret)))
        {
            return BillingGuards.TooSoon(throttle);
        }

        var check = await CandidateCheck.RunAsync(provider, environment, request.Username, request.Password, request.ClientId, request.ClientSecret, cancellationToken);
        var now = clock.UtcNow;
        if (!check.Ok)
        {
            await audit.WriteAsync(
                new AuditEntry("billing", "FISCAL_CONNECTION_VERIFIED", "BillingProviderSettings", null, $"Facturación electrónica ({provider.Name})",
                    $"Cambio de llaves de {provider.Name} rechazado: las llaves nuevas no pasaron la prueba en {environment.Db()} y no se guardaron. {check.Message}",
                    NewValues: new Dictionary<string, object?> { ["Ambiente"] = environment.Db(), ["Correcta"] = false, ["Candidatas"] = true },
                    Severity: AuditSeverity.Warning),
                CancellationToken.None);
            var savedAudit = await transactions.SaveAsync(cancellationToken: CancellationToken.None);
            return savedAudit.IsFailure ? savedAudit.Error : new FiscalConnectionVerificationDto(false, check.Message, environment.Db(), now, check.Transient);
        }

        var encrypted = protector.Protect(new FiscalCredentials(request.Username.Trim(), request.Password, request.ClientId.Trim(), request.ClientSecret.Trim()));
        var applied = false;
        void Apply()
        {
            // Solo si mientras se probaba nadie cambió las llaves ni el ambiente.
            applied = settings.Environment == environment && settings.CredentialsUpdatedAt == keysChangedAt;
            if (applied)
            {
                settings.ReplaceVerifiedCredentials(encrypted, now, BillingProviderSettings.MaskUsername(request.Username), check.Message);
            }
        }

        Apply();
        var saved = await transactions.SaveAsync(Apply, CancellationToken.None);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        if (!applied)
        {
            return new FiscalConnectionVerificationDto(false, "Las llaves o el ambiente cambiaron mientras se probaban: no se cambió nada.", environment.Db(), now, true);
        }

        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_CREDENTIALS_CHANGED", "BillingProviderSettings", null, $"Facturación electrónica ({provider.Name})",
                $"Credenciales del proveedor {provider.Name} cambiadas tras verificarlas en {environment.Db()} (cifradas; no se registran). "
                + $"El modo {settings.Mode.Db()} no cambió.",
                Severity: AuditSeverity.Critical),
            CancellationToken.None);
        await transactions.SaveAsync(cancellationToken: CancellationToken.None);
        return new FiscalConnectionVerificationDto(true, check.Message, environment.Db(), now);
    }
}

internal static class CandidateCheck
{
    public static Task<FiscalConnectionCheck> RunAsync(
        IFiscalProvider provider, FiscalEnvironment environment, string username, string password, string clientId, string clientSecret,
        CancellationToken cancellationToken) =>
        provider.VerifyAsync(
            new FiscalConnection(environment, new FiscalCredentials(username.Trim(), password, clientId.Trim(), clientSecret.Trim())), cancellationToken);

    /// <summary>Huella (SHA-256) de un juego de llaves para el freno: nunca se guarda ni se registra.</summary>
    public static string Fingerprint(string purpose, FiscalEnvironment environment, string username, string password, string clientId, string clientSecret) =>
        purpose + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('\n', environment.Db(), username.Trim(), password, clientId.Trim(), clientSecret.Trim()))));
}

/// <summary>Atraso retenido del ambiente vigente y de otros ambientes (PB1).</summary>
internal static class HeldDocuments
{
    public static async Task<FiscalHeldCount> CountAsync(IFiscalDocumentStore documents, BillingProviderSettings? settings, CancellationToken cancellationToken) =>
        settings is null ? new FiscalHeldCount(0, 0) : await documents.CountHeldAsync(settings.HeldBefore, settings.Environment, cancellationToken);
}

/// <summary>
/// "Enviar ahora" el atraso retenido (PA1): los documentos electrónicos del ambiente vigente emitidos antes de la primera verificación
/// correcta se envían como los demás (con la facturación encendida), con la fecha del día del envío (Factus no recibe fecha de emisión). Los
/// de OTRO ambiente nunca salen (PB1). Devuelve cuántos quedan en cola. Permitido con la licencia restringida (obligación ante la DIAN).
/// </summary>
public sealed record ReleaseFiscalBacklogCommand : ICommand<int>, IAllowedWhenRestricted;

internal sealed class ReleaseFiscalBacklogHandler(IBillingSettingsStore store, IFiscalDocumentStore documents, IInstallationContext installation, IAuditWriter audit)
    : ICommandHandler<ReleaseFiscalBacklogCommand, int>
{
    public async Task<Result<int>> Handle(ReleaseFiscalBacklogCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        if (await store.GetAsync(companyId, cancellationToken) is not { IsVerified: true } settings)
        {
            return BillingErrors.VerificationRequired;
        }

        if (settings.HeldBefore is null)
        {
            return 0;
        }

        var count = (await HeldDocuments.CountAsync(documents, settings, cancellationToken)).BeforeConnection;
        settings.ReleaseBacklog();
        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_BACKLOG_RELEASED", "BillingProviderSettings", null, "Facturación electrónica",
                $"Enviar ahora: {count} documento(s) electrónico(s) de antes de conectar el proveedor ({settings.Environment.Db()}) quedan en la cola de envío.",
                NewValues: new Dictionary<string, object?> { ["Documentos"] = count, ["Ambiente"] = settings.Environment.Db() },
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return count;
    }
}

/// <summary>
/// "No enviar" el atraso retenido (PA1, PB1, PB7): los documentos de antes de conectar y los de OTRO ambiente (pendientes, con error, en
/// contingencia o reclamados) quedan NOT_SENT y la cola no los vuelve a tomar. IRREVERSIBLE. Devuelve cuántos. No se permite con la licencia
/// restringida.
/// </summary>
public sealed record DiscardFiscalBacklogCommand : ICommand<int>;

internal sealed class DiscardFiscalBacklogHandler(
    IBillingSettingsStore store, IFiscalDocumentStore documents, IInstallationContext installation, ICurrentUser user, IAuditWriter audit, IIdGenerator ids,
    IClock clock)
    : ICommandHandler<DiscardFiscalBacklogCommand, int>
{
    public async Task<Result<int>> Handle(DiscardFiscalBacklogCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        if (await store.GetAsync(companyId, cancellationToken) is not { } settings)
        {
            return 0;
        }

        var now = clock.UtcNow;
        var count = 0;
        foreach (var document in await documents.ListHeldAsync(settings.HeldBefore, settings.Environment, cancellationToken))
        {
            var reason = document.Environment != settings.Environment
                ? $"No enviar: documento de otro ambiente ({document.Environment?.Db() ?? "desconocido"})."
                : "No enviar: atraso de antes de conectar el proveedor.";
            count += document.DiscardBacklog(reason, now, user.UserId, ids.NewId) ? 1 : 0;
        }

        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_BACKLOG_DISCARDED", "BillingProviderSettings", null, "Facturación electrónica",
                $"No enviar: {count} documento(s) electrónico(s) de antes de conectar el proveedor o de otro ambiente quedan como NO ENVIADOS (irreversible).",
                NewValues: new Dictionary<string, object?> { ["Documentos"] = count },
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return count;
    }
}

// ─────────────────────────────── Rangos ───────────────────────────────

public sealed record ListFiscalRangesQuery : IQuery<IReadOnlyList<FiscalRangeDto>>;

internal sealed class ListFiscalRangesHandler(IFiscalRangeStore ranges, ISettingsReader settings, IInstallationContext installation, IClock clock)
    : IQueryHandler<ListFiscalRangesQuery, IReadOnlyList<FiscalRangeDto>>
{
    public async Task<Result<IReadOnlyList<FiscalRangeDto>>> Handle(ListFiscalRangesQuery request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        var context = new SettingContext(companyId);
        var percent = await settings.GetAsync(BillingSettings.RangeAlertPercent, context, cancellationToken);
        var days = await settings.GetAsync(BillingSettings.RangeAlertDays, context, cancellationToken);
        return (await ranges.ListAsync(cancellationToken))
            .Select(r => r.ToDto(clock.Today, percent, days))
            .OrderBy(r => r.DocumentType, StringComparer.Ordinal).ThenBy(r => r.Prefix, StringComparer.Ordinal).ThenBy(r => r.From)
            .ToList();
    }
}

/// <summary>
/// Asigna un rango a una sucursal (y opcionalmente a una caja) o lo desasigna (sin sucursal). El rango que ocupaba ese lugar para
/// el mismo tipo de documento se desasigna.
/// </summary>
public sealed record AssignFiscalRangeCommand(Guid RangeId, Guid? BranchId, Guid? PosTerminalId) : ICommand<FiscalRangeDto>;

internal sealed class AssignFiscalRangeHandler(
    IFiscalRangeStore ranges, ISettingsReader settings, IInstallationContext installation, IAuditWriter audit, IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<AssignFiscalRangeCommand, FiscalRangeDto>
{
    public async Task<Result<FiscalRangeDto>> Handle(AssignFiscalRangeCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        var range = await ranges.GetAsync(request.RangeId, cancellationToken);
        if (range is null)
        {
            return BillingErrors.RangeNotFound;
        }

        if (request.BranchId is not null && !range.IsActive)
        {
            return BillingErrors.RangeNotUsable;
        }

        if (request.PosTerminalId is not null && request.BranchId is null)
        {
            return BillingErrors.InvalidAssignment;
        }

        // Libera el lugar primero (índice único por sucursal, caja y tipo) y luego asigna.
        var displaced = (await ranges.ListAsync(cancellationToken))
            .Where(r => r.Id != range.Id && r.IsActive && request.BranchId is not null && r.BranchId == request.BranchId
                        && r.PosTerminalId == request.PosTerminalId && r.DocumentType == range.DocumentType)
            .ToList();
        foreach (var other in displaced)
        {
            other.Assign(null, null);
        }

        if (displaced.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var before = range.BranchId is null ? "sin asignar" : $"sucursal {range.BranchId}{(range.PosTerminalId is null ? string.Empty : $", caja {range.PosTerminalId}")}";
        var assigned = range.Assign(request.BranchId, request.PosTerminalId);
        if (assigned.IsFailure)
        {
            return assigned.Error;
        }

        var after = range.BranchId is null ? "sin asignar" : $"sucursal {range.BranchId}{(range.PosTerminalId is null ? string.Empty : $", caja {range.PosTerminalId}")}";
        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_RANGE_ASSIGNED", nameof(FiscalNumberingRange), range.Id, range.AuditLabel,
                $"Rango {range.Prefix} {range.RangeFrom}-{range.RangeTo}: {before} → {after}{(displaced.Count > 0 ? $" (reemplaza {displaced.Count})" : string.Empty)}.",
                Severity: AuditSeverity.Warning),
            cancellationToken);

        var context = new SettingContext(companyId);
        return range.ToDto(
            clock.Today, await settings.GetAsync(BillingSettings.RangeAlertPercent, context, cancellationToken),
            await settings.GetAsync(BillingSettings.RangeAlertDays, context, cancellationToken));
    }
}

// ─────────────────────────────── Conciliación y alertas ───────────────────────────────

/// <summary>Resumen por día de la sucursal: ventas vs. documentos aceptados, pendientes, rechazados y en contingencia (máximo 92 días).</summary>
public sealed record GetFiscalReconciliationQuery(DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<FiscalReconciliationDayDto>>;

internal sealed class GetFiscalReconciliationHandler(IFiscalReadModel readModel, IInstallationContext installation, IClock clock)
    : IQueryHandler<GetFiscalReconciliationQuery, IReadOnlyList<FiscalReconciliationDayDto>>
{
    public async Task<Result<IReadOnlyList<FiscalReconciliationDayDto>>> Handle(GetFiscalReconciliationQuery request, CancellationToken cancellationToken)
    {
        if (installation.BranchId is not { } branch)
        {
            return BillingGuards.SetupRequired;
        }

        var to = request.To ?? clock.Today;
        var from = request.From ?? to.AddDays(-6);
        if (from > to || to.DayNumber - from.DayNumber > 92)
        {
            return Error.Validation("BILLING.INVALID_PERIOD", "El periodo debe ser válido y de máximo 92 días.");
        }

        return Result.Success(await readModel.ReconcileAsync(branch, from, to, cancellationToken));
    }
}

public sealed record GetFiscalAlertsQuery : IQuery<FiscalAlertsDto>;

internal sealed class GetFiscalAlertsHandler(
    IFiscalReadModel readModel, IFiscalRangeStore ranges, IBillingSettingsStore store, IFiscalDocumentStore documents, ISettingsReader settings,
    IInstallationContext installation, IClock clock)
    : IQueryHandler<GetFiscalAlertsQuery, FiscalAlertsDto>
{
    public async Task<Result<FiscalAlertsDto>> Handle(GetFiscalAlertsQuery request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        var context = new SettingContext(companyId);
        var hours = await settings.GetAsync(BillingSettings.PendingAlertHours, context, cancellationToken);
        var percent = await settings.GetAsync(BillingSettings.RangeAlertPercent, context, cancellationToken);
        var days = await settings.GetAsync(BillingSettings.RangeAlertDays, context, cancellationToken);
        var configured = await store.GetAsync(companyId, cancellationToken);
        // Lo retenido (antes de conectar u otro ambiente) no cuenta como pendiente vencido (PB7): tiene su propio aviso.
        var attention = await readModel.AttentionAsync(
            companyId, clock.UtcNow.AddHours(-hours), configured?.HeldBefore, configured?.Environment, cancellationToken);
        var today = clock.Today;
        var rangeAlerts = (await ranges.ListAsync(cancellationToken))
            .Where(r => r.BranchId is not null && r.NeedsAlert(today, percent, days))
            .Select(r => new FiscalRangeAlertDto(
                r.Id, r.Prefix, r.DocumentType.Db(), r.UsagePercent, r.ValidTo,
                !r.IsUsableOn(today) ? "El rango está agotado o vencido: la facturación queda pendiente."
                : r.UsagePercent >= percent ? $"Quedan {r.Remaining} números ({r.UsagePercent:N2} % usado)."
                : $"Vence en {r.DaysToExpire(today)} días."))
            .ToList();
        var mode = await BillingService.ModeAsync(store, settings, companyId, installation.BranchId, cancellationToken);
        var notice = configured is { AutoDisabledAt: not null, Mode: BillingMode.Off } ? BillingGuards.AutoDisabledNotice : null;
        var held = await HeldDocuments.CountAsync(documents, configured, cancellationToken);
        return new FiscalAlertsDto(
            mode.Db(), attention.PendingOverdue, attention.Rejected, attention.WithoutRange, rangeAlerts, attention.OldestPendingAt, notice, held.Total);
    }
}
