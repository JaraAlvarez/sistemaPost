using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Text;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Cash.Application;

// ─────────────────────────────── Puertos ───────────────────────────────

/// <summary>Llaves del convenio de la tienda con Sistecrédito. <see cref="ToString"/> no las muestra.</summary>
public sealed record SistecreditoCredentials(string SubscriptionKey, string ApplicationKey, string ApplicationToken)
{
    /// <summary>
    /// Las llaves llegan en el correo de Sistecrédito ("Vendor ID  &lt;código&gt;", "Subscription Key  &lt;código&gt;") y al copiarlas es fácil
    /// arrastrar la etiqueta. Ninguna llave tiene espacios: se conserva solo la última palabra (como en tutiendanueva).
    /// </summary>
    public static string Clean(string? value) =>
        (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } parts
            ? parts[^1]
            : string.Empty;

    public override string ToString() => "SistecreditoCredentials { *** }";
}

/// <summary>Llaves y ambiente con que se habla con Sistecrédito.</summary>
public sealed record SistecreditoConnection(SistecreditoCredentials Credentials, SistecreditoEnvironment Environment);

public sealed record SistecreditoCreateRequest(
    string Invoice, string Description, decimal Amount, string DocumentType, string DocumentNumber, string UrlResponse, string UrlConfirmation);

public sealed record SistecreditoCreated(string TransactionId, string? TransactionStatus);

/// <summary>Transacción leída de <c>GetTransactionResponse</c>: estado, URL de pago (cuando aparece), motivo y cuotas si las informa.</summary>
public sealed record SistecreditoTransaction(string? TransactionId, string? Status, string? RedirectUrl, string? Description, int? Installments);

/// <summary>Resultado de verificar las llaves. <c>WorkingEnvironment</c>: el otro ambiente, si allá sí las reconocen.</summary>
public sealed record SistecreditoVerification(bool Ok, string Message, SistecreditoEnvironment? WorkingEnvironment);

public enum SistecreditoFailure
{
    /// <summary>Sin internet, tiempo agotado o Sistecrédito no responde.</summary>
    Network,

    /// <summary>Sistecrédito respondió con un error (ya traducido en el mensaje).</summary>
    Provider,
}

/// <summary>Falla al hablar con Sistecrédito. El mensaje ya está en español simple; nunca contiene llaves ni el documento completo.</summary>
public sealed class SistecreditoGatewayException : Exception
{
    public SistecreditoGatewayException()
    {
    }

    public SistecreditoGatewayException(string message)
        : base(message)
    {
    }

    public SistecreditoGatewayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SistecreditoGatewayException(string message, SistecreditoFailure failure, int? errorCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        ErrorCode = errorCode;
    }

    public SistecreditoFailure Failure { get; }

    public int? ErrorCode { get; }
}

/// <summary>
/// API de pagos de Sistecrédito (D7B-01): <c>POST /pay/create</c>, <c>GET /pay/GetTransactionResponse</c>. El adaptador real usa
/// HttpClient; las pruebas usan uno simulado. Lanza <see cref="SistecreditoGatewayException"/>.
/// </summary>
public interface ISistecreditoGateway
{
    Task<SistecreditoCreated> CreateAsync(SistecreditoConnection connection, SistecreditoCreateRequest request, CancellationToken cancellationToken);

    Task<SistecreditoTransaction> GetTransactionAsync(SistecreditoConnection connection, string transactionId, CancellationToken cancellationToken);

    /// <summary>
    /// Consulta hasta que aparece la URL de pago o un estado de fallo (Sistecrédito no siempre la entrega al crear). Si se agotan los
    /// intentos, devuelve la última respuesta (sin URL).
    /// </summary>
    Task<SistecreditoTransaction> WaitForRedirectUrlAsync(
        SistecreditoConnection connection, string transactionId, int attempts, TimeSpan interval, CancellationToken cancellationToken);

    /// <summary>"Verificar conexión": detecta llaves no reconocidas y llaves del otro ambiente. No lanza.</summary>
    Task<SistecreditoVerification> VerifyCredentialsAsync(SistecreditoConnection connection, CancellationToken cancellationToken);
}

/// <summary>Cifra y descifra las llaves (DPAPI de la máquina, igual que las credenciales de Factus).</summary>
public interface ISistecreditoCredentialProtector
{
    byte[] Protect(SistecreditoCredentials credentials);

    /// <summary><c>null</c> si no se puede (p. ej. la BD se restauró en otro equipo: hay que volver a ingresarlas).</summary>
    SistecreditoCredentials? Unprotect(byte[] protectedBytes);
}

/// <summary>Configuración y cobros de Sistecrédito (EF Core).</summary>
public interface ISistecreditoStore
{
    void Add(SistecreditoSettings settings);

    void Add(SistecreditoAttempt attempt);

    Task<SistecreditoSettings?> GetSettingsAsync(Guid companyId, CancellationToken cancellationToken);

    Task<SistecreditoAttempt?> GetAttemptAsync(Guid id, CancellationToken cancellationToken);

    Task<SistecreditoAttempt?> FindByTransactionAsync(string transactionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SistecreditoAttempt>> ListForSaleAsync(Guid saleId, CancellationToken cancellationToken);

    /// <summary>
    /// Para la revisión automática: pendientes con más de un minuto, cancelados en la caja en las últimas 24 h que Sistecrédito no dio
    /// por fallidos (el cliente pudo aprobar después) y aprobados sin venta.
    /// </summary>
    Task<IReadOnlyList<SistecreditoAttempt>> ListToReconcileAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<SistecreditoAttempt>> ListAsync(SistecreditoAttemptStatus? status, int limit, CancellationToken cancellationToken);

    Task<int> CountPendingReviewsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Ajustes técnicos (<c>Pos:Sistecredito</c>). Las URL de respuesta y confirmación son obligatorias en la API, pero el POS no las usa
/// (no tiene dirección pública, D7B-03): apuntan a páginas informativas de BusinessPost.
/// </summary>
public sealed class SistecreditoOptions
{
    public string ResponseUrl { get; set; } = "https://businesspost.tutiendanueva.com/";

    public string ConfirmationUrl { get; set; } = "https://businesspost.tutiendanueva.com/sistecredito/confirmacion";

    public string Description { get; set; } = "Compra en tienda física";

    /// <summary>Consultas para obtener la URL de pago al crear el cobro (⚙️ 3 × 1,5 s); si no llega, la caja la sigue consultando.</summary>
    public int RedirectPollAttempts { get; set; } = 3;

    public int RedirectPollIntervalMilliseconds { get; set; } = 1500;

    /// <summary>Tiempo máximo de cada petición a Sistecrédito (⚙️ 20 s).</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Cada cuánto corre la revisión automática (⚙️ 60 s).</summary>
    public int ReconcileIntervalSeconds { get; set; } = 60;

    /// <summary>Minutos tras la aprobación sin venta completada para marcar "Requiere revisión" (⚙️ 15).</summary>
    public int ReviewAfterMinutes { get; set; } = 15;

    /// <summary>Apagar la revisión automática (pruebas).</summary>
    public bool Worker { get; set; } = true;
}

// ─────────────────────────────── Apoyo ───────────────────────────────

public static class SistecreditoMapping
{
    public static SistecreditoPaymentDto ToDto(this SistecreditoAttempt a, string? warning = null) => new(
        a.Id, a.SaleId, a.Amount, a.DocumentType, a.MaskedDocument, a.Environment.UpperSnake(), a.TransactionId, a.Status.UpperSnake(), a.ProviderStatus,
        a.RedirectUrl, a.Installments, a.Message, warning, a.StartedAt, a.ApprovedAt, a.CompletedSaleId, a.ReviewRequiredAt, a.ReviewedAt, a.ReviewNote);

    public static SistecreditoEnvironment? ParseEnvironment(string? value) => EnumText.Normalize(value) switch
    {
        "STAGING" or "PRUEBAS" => SistecreditoEnvironment.Staging,
        "PRODUCTION" or "PRODUCCION" => SistecreditoEnvironment.Production,
        _ => null,
    };

    public static string Name(SistecreditoEnvironment environment) =>
        environment == SistecreditoEnvironment.Staging ? "pruebas (Staging)" : "producción";
}

/// <summary>Lee la configuración y descifra las llaves para hablar con Sistecrédito.</summary>
public sealed class SistecreditoConnections(ISistecreditoStore store, ISistecreditoCredentialProtector protector, IInstallationContext installation)
{
    public async Task<Result<(SistecreditoSettings Settings, SistecreditoConnection Connection)>> EnabledAsync(CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId
            || await store.GetSettingsAsync(companyId, cancellationToken) is not { IsEnabled: true, HasCredentials: true } settings)
        {
            return SistecreditoErrors.NotConfigured;
        }

        return protector.Unprotect(settings.Credentials!) is { } credentials
            ? (settings, new SistecreditoConnection(credentials, settings.Environment))
            : SistecreditoErrors.CredentialsUnreadable;
    }

    public static Error ToError(SistecreditoGatewayException exception) =>
        exception.Failure == SistecreditoFailure.Network ? SistecreditoErrors.Offline : SistecreditoErrors.Provider(exception.Message);
}

/// <summary>Arma la configuración que ve la administración.</summary>
public sealed class SistecreditoSettingsViews(ISistecreditoStore store, ICashStore cash)
{
    public async Task<SistecreditoSettingsDto> BuildAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var settings = await store.GetSettingsAsync(companyId, cancellationToken);
        var method = (await cash.GetPaymentMethodsAsync(cancellationToken)).FirstOrDefault(m => m.Code == PaymentMethod.SistecreditoCode);
        return new SistecreditoSettingsDto(
            settings?.HasCredentials ?? false, settings?.IsEnabled ?? false, (settings?.Environment ?? SistecreditoEnvironment.Staging).UpperSnake(),
            settings?.CredentialsUpdatedAt, settings?.KeyHint, settings?.LastVerifiedAt, settings?.LastVerificationOk, settings?.LastVerificationMessage,
            method?.Status == MasterStatus.Active, method?.Id, await store.CountPendingReviewsAsync(cancellationToken));
    }

    /// <summary>El medio de pago SISTECREDITO se activa y se inactiva con la configuración.</summary>
    public async Task SyncPaymentMethodAsync(bool active, CancellationToken cancellationToken)
    {
        if ((await cash.GetPaymentMethodsAsync(cancellationToken)).FirstOrDefault(m => m.Code == PaymentMethod.SistecreditoCode) is { } method)
        {
            method.Update(method.Name, method.DianCode, requiresReference: true, method.SortOrder, active);
        }
    }
}

// ─────────────────────────────── Configuración (administración) ───────────────────────────────

public sealed record GetSistecreditoSettingsQuery : IQuery<SistecreditoSettingsDto>;

internal sealed class GetSistecreditoSettingsHandler(SistecreditoSettingsViews views, IInstallationContext installation)
    : IQueryHandler<GetSistecreditoSettingsQuery, SistecreditoSettingsDto>
{
    public async Task<Result<SistecreditoSettingsDto>> Handle(GetSistecreditoSettingsQuery request, CancellationToken cancellationToken) =>
        installation.CompanyId is { } companyId ? await views.BuildAsync(companyId, cancellationToken) : CashAppErrors.SetupRequired;
}

/// <summary>
/// Guarda las llaves (si llegan las tres: se limpian, se cifran y nunca se devuelven ni se registran), el ambiente y el encendido. Sin llaves
/// nuevas se conservan las guardadas. El medio de pago SISTECREDITO queda activo o inactivo según <c>Enabled</c>.
/// </summary>
public sealed record SaveSistecreditoSettingsCommand(string? SubscriptionKey, string? ApplicationKey, string? ApplicationToken, string Environment, bool Enabled)
    : ICommand<SistecreditoSettingsDto>
{
    public override string ToString() => $"SaveSistecreditoSettingsCommand {{ Environment = {Environment}, Enabled = {Enabled}, Llaves = *** }}";
}

internal sealed class SaveSistecreditoSettingsHandler(
    ISistecreditoStore store, ISistecreditoCredentialProtector protector, SistecreditoSettingsViews views, IInstallationContext installation,
    IAuditWriter audit, IClock clock) : ICommandHandler<SaveSistecreditoSettingsCommand, SistecreditoSettingsDto>
{
    public async Task<Result<SistecreditoSettingsDto>> Handle(SaveSistecreditoSettingsCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return CashAppErrors.SetupRequired;
        }

        if (SistecreditoMapping.ParseEnvironment(request.Environment) is not { } environment)
        {
            return SistecreditoErrors.InvalidEnvironment;
        }

        var keys = new[] { request.SubscriptionKey, request.ApplicationKey, request.ApplicationToken }.Select(SistecreditoCredentials.Clean).ToArray();
        var newKeys = keys.Any(k => k.Length > 0);
        if (newKeys && keys.Any(k => k.Length is 0 or > 200))
        {
            return SistecreditoErrors.InvalidCredentials;
        }

        var settings = await store.GetSettingsAsync(companyId, cancellationToken);
        if (settings is null)
        {
            settings = SistecreditoSettings.Create(companyId);
            store.Add(settings);
        }

        var before = (settings.Environment, settings.IsEnabled);
        var configured = settings.Configure(
            newKeys ? protector.Protect(new SistecreditoCredentials(keys[0], keys[1], keys[2])) : null, newKeys ? keys[0] : null, environment, request.Enabled,
            clock.UtcNow);
        if (configured.IsFailure)
        {
            return configured.Error;
        }

        await views.SyncPaymentMethodAsync(settings.IsEnabled, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_SETTINGS_CHANGED", nameof(SistecreditoSettings), null, "Sistecrédito",
                $"Sistecrédito: ambiente {SistecreditoMapping.Name(environment)}, {(settings.IsEnabled ? "activo" : "inactivo")}"
                + (newKeys ? "; llaves nuevas (cifradas; no se registran)." : "."),
                OldValues: new Dictionary<string, object?> { ["Ambiente"] = before.Environment.UpperSnake(), ["Activo"] = before.IsEnabled },
                NewValues: new Dictionary<string, object?> { ["Ambiente"] = environment.UpperSnake(), ["Activo"] = settings.IsEnabled, ["LlavesNuevas"] = newKeys },
                Severity: newKeys ? AuditSeverity.Critical : AuditSeverity.Warning),
            cancellationToken);
        return await views.BuildAsync(companyId, cancellationToken);
    }
}

/// <summary>Apaga Sistecrédito (las llaves se conservan cifradas) e inactiva su medio de pago.</summary>
public sealed record DisableSistecreditoCommand : ICommand<SistecreditoSettingsDto>;

internal sealed class DisableSistecreditoHandler(ISistecreditoStore store, SistecreditoSettingsViews views, IInstallationContext installation, IAuditWriter audit)
    : ICommandHandler<DisableSistecreditoCommand, SistecreditoSettingsDto>
{
    public async Task<Result<SistecreditoSettingsDto>> Handle(DisableSistecreditoCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return CashAppErrors.SetupRequired;
        }

        if (await store.GetSettingsAsync(companyId, cancellationToken) is { } settings)
        {
            settings.Disable();
        }

        await views.SyncPaymentMethodAsync(false, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_DISABLED", nameof(SistecreditoSettings), null, "Sistecrédito", "Sistecrédito desactivado en la caja.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return await views.BuildAsync(companyId, cancellationToken);
    }
}

/// <summary>
/// "Verificar conexión" (D7B-02): crea una transacción de verificación (vence sola, no mueve dinero) y dice si Sistecrédito reconoce las
/// llaves; si no sirven en el ambiente elegido pero sí en el otro, dice cuál elegir.
/// </summary>
public sealed record VerifySistecreditoCommand : ICommand<SistecreditoVerificationDto>;

internal sealed class VerifySistecreditoHandler(
    ISistecreditoStore store, ISistecreditoCredentialProtector protector, ISistecreditoGateway gateway, IInstallationContext installation, IAuditWriter audit,
    IClock clock) : ICommandHandler<VerifySistecreditoCommand, SistecreditoVerificationDto>
{
    public async Task<Result<SistecreditoVerificationDto>> Handle(VerifySistecreditoCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return CashAppErrors.SetupRequired;
        }

        if (await store.GetSettingsAsync(companyId, cancellationToken) is not { HasCredentials: true } settings)
        {
            return SistecreditoErrors.CredentialsRequired;
        }

        if (protector.Unprotect(settings.Credentials!) is not { } credentials)
        {
            return SistecreditoErrors.CredentialsUnreadable;
        }

        var result = await gateway.VerifyCredentialsAsync(new SistecreditoConnection(credentials, settings.Environment), cancellationToken);
        settings.RecordVerification(clock.UtcNow, result.Ok, result.Message);
        await audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_CONNECTION_VERIFIED", nameof(SistecreditoSettings), null, "Sistecrédito",
                $"Verificación de Sistecrédito en {SistecreditoMapping.Name(settings.Environment)}: {(result.Ok ? "correcta" : "con problema")}. {result.Message}"),
            cancellationToken);
        return new SistecreditoVerificationDto(result.Ok, result.Message, result.WorkingEnvironment?.UpperSnake());
    }
}

// ─────────────────────────────── Cobro en la caja ───────────────────────────────

/// <summary>
/// Crea el cobro en Sistecrédito por el valor indicado (todo lo que falta o una parte, en pago mixto) con el documento del cliente y
/// devuelve la URL de pago para el QR cuando Sistecrédito ya la entregó. Necesita internet SOLO aquí (D7B-04). Si la venta ya tiene un
/// cobro aprobado sin usar por ese mismo valor, lo devuelve (nunca se cobra dos veces); los pendientes anteriores se cancelan.
/// </summary>
public sealed record StartSistecreditoPaymentCommand(Guid SaleId, decimal Amount, string DocumentType, string DocumentNumber)
    : ICommand<SistecreditoPaymentDto>, IAllowedWhenRestricted
{
    public override string ToString() =>
        $"StartSistecreditoPaymentCommand {{ SaleId = {SaleId}, Amount = {Amount}, DocumentType = {DocumentType}, DocumentNumber = {SistecreditoProtocol.Mask(DocumentNumber)} }}";
}

internal sealed class StartSistecreditoPaymentHandler(
    SistecreditoConnections connections,
    ISistecreditoStore store,
    ISistecreditoGateway gateway,
    ISaleChargeProbe sales,
    ICurrentUser current,
    IInstallationContext installation,
    SistecreditoOptions options,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<StartSistecreditoPaymentCommand, SistecreditoPaymentDto>
{
    public async Task<Result<SistecreditoPaymentDto>> Handle(StartSistecreditoPaymentCommand request, CancellationToken cancellationToken)
    {
        var enabled = await connections.EnabledAsync(cancellationToken);
        if (enabled.IsFailure)
        {
            return enabled.Error;
        }

        var (settings, connection) = enabled.Value;
        if (await sales.GetAsync(request.SaleId, cancellationToken) is not { Status: "OPEN" } sale
            || (current.PosTerminalId is { } terminal && terminal != sale.PosTerminalId))
        {
            return SistecreditoErrors.SaleNotOpen;
        }

        var now = clock.UtcNow;
        var created = SistecreditoAttempt.Create(
            ids.NewId(), installation.CompanyId!.Value, sale.SaleId, sale.PosTerminalId, request.Amount, request.DocumentType, request.DocumentNumber,
            settings.Environment, now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (request.Amount > sale.AmountDue)
        {
            return SistecreditoErrors.AmountAboveDue;
        }

        // Un cobro aprobado y sin usar por el mismo valor se reutiliza (el cliente ya pagó); los pendientes anteriores se cancelan.
        var previous = await store.ListForSaleAsync(sale.SaleId, cancellationToken);
        if (previous.FirstOrDefault(a => a.CompletedSaleId is null && a.ApprovedAt is not null && a.ReviewedAt is null && a.Amount == request.Amount
                && a.Status is SistecreditoAttemptStatus.Approved or SistecreditoAttemptStatus.ReviewRequired) is { } approved)
        {
            return approved.ToDto();
        }

        foreach (var pending in previous.Where(a => a.Status == SistecreditoAttemptStatus.Pending))
        {
            pending.CancelLocally("Reemplazado por un cobro nuevo en la caja.", now);
        }

        var attempt = created.Value;
        try
        {
            var response = await gateway.CreateAsync(
                connection,
                new SistecreditoCreateRequest(
                    attempt.Invoice, options.Description, attempt.Amount, attempt.DocumentType, attempt.DocumentNumber, options.ResponseUrl, options.ConfirmationUrl),
                cancellationToken);
            attempt.Started(response.TransactionId, response.TransactionStatus, clock.UtcNow);
        }
        catch (SistecreditoGatewayException ex)
        {
            return SistecreditoConnections.ToError(ex);
        }

        store.Add(attempt);
        string? warning = null;
        try
        {
            var transaction = await gateway.WaitForRedirectUrlAsync(
                connection, attempt.TransactionId!, Math.Clamp(options.RedirectPollAttempts, 1, 10),
                TimeSpan.FromMilliseconds(Math.Clamp(options.RedirectPollIntervalMilliseconds, 0, 5000)), cancellationToken);
            attempt.Apply(transaction.Status, transaction.RedirectUrl, transaction.Installments, transaction.Description, clock.UtcNow);
            if (attempt.Status == SistecreditoAttemptStatus.Pending && attempt.RedirectUrl is null)
            {
                warning = "Sistecrédito está tardando más de lo normal en entregar el enlace; se sigue consultando.";
            }
        }
        catch (SistecreditoGatewayException ex)
        {
            // El cobro ya existe en Sistecrédito: se guarda pendiente y la caja sigue consultando.
            warning = ex.Failure == SistecreditoFailure.Network ? "Sin conexión con Sistecrédito por un momento; se sigue consultando." : ex.Message;
        }

        await audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_PAYMENT_STARTED", nameof(SistecreditoAttempt), attempt.Id, $"Sistecrédito {attempt.TransactionId}",
                $"Cobro con Sistecrédito por {attempt.Amount:N0} al cliente {attempt.DocumentType} {attempt.MaskedDocument} ({SistecreditoMapping.Name(attempt.Environment)})."),
            cancellationToken);
        return attempt.ToDto(warning);
    }
}

/// <summary>
/// Estado del cobro: si sigue pendiente, consulta a Sistecrédito (la caja lo llama cada pocos segundos). Sin conexión no falla: devuelve el
/// último estado con un aviso y la caja sigue consultando.
/// </summary>
public sealed record CheckSistecreditoPaymentCommand(Guid AttemptId) : ICommand<SistecreditoPaymentDto>, IAllowedWhenRestricted;

internal sealed class CheckSistecreditoPaymentHandler(
    SistecreditoConnections connections, ISistecreditoStore store, ISistecreditoGateway gateway, IAuditWriter audit, IClock clock)
    : ICommandHandler<CheckSistecreditoPaymentCommand, SistecreditoPaymentDto>
{
    public async Task<Result<SistecreditoPaymentDto>> Handle(CheckSistecreditoPaymentCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetAttemptAsync(request.AttemptId, cancellationToken) is not { } attempt)
        {
            return SistecreditoErrors.AttemptNotFound;
        }

        if (attempt.Status != SistecreditoAttemptStatus.Pending || attempt.TransactionId is null)
        {
            return attempt.ToDto();
        }

        var enabled = await connections.EnabledAsync(cancellationToken);
        if (enabled.IsFailure)
        {
            return attempt.ToDto(enabled.Error.Message);
        }

        try
        {
            var transaction = await gateway.GetTransactionAsync(enabled.Value.Connection, attempt.TransactionId, cancellationToken);
            if (attempt.Apply(transaction.Status, transaction.RedirectUrl, transaction.Installments, transaction.Description, clock.UtcNow)
                && attempt.Status == SistecreditoAttemptStatus.Approved)
            {
                await SistecreditoAudit.ApprovedAsync(audit, attempt, cancellationToken);
            }

            return attempt.ToDto();
        }
        catch (SistecreditoGatewayException ex)
        {
            return attempt.ToDto(ex.Failure == SistecreditoFailure.Network ? "Sin conexión con Sistecrédito; se sigue intentando." : ex.Message);
        }
    }
}

/// <summary>
/// Cancela el cobro en la caja (Sistecrédito no tiene cómo anularlo). Antes pregunta: si el cliente ya aprobó, lo devuelve APROBADO para
/// completar la venta; si ya estaba aprobado y la caja igual lo cancela, queda "Requiere revisión" (la plata del cliente no se pierde).
/// </summary>
public sealed record CancelSistecreditoPaymentCommand(Guid AttemptId) : ICommand<SistecreditoPaymentDto>, IAllowedWhenRestricted;

internal sealed class CancelSistecreditoPaymentHandler(
    SistecreditoConnections connections, ISistecreditoStore store, ISistecreditoGateway gateway, IAuditWriter audit, IClock clock)
    : ICommandHandler<CancelSistecreditoPaymentCommand, SistecreditoPaymentDto>
{
    public async Task<Result<SistecreditoPaymentDto>> Handle(CancelSistecreditoPaymentCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetAttemptAsync(request.AttemptId, cancellationToken) is not { } attempt)
        {
            return SistecreditoErrors.AttemptNotFound;
        }

        if (attempt.Status == SistecreditoAttemptStatus.Pending && attempt.TransactionId is not null
            && await connections.EnabledAsync(cancellationToken) is { IsSuccess: true } enabled)
        {
            try
            {
                var transaction = await gateway.GetTransactionAsync(enabled.Value.Connection, attempt.TransactionId, cancellationToken);
                if (attempt.Apply(transaction.Status, transaction.RedirectUrl, transaction.Installments, transaction.Description, clock.UtcNow)
                    && attempt.Status == SistecreditoAttemptStatus.Approved)
                {
                    await SistecreditoAudit.ApprovedAsync(audit, attempt, cancellationToken);
                    return attempt.ToDto("El cliente ya había aprobado el crédito: complete la venta con Sistecrédito.");
                }
            }
            catch (SistecreditoGatewayException)
            {
                // Sin respuesta: se cancela en la caja y la revisión automática vigila si el cliente igual aprueba.
            }
        }

        var now = clock.UtcNow;
        switch (attempt.Status)
        {
            case SistecreditoAttemptStatus.Pending:
                attempt.CancelLocally("Cancelado en la caja.", now);
                await audit.WriteAsync(
                    new AuditEntry("cash", "SISTECREDITO_PAYMENT_CANCELLED", nameof(SistecreditoAttempt), attempt.Id, $"Sistecrédito {attempt.TransactionId}",
                        $"Cobro con Sistecrédito por {attempt.Amount:N0} cancelado en la caja."),
                    cancellationToken);
                break;
            case SistecreditoAttemptStatus.Approved when attempt.CompletedSaleId is null:
                attempt.RequireReview("Aprobado por el cliente y cancelado en la caja: reverse el pago en el portal de Sistecrédito o complete la venta.", now);
                await SistecreditoAudit.ReviewRequiredAsync(audit, attempt, cancellationToken);
                break;
        }

        return attempt.ToDto();
    }
}

internal static class SistecreditoAudit
{
    public static Task ApprovedAsync(IAuditWriter audit, SistecreditoAttempt attempt, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_PAYMENT_APPROVED", nameof(SistecreditoAttempt), attempt.Id, $"Sistecrédito {attempt.TransactionId}",
                $"Sistecrédito aprobó el cobro por {attempt.Amount:N0} ({attempt.DocumentType} {attempt.MaskedDocument})"
                + (attempt.Installments is { } cuotas ? $" a {cuotas} cuotas." : ".")),
            cancellationToken);

    public static Task ReviewRequiredAsync(IAuditWriter audit, SistecreditoAttempt attempt, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_REVIEW_REQUIRED", nameof(SistecreditoAttempt), attempt.Id, $"Sistecrédito {attempt.TransactionId}",
                $"Cobro aprobado por Sistecrédito ({attempt.Amount:N0}, {attempt.DocumentType} {attempt.MaskedDocument}) sin venta completada: {attempt.Message}",
                Severity: AuditSeverity.Warning),
            cancellationToken);
}

/// <summary>Un cobro (para el QR y la consulta de la administración).</summary>
public sealed record GetSistecreditoPaymentQuery(Guid AttemptId) : IQuery<SistecreditoPaymentDto>;

internal sealed class GetSistecreditoPaymentHandler(ISistecreditoStore store) : IQueryHandler<GetSistecreditoPaymentQuery, SistecreditoPaymentDto>
{
    public async Task<Result<SistecreditoPaymentDto>> Handle(GetSistecreditoPaymentQuery request, CancellationToken cancellationToken) =>
        await store.GetAttemptAsync(request.AttemptId, cancellationToken) is { } attempt ? attempt.ToDto() : SistecreditoErrors.AttemptNotFound;
}

/// <summary>Cobros recientes (administración); <c>Status</c> opcional, p. ej. REVIEW_REQUIRED.</summary>
public sealed record ListSistecreditoPaymentsQuery(string? Status, int? Limit) : IQuery<IReadOnlyList<SistecreditoPaymentDto>>;

internal sealed class ListSistecreditoPaymentsHandler(ISistecreditoStore store) : IQueryHandler<ListSistecreditoPaymentsQuery, IReadOnlyList<SistecreditoPaymentDto>>
{
    public async Task<Result<IReadOnlyList<SistecreditoPaymentDto>>> Handle(ListSistecreditoPaymentsQuery request, CancellationToken cancellationToken)
    {
        SistecreditoAttemptStatus? status = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!Enum.TryParse<SistecreditoAttemptStatus>(EnumText.ToEnumName(request.Status), out var parsed))
            {
                return Error.Validation("CASH.SISTECREDITO_INVALID_STATUS", "Estado desconocido.");
            }

            status = parsed;
        }

        return (await store.ListAsync(status, Math.Clamp(request.Limit ?? 100, 1, 500), cancellationToken)).Select(a => a.ToDto()).ToList();
    }
}

/// <summary>La administración cierra un caso "Requiere revisión" con una nota (p. ej. "Reversado en el portal de Sistecrédito").</summary>
public sealed record ResolveSistecreditoReviewCommand(Guid AttemptId, string Note) : ICommand<SistecreditoPaymentDto>;

internal sealed class ResolveSistecreditoReviewHandler(ISistecreditoStore store, IActorContext actor, IAuditWriter audit, IClock clock)
    : ICommandHandler<ResolveSistecreditoReviewCommand, SistecreditoPaymentDto>
{
    public async Task<Result<SistecreditoPaymentDto>> Handle(ResolveSistecreditoReviewCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetAttemptAsync(request.AttemptId, cancellationToken) is not { } attempt)
        {
            return SistecreditoErrors.AttemptNotFound;
        }

        var resolved = attempt.Resolve(request.Note, actor.ActorId!.Value, clock.UtcNow);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_REVIEW_RESOLVED", nameof(SistecreditoAttempt), attempt.Id, $"Sistecrédito {attempt.TransactionId}",
                $"Revisión del cobro con Sistecrédito por {attempt.Amount:N0}: {attempt.ReviewNote}"),
            cancellationToken);
        return attempt.ToDto();
    }
}

// ─────────────────────────────── Ventas y revisión automática ───────────────────────────────

/// <summary>Implementa <see cref="ISistecreditoPayments"/> para el módulo de ventas.</summary>
public sealed class SistecreditoPaymentsService(ISistecreditoStore store, IClock clock) : ISistecreditoPayments
{
    public async Task<Result> UseApprovedAsync(Guid saleId, string transactionId, decimal amount, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId) || await store.FindByTransactionAsync(transactionId.Trim(), cancellationToken) is not { } attempt)
        {
            return SistecreditoErrors.NotApproved;
        }

        return attempt.Use(saleId, amount, clock.UtcNow);
    }

    public Task<int> CountPendingReviewsAsync(CancellationToken cancellationToken = default) => store.CountPendingReviewsAsync(cancellationToken);
}

/// <summary>
/// Revisión automática (D7B-05), cada ⚙️ 60 s: pregunta a Sistecrédito por los cobros pendientes y por los cancelados en la caja (el
/// cliente pudo aprobar después) y marca "Requiere revisión" los aprobados que llevan ⚙️ 15 minutos sin venta completada. Lo que cambia
/// queda en la bitácora; la administración lo ve como aviso.
/// </summary>
public sealed class SistecreditoReconciler(
    ISistecreditoStore store, ISistecreditoCredentialProtector protector, ISistecreditoGateway gateway, IInstallationContext installation,
    SistecreditoOptions options, IAuditWriter audit)
{
    public async Task<(int Checked, int Changed)> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return (0, 0);
        }

        var settings = await store.GetSettingsAsync(companyId, cancellationToken);
        var credentials = settings is { HasCredentials: true } ? protector.Unprotect(settings.Credentials!) : null;
        var connection = credentials is null ? null : new SistecreditoConnection(credentials, settings!.Environment);
        var attempts = await store.ListToReconcileAsync(now, 50, cancellationToken);
        var reachable = connection is not null;
        var changed = 0;
        foreach (var attempt in attempts)
        {
            var before = attempt.Status;
            if (reachable && attempt.TransactionId is not null && attempt.Environment == connection!.Environment
                && attempt.Status is SistecreditoAttemptStatus.Pending or SistecreditoAttemptStatus.Cancelled)
            {
                try
                {
                    var transaction = await gateway.GetTransactionAsync(connection, attempt.TransactionId, cancellationToken);
                    attempt.Apply(transaction.Status, transaction.RedirectUrl, transaction.Installments, transaction.Description, now);
                }
                catch (SistecreditoGatewayException ex) when (ex.Failure == SistecreditoFailure.Network)
                {
                    reachable = false; // sin internet: se reintenta en la próxima vuelta
                }
                catch (SistecreditoGatewayException)
                {
                    // Error de Sistecrédito con este cobro (p. ej. 708 no encontrado): se vuelve a mirar después.
                }
            }

            if (attempt.Status == SistecreditoAttemptStatus.Approved && attempt.CompletedSaleId is null && attempt.ApprovedAt is { } approvedAt
                && now - approvedAt >= TimeSpan.FromMinutes(Math.Max(1, options.ReviewAfterMinutes)))
            {
                attempt.RequireReview(
                    "Sistecrédito aprobó el cobro pero la venta no se completó en la caja: complete la venta o reverse el pago en el portal de Sistecrédito.", now);
            }

            if (attempt.Status == before)
            {
                continue;
            }

            changed++;
            if (attempt.Status == SistecreditoAttemptStatus.Approved)
            {
                await SistecreditoAudit.ApprovedAsync(audit, attempt, cancellationToken);
            }
            else if (attempt.Status == SistecreditoAttemptStatus.ReviewRequired)
            {
                await SistecreditoAudit.ReviewRequiredAsync(audit, attempt, cancellationToken);
            }
        }

        return (attempts.Count, changed);
    }
}

internal static class CashAppErrors
{
    public static readonly Error SetupRequired = Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
}
