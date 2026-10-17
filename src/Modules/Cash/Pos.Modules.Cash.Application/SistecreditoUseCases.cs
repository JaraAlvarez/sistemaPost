using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
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

    /// <summary>Créditos «no usados» (liberados) en una caja entre dos momentos (para el cierre y el detalle de la jornada).</summary>
    Task<IReadOnlyList<SistecreditoAttempt>> ListReleasedAsync(Guid posTerminalId, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken);
}

/// <summary>
/// Ajustes técnicos (<c>Pos:Sistecredito</c>). Las URL de respuesta y confirmación son obligatorias en la API, pero el POS no las usa
/// (no tiene dirección pública, D7B-03): apuntan a páginas de la nube de BusinessPost (<c>/sistecredito/respuesta</c>, amable para el
/// cliente, y <c>/sistecredito/confirmacion</c>, que acepta el aviso con 200 sin guardar nada). El pago se confirma consultando.
/// </summary>
public sealed class SistecreditoOptions
{
    public string ResponseUrl { get; set; } = "https://businesspost.tutiendanueva.com/sistecredito/respuesta";

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

    /// <summary>Horas tras las que un cobro que sigue pendiente se da por vencido (después de una última consulta) (⚙️ 24).</summary>
    public int PendingExpiryHours { get; set; } = 24;

    /// <summary>
    /// Tiempo máximo de la nueva consulta a Sistecrédito justo antes de completar la venta con un cobro aprobado (⚙️ 5 s). Sin respuesta
    /// en ese tiempo se usa el último estado conocido (la venta no se bloquea por falta de internet).
    /// </summary>
    public int UseRecheckTimeoutSeconds { get; set; } = 5;

    /// <summary>Apagar la revisión automática (pruebas).</summary>
    public bool Worker { get; set; } = true;
}

// ─────────────────────────────── Apoyo ───────────────────────────────

public static class SistecreditoMapping
{
    public static SistecreditoPaymentDto ToDto(this SistecreditoAttempt a, string? warning = null) => new(
        a.Id, a.SaleId, a.Amount, a.DocumentType, a.MaskedDocument, a.Environment.UpperSnake(), a.TransactionId, a.Status.UpperSnake(), a.ProviderStatus,
        a.RedirectUrl, a.Installments, a.Message, warning, a.StartedAt, a.ApprovedAt, a.CompletedSaleId, a.ReviewRequiredAt, a.ReviewedAt, a.ReviewNote,
        a.IsUsable);

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
/// llaves; si no sirven en el ambiente elegido pero sí en el otro, dice cuál elegir. Hasta dos llamadas a Sistecrédito: va por fuera de
/// la transacción (<see cref="ISelfTransactedCommand"/>) y guarda el resultado en una transacción corta (solo si mientras tanto no
/// cambiaron las llaves ni el ambiente).
/// </summary>
public sealed record VerifySistecreditoCommand : ICommand<SistecreditoVerificationDto>, ISelfTransactedCommand;

internal sealed class VerifySistecreditoHandler(
    ISistecreditoStore store, ISistecreditoCredentialProtector protector, ISistecreditoGateway gateway, IInstallationContext installation, IAuditWriter audit,
    IShortTransactions transactions, IClock clock) : ICommandHandler<VerifySistecreditoCommand, SistecreditoVerificationDto>
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

        var environment = settings.Environment;
        var keysChangedAt = settings.CredentialsUpdatedAt;
        var result = await gateway.VerifyCredentialsAsync(new SistecreditoConnection(credentials, environment), cancellationToken);
        var now = clock.UtcNow;

        // El resultado es de ESTAS llaves en ESTE ambiente: si mientras se verificaba alguien las cambió, no se marca nada.
        var recorded = false;
        void Record()
        {
            recorded = settings.Environment == environment && settings.CredentialsUpdatedAt == keysChangedAt;
            if (recorded)
            {
                settings.RecordVerification(now, result.Ok, result.Message);
            }
        }

        Record();
        var saved = await transactions.SaveAsync(Record, CancellationToken.None);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_CONNECTION_VERIFIED", nameof(SistecreditoSettings), null, "Sistecrédito",
                recorded
                    ? $"Verificación de Sistecrédito en {SistecreditoMapping.Name(environment)}: {(result.Ok ? "correcta" : "con problema")}. {result.Message}"
                    : $"Verificación de Sistecrédito en {SistecreditoMapping.Name(environment)} descartada: las llaves o el ambiente cambiaron mientras se verificaba."),
            CancellationToken.None);
        await transactions.SaveAsync(cancellationToken: CancellationToken.None);
        return recorded
            ? new SistecreditoVerificationDto(result.Ok, result.Message, result.WorkingEnvironment?.UpperSnake())
            : new SistecreditoVerificationDto(false, "Las llaves o el ambiente cambiaron mientras se verificaba: vuelva a verificar.", null);
    }
}

// ─────────────────────────────── Cobro en la caja ───────────────────────────────

/// <summary>
/// Quién puede ver y operar los cobros: la caja de la venta (sesión de caja con su jornada ABIERTA) o, para consultarlos, la administración
/// (permiso de configurar medios de pago). Las URL de pago y los números de transacción no se muestran a otras cajas.
/// </summary>
public sealed class SistecreditoAccess(ISaleChargeProbe sales, ICurrentUser current, ICashStore cash, IPermissionChecker permissions)
{
    public async Task<Result<SaleChargeInfo>> OpenSaleOfThisTerminalAsync(Guid saleId, CancellationToken cancellationToken)
    {
        if (current.PosTerminalId is not { } terminal
            || await sales.GetAsync(saleId, cancellationToken) is not { Status: "OPEN" } sale || sale.PosTerminalId != terminal
            || await cash.GetUnclosedSessionAsync(terminal, null, cancellationToken) is not { Status: CashSessionStatus.Open })
        {
            return SistecreditoErrors.SaleNotOpen;
        }

        return sale;
    }

    public async Task<bool> CanSeeAsync(SistecreditoAttempt attempt, CancellationToken cancellationToken) =>
        current.PosTerminalId is { } terminal
            ? attempt.PosTerminalId == terminal
            : await permissions.HasPermissionAsync(CashPermissions.PaymentMethodManage, null, cancellationToken);
}

/// <summary>
/// Crea el cobro en Sistecrédito por el valor indicado (todo lo que falta o una parte, en pago mixto, en pesos enteros) con el documento
/// del cliente y devuelve la URL de pago para el QR cuando Sistecrédito ya la entregó. Necesita internet SOLO aquí (D7B-04). Solo desde la
/// caja de la venta, con su jornada abierta.
/// <para>Nunca se cobra dos veces: los cobros aprobados y sin usar de la venta (aunque estén en "Requiere revisión") cuentan contra lo que
/// falta; si hay uno por el mismo valor que la caja todavía no tiene como pago (<c>HeldTransactionIds</c>), se devuelve ese, y si el nuevo
/// más los aprobados supera lo que falta, se rechaza.</para>
/// <para>Sin transacción larga (<see cref="ISelfTransactedCommand"/>): el cobro se guarda PENDIENTE antes de llamar a Sistecrédito, la
/// llamada va por fuera de la base de datos y lo que responde se guarda en transacciones cortas (con un reintento si otro proceso tocó la
/// fila); así nunca se pierde el registro de una transacción viva en Sistecrédito ni queda una conexión abierta mientras responde.</para>
/// </summary>
public sealed record StartSistecreditoPaymentCommand(
    Guid SaleId, decimal Amount, string DocumentType, string DocumentNumber, IReadOnlyList<string>? HeldTransactionIds = null)
    : ICommand<SistecreditoPaymentDto>, IAllowedWhenRestricted, ISelfTransactedCommand
{
    public override string ToString() =>
        $"StartSistecreditoPaymentCommand {{ SaleId = {SaleId}, Amount = {Amount}, DocumentType = {DocumentType}, DocumentNumber = {SistecreditoProtocol.Mask(DocumentNumber)} }}";
}

internal sealed class StartSistecreditoPaymentHandler(
    SistecreditoConnections connections,
    SistecreditoAccess access,
    ISistecreditoStore store,
    ISistecreditoGateway gateway,
    IInstallationContext installation,
    SistecreditoOptions options,
    IAuditWriter audit,
    IShortTransactions transactions,
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
        var open = await access.OpenSaleOfThisTerminalAsync(request.SaleId, cancellationToken);
        if (open.IsFailure)
        {
            return open.Error;
        }

        var sale = open.Value;
        var created = SistecreditoAttempt.Create(
            ids.NewId(), installation.CompanyId!.Value, sale.SaleId, sale.PosTerminalId, request.Amount, request.DocumentType, request.DocumentNumber,
            settings.Environment, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (request.Amount > sale.AmountDue)
        {
            return SistecreditoErrors.AmountAboveDue;
        }

        // Los aprobados sin usar de esta venta ya son plata del cliente: uno por el mismo valor (que la caja no tenga ya como pago) se
        // reutiliza y entre todos nunca superan lo que falta.
        var previous = await store.ListForSaleAsync(sale.SaleId, cancellationToken);
        var usable = previous.Where(a => a.IsUsable).ToList();
        var held = (request.HeldTransactionIds ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToHashSet(StringComparer.Ordinal);
        if (usable.FirstOrDefault(a => a.Amount == request.Amount && !held.Contains(a.TransactionId!)) is { } approved)
        {
            return approved.ToDto();
        }

        var approvedTotal = usable.Sum(a => a.Amount);
        if (approvedTotal > 0m && approvedTotal + request.Amount > sale.AmountDue)
        {
            return Error.BusinessRule(
                SistecreditoErrors.AlreadyApproved.Code,
                $"{SistecreditoErrors.AlreadyApproved.Message} Aprobado sin usar: {approvedTotal:N0}; con Sistecrédito puede cobrar como máximo "
                + $"{Math.Max(0m, decimal.Floor(sale.AmountDue - approvedTotal)):N0} más.");
        }

        // 1) Registro local primero (transacción corta): si algo falla después, el intento queda guardado.
        var attempt = created.Value;
        store.Add(attempt);
        var saved = await transactions.SaveAsync(cancellationToken: cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        // 2) Crear la transacción en Sistecrédito, por fuera de la base de datos.
        SistecreditoCreated response;
        try
        {
            response = await gateway.CreateAsync(
                connection,
                new SistecreditoCreateRequest(
                    attempt.Invoice, options.Description, attempt.Amount, attempt.DocumentType, attempt.DocumentNumber, options.ResponseUrl, options.ConfirmationUrl),
                cancellationToken);
        }
        catch (SistecreditoGatewayException ex)
        {
            var error = SistecreditoConnections.ToError(ex);
            var failedAt = clock.UtcNow;
            attempt.StartFailed(error.Message, failedAt);
            await transactions.SaveAsync(() => attempt.StartFailed(error.Message, failedAt), CancellationToken.None);
            return error;
        }

        // 3) La transacción ya existe en Sistecrédito: su número se guarda aunque la caja ya no espere (CancellationToken.None), con un
        //    reintento sobre la fila recargada si otro proceso la tocó.
        var startedAt = clock.UtcNow;
        attempt.Started(response.TransactionId, response.TransactionStatus, startedAt);
        await audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_PAYMENT_STARTED", nameof(SistecreditoAttempt), attempt.Id, $"Sistecrédito {attempt.TransactionId}",
                $"Cobro con Sistecrédito por {attempt.Amount:N0} al cliente {attempt.DocumentType} {attempt.MaskedDocument} ({SistecreditoMapping.Name(attempt.Environment)})."),
            CancellationToken.None);
        saved = await transactions.SaveAsync(
            () =>
            {
                if (attempt.TransactionId is null)
                {
                    attempt.Started(response.TransactionId, response.TransactionStatus, startedAt);
                }
            },
            CancellationToken.None);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        // Los pendientes anteriores de la venta se cancelan solo ahora, cuando el cobro nuevo quedó creado (aparte: si uno cambió mientras
        // tanto, se recarga y se cancela solo si sigue pendiente).
        var replaced = previous.Where(a => a.Id != attempt.Id && a.Status == SistecreditoAttemptStatus.Pending).ToList();
        if (replaced.Count > 0)
        {
            void CancelReplaced()
            {
                foreach (var pending in replaced.Where(a => a.Status == SistecreditoAttemptStatus.Pending))
                {
                    pending.CancelLocally("Reemplazado por un cobro nuevo en la caja.", startedAt);
                }
            }

            CancelReplaced();
            await transactions.SaveAsync(CancelReplaced, CancellationToken.None);
        }

        // 4) Esperar la URL de pago, también por fuera de la base de datos (si no llega, la caja la sigue consultando).
        string? warning = null;
        var fetchedAt = clock.UtcNow;
        SistecreditoTransaction? transaction = null;
        try
        {
            transaction = await gateway.WaitForRedirectUrlAsync(
                connection, attempt.TransactionId!, Math.Clamp(options.RedirectPollAttempts, 1, 10),
                TimeSpan.FromMilliseconds(Math.Clamp(options.RedirectPollIntervalMilliseconds, 0, 5000)), cancellationToken);
        }
        catch (SistecreditoGatewayException ex)
        {
            // El cobro ya existe en Sistecrédito: queda pendiente y la caja sigue consultando.
            warning = ex.Failure == SistecreditoFailure.Network ? "Sin conexión con Sistecrédito por un momento; se sigue consultando." : ex.Message;
        }

        if (transaction is { } found && await SistecreditoChecks.ApplyAndSaveAsync(attempt, found, fetchedAt, clock.UtcNow, transactions, audit) is { IsFailure: true } failed)
        {
            return failed.Error;
        }

        if (attempt.Status == SistecreditoAttemptStatus.Pending && attempt.RedirectUrl is null && warning is null)
        {
            warning = "Sistecrédito está tardando más de lo normal en entregar el enlace; se sigue consultando.";
        }

        return attempt.ToDto(warning);
    }
}

/// <summary>Aplicar una respuesta de Sistecrédito y guardarla sin pisar datos más nuevos; la bitácora de "aprobado" va después de guardar.</summary>
internal static class SistecreditoChecks
{
    public static async Task<Result> ApplyAndSaveAsync(
        SistecreditoAttempt attempt, SistecreditoTransaction transaction, DateTimeOffset fetchedAt, DateTimeOffset now, IShortTransactions transactions,
        IAuditWriter audit)
    {
        var before = attempt.Status;
        attempt.Apply(transaction.Status, transaction.RedirectUrl, transaction.Installments, transaction.Description, now);

        // Si al recargar otro proceso ya consultó DESPUÉS de esta respuesta, la respuesta es vieja: no se aplica.
        var saved = await transactions.SaveAsync(
            () =>
            {
                before = attempt.Status;
                if (attempt.LastCheckedAt is null || attempt.LastCheckedAt <= fetchedAt)
                {
                    attempt.Apply(transaction.Status, transaction.RedirectUrl, transaction.Installments, transaction.Description, now);
                }
            },
            CancellationToken.None);
        if (saved.IsFailure)
        {
            return saved;
        }

        if (before != SistecreditoAttemptStatus.Approved && attempt.Status == SistecreditoAttemptStatus.Approved)
        {
            await SistecreditoAudit.ApprovedAsync(audit, attempt, CancellationToken.None);
            await transactions.SaveAsync(cancellationToken: CancellationToken.None);
        }

        return Result.Success();
    }
}

/// <summary>
/// Cobros abiertos de una venta en curso de ESTA caja (con su jornada abierta): los aprobados y sin usar (aunque estén en "Requiere
/// revisión"), que la caja vuelve a poner como pago al abrir el cobro, y los pendientes, que la caja vuelve a mostrar y a vigilar.
/// </summary>
public sealed record ListOpenSistecreditoPaymentsQuery(Guid SaleId) : IQuery<IReadOnlyList<SistecreditoPaymentDto>>;

internal sealed class ListOpenSistecreditoPaymentsHandler(ISistecreditoStore store, SistecreditoAccess access)
    : IQueryHandler<ListOpenSistecreditoPaymentsQuery, IReadOnlyList<SistecreditoPaymentDto>>
{
    public async Task<Result<IReadOnlyList<SistecreditoPaymentDto>>> Handle(ListOpenSistecreditoPaymentsQuery request, CancellationToken cancellationToken)
    {
        var open = await access.OpenSaleOfThisTerminalAsync(request.SaleId, cancellationToken);
        if (open.IsFailure)
        {
            return open.Error;
        }

        return (await store.ListForSaleAsync(open.Value.SaleId, cancellationToken))
            .Where(a => a.IsUsable || (a.Status == SistecreditoAttemptStatus.Pending && a.TransactionId is not null))
            .Select(a => a.ToDto())
            .ToList();
    }
}

/// <summary>
/// Estado del cobro: si sigue pendiente, consulta a Sistecrédito con el ambiente del cobro (la caja lo llama cada pocos segundos). Sin
/// conexión no falla: devuelve el último estado con un aviso y la caja sigue consultando. La consulta va por fuera de la transacción.
/// Solo la caja del cobro.
/// </summary>
public sealed record CheckSistecreditoPaymentCommand(Guid AttemptId) : ICommand<SistecreditoPaymentDto>, IAllowedWhenRestricted, ISelfTransactedCommand;

internal sealed class CheckSistecreditoPaymentHandler(
    SistecreditoConnections connections, SistecreditoAccess access, ISistecreditoStore store, ISistecreditoGateway gateway, IAuditWriter audit,
    IShortTransactions transactions, IClock clock) : ICommandHandler<CheckSistecreditoPaymentCommand, SistecreditoPaymentDto>
{
    public async Task<Result<SistecreditoPaymentDto>> Handle(CheckSistecreditoPaymentCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetAttemptAsync(request.AttemptId, cancellationToken) is not { } attempt || !await access.CanSeeAsync(attempt, cancellationToken))
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

        var fetchedAt = clock.UtcNow;
        SistecreditoTransaction transaction;
        try
        {
            transaction = await gateway.GetTransactionAsync(enabled.Value.Connection with { Environment = attempt.Environment }, attempt.TransactionId, cancellationToken);
        }
        catch (SistecreditoGatewayException ex)
        {
            return attempt.ToDto(ex.Failure == SistecreditoFailure.Network ? "Sin conexión con Sistecrédito; se sigue intentando." : ex.Message);
        }

        var saved = await SistecreditoChecks.ApplyAndSaveAsync(attempt, transaction, fetchedAt, clock.UtcNow, transactions, audit);
        return saved.IsFailure ? saved.Error : attempt.ToDto();
    }
}

/// <summary>
/// Cancela un cobro PENDIENTE en la caja (Sistecrédito no tiene cómo anularlo). Antes pregunta (con el ambiente del cobro, por fuera de la
/// transacción): si el cliente ya aprobó, lo devuelve APROBADO sin cambiarlo. Un cobro aprobado nunca se cancela aquí: la caja lo usa como
/// pago o el supervisor autoriza «No usar este crédito». Si otro proceso tocó el cobro mientras tanto, se decide de nuevo sobre lo
/// recargado. Solo la caja del cobro.
/// </summary>
public sealed record CancelSistecreditoPaymentCommand(Guid AttemptId) : ICommand<SistecreditoPaymentDto>, IAllowedWhenRestricted, ISelfTransactedCommand;

internal sealed class CancelSistecreditoPaymentHandler(
    SistecreditoConnections connections, SistecreditoAccess access, ISistecreditoStore store, ISistecreditoGateway gateway, IAuditWriter audit,
    IShortTransactions transactions, IClock clock) : ICommandHandler<CancelSistecreditoPaymentCommand, SistecreditoPaymentDto>
{
    private const string AlreadyApproved =
        "El cliente ya aprobó el crédito: úselo como pago o, si el cliente paga con otro medio, use «No usar este crédito» (con autorización de supervisor).";

    public async Task<Result<SistecreditoPaymentDto>> Handle(CancelSistecreditoPaymentCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetAttemptAsync(request.AttemptId, cancellationToken) is not { } attempt || !await access.CanSeeAsync(attempt, cancellationToken))
        {
            return SistecreditoErrors.AttemptNotFound;
        }

        if (attempt.IsUsable)
        {
            return attempt.ToDto(AlreadyApproved);
        }

        if (attempt.Status == SistecreditoAttemptStatus.Pending && attempt.TransactionId is not null
            && await connections.EnabledAsync(cancellationToken) is { IsSuccess: true } enabled)
        {
            var fetchedAt = clock.UtcNow;
            try
            {
                var transaction = await gateway.GetTransactionAsync(
                    enabled.Value.Connection with { Environment = attempt.Environment }, attempt.TransactionId, cancellationToken);
                if (SistecreditoProtocol.Map(transaction.Status) == SistecreditoAttemptStatus.Approved)
                {
                    var approved = await SistecreditoChecks.ApplyAndSaveAsync(attempt, transaction, fetchedAt, clock.UtcNow, transactions, audit);
                    return approved.IsFailure ? approved.Error : attempt.ToDto(AlreadyApproved);
                }
            }
            catch (SistecreditoGatewayException)
            {
                // Sin respuesta: se cancela en la caja y la revisión automática vigila si el cliente igual aprueba.
            }
        }

        var now = clock.UtcNow;
        var cancelled = false;

        // Solo un pendiente se cancela; si al recargar ya estaba aprobado (lo vio otro proceso), se devuelve aprobado sin cambiarlo.
        void Decide() => cancelled = attempt.Status == SistecreditoAttemptStatus.Pending && attempt.CancelLocally("Cancelado en la caja.", now).IsSuccess;

        Decide();
        var saved = await transactions.SaveAsync(Decide, CancellationToken.None);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        if (attempt.IsUsable)
        {
            return attempt.ToDto(AlreadyApproved);
        }

        if (cancelled)
        {
            await audit.WriteAsync(
                new AuditEntry("cash", "SISTECREDITO_PAYMENT_CANCELLED", nameof(SistecreditoAttempt), attempt.Id, $"Sistecrédito {attempt.TransactionId}",
                    $"Cobro con Sistecrédito por {attempt.Amount:N0} cancelado en la caja."),
                CancellationToken.None);
            await transactions.SaveAsync(cancellationToken: CancellationToken.None);
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

internal sealed class GetSistecreditoPaymentHandler(ISistecreditoStore store, SistecreditoAccess access) : IQueryHandler<GetSistecreditoPaymentQuery, SistecreditoPaymentDto>
{
    /// <summary>Solo la caja del cobro o la administración (el QR y la transacción no se muestran a otras cajas).</summary>
    public async Task<Result<SistecreditoPaymentDto>> Handle(GetSistecreditoPaymentQuery request, CancellationToken cancellationToken) =>
        await store.GetAttemptAsync(request.AttemptId, cancellationToken) is { } attempt && await access.CanSeeAsync(attempt, cancellationToken)
            ? attempt.ToDto()
            : SistecreditoErrors.AttemptNotFound;
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
public sealed class SistecreditoPaymentsService(
    ISistecreditoStore store, SistecreditoConnections connections, ISistecreditoGateway gateway, SistecreditoOptions options, IAuditWriter audit,
    IShortTransactions transactions, IClock clock) : ISistecreditoPayments
{
    /// <summary>Solo marca el cobro como usado (sin llamar a Sistecrédito: la nueva consulta se hizo antes, en <see cref="RecheckForSaleAsync"/>).</summary>
    public async Task<Result> UseApprovedAsync(Guid saleId, string transactionId, decimal amount, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId) || await store.FindByTransactionAsync(transactionId.Trim(), cancellationToken) is not { } attempt)
        {
            return SistecreditoErrors.NotApproved;
        }

        return attempt.Use(saleId, amount, clock.UtcNow);
    }

    public async Task<IReadOnlyList<SistecreditoPendingUse>> ListUsableAsync(Guid saleId, CancellationToken cancellationToken = default) =>
        (await store.ListForSaleAsync(saleId, cancellationToken))
            .Where(a => a.IsUsable)
            .Select(a => new SistecreditoPendingUse(a.TransactionId!, a.Amount))
            .ToList();

    public async Task<Result> ReleaseAsync(Guid saleId, string transactionId, Guid userId, Guid? authorizedBy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId) || await store.FindByTransactionAsync(transactionId.Trim(), cancellationToken) is not { } attempt)
        {
            return SistecreditoErrors.AttemptNotFound;
        }

        var alreadyReleased = attempt.ReleasedAt is not null;
        var released = attempt.Release(saleId, userId, clock.UtcNow);
        if (released.IsFailure || alreadyReleased)
        {
            return released; // repetir «No usar» sobre el mismo cobro no cambia nada ni duplica la bitácora
        }

        await audit.WriteAsync(
            new AuditEntry("cash", "SISTECREDITO_RELEASED", nameof(SistecreditoAttempt), attempt.Id, $"Sistecrédito {attempt.TransactionId}",
                $"Crédito aprobado por Sistecrédito ({attempt.Amount:N0}, {attempt.DocumentType} {attempt.MaskedDocument}) no usado en la venta (el cliente "
                + "paga con otro medio). Reverse el pago en el portal de Sistecrédito.",
                AuthorizedBy: authorizedBy, Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Después de completar la venta: los cobros que seguían PENDIENTES (el cliente podría aprobarlos segundos después) se consultan una
    /// última vez (⚙️ máximo 5 s en total) y se cancelan en la caja; si ya estaban aprobados, quedan "Requiere revisión" para reversarlos.
    /// La revisión automática sigue mirando los cancelados 24 h por si el cliente aprueba después.
    /// </summary>
    public async Task SettleAfterSaleAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        var pending = (await store.ListForSaleAsync(saleId, cancellationToken))
            .Where(a => a.Status == SistecreditoAttemptStatus.Pending && a.TransactionId is not null && a.CompletedSaleId is null)
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var enabled = await connections.EnabledAsync(cancellationToken);
        var answers = new Dictionary<Guid, (SistecreditoTransaction Transaction, DateTimeOffset FetchedAt)>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.UseRecheckTimeoutSeconds, 1, 5)));
        foreach (var attempt in pending.Where(_ => enabled.IsSuccess))
        {
            var fetchedAt = clock.UtcNow;
            try
            {
                answers[attempt.Id] = (await gateway.GetTransactionAsync(
                    enabled.Value.Connection with { Environment = attempt.Environment }, attempt.TransactionId!, budget.Token), fetchedAt);
            }
            catch (SistecreditoGatewayException)
            {
                // Sin respuesta: se cancela igual y la revisión automática vigila si el cliente aprueba.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break; // se acabó el tiempo: los demás se cancelan sin consultar
            }
        }

        var now = clock.UtcNow;
        var reviewed = new HashSet<SistecreditoAttempt>();

        // Se decide sobre lo que hay (y otra vez sobre lo recargado si otro proceso tocó un cobro): una respuesta solo se aplica si nadie
        // consultó después de pedirla; aprobado → revisión; sigue pendiente → cancelado.
        void Decide()
        {
            reviewed.Clear();
            foreach (var attempt in pending.Where(a => a.CompletedSaleId is null))
            {
                if (attempt.Status == SistecreditoAttemptStatus.Pending && answers.TryGetValue(attempt.Id, out var answer)
                    && (attempt.LastCheckedAt is null || attempt.LastCheckedAt <= answer.FetchedAt))
                {
                    attempt.Apply(answer.Transaction.Status, answer.Transaction.RedirectUrl, answer.Transaction.Installments, answer.Transaction.Description, now);
                }

                if (attempt.Status == SistecreditoAttemptStatus.Approved)
                {
                    attempt.RequireReview("Aprobado después de completar la venta con otro medio de pago: reverse el pago en el portal de Sistecrédito.", now);
                    reviewed.Add(attempt);
                }
                else if (attempt.Status == SistecreditoAttemptStatus.Pending)
                {
                    attempt.CancelLocally("La venta se completó con otro medio de pago.", now);
                }
            }
        }

        Decide();
        if ((await transactions.SaveAsync(Decide, CancellationToken.None)).IsFailure)
        {
            return; // la revisión automática los sigue mirando
        }

        foreach (var attempt in reviewed)
        {
            await SistecreditoAudit.ReviewRequiredAsync(audit, attempt, CancellationToken.None);
        }

        await transactions.SaveAsync(cancellationToken: CancellationToken.None);
    }

    public async Task<IReadOnlyList<SistecreditoPendingUse>> ListApprovedUnusedAsync(Guid saleId, CancellationToken cancellationToken = default) =>
        (await store.ListForSaleAsync(saleId, cancellationToken))
            .Where(a => a.IsUsable) // aprobados o en revisión, mientras el supervisor no autorice «No usar este crédito»
            .Select(a => new SistecreditoPendingUse(a.TransactionId!, a.Amount))
            .ToList();

    /// <summary>
    /// Antes de abrir la transacción de la venta, vuelve a preguntar a Sistecrédito por los cobros usables de la venta (⚙️ máximo 5 s en
    /// total; sin respuesta se usa el último estado conocido). Si alguno ya no está aprobado queda "Requiere revisión" y se GUARDA (en una
    /// transacción corta propia): no se pierde aunque después la venta falle.
    /// </summary>
    public async Task RecheckForSaleAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        var usable = (await store.ListForSaleAsync(saleId, cancellationToken)).Where(a => a.IsUsable && a.TransactionId is not null).ToList();
        if (usable.Count == 0 || await connections.EnabledAsync(cancellationToken) is not { IsSuccess: true } enabled)
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.UseRecheckTimeoutSeconds, 1, 5)));
        foreach (var attempt in usable)
        {
            var before = attempt.Status;
            try
            {
                var transaction = await gateway.GetTransactionAsync(
                    enabled.Value.Connection with { Environment = attempt.Environment }, attempt.TransactionId!, budget.Token);
                attempt.Apply(transaction.Status, transaction.RedirectUrl, transaction.Installments, transaction.Description, clock.UtcNow);
            }
            catch (SistecreditoGatewayException ex) when (ex.ErrorCode == SistecreditoProtocol.NotFoundErrorCode)
            {
                attempt.NotFoundAtProvider(clock.UtcNow);
            }
            catch (SistecreditoGatewayException)
            {
                // Sin internet u otro error: se usa el último estado conocido (la revisión automática lo sigue mirando).
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break; // se acabó el tiempo: los demás se usan con su último estado conocido
            }

            if (attempt.Status != before && attempt.Status == SistecreditoAttemptStatus.ReviewRequired)
            {
                await SistecreditoAudit.ReviewRequiredAsync(audit, attempt, CancellationToken.None);
            }
        }

        // Mejor esfuerzo: si otro proceso tocó un cobro, se recarga y la venta decide con lo que hay en la base de datos.
        await transactions.SaveAsync(cancellationToken: CancellationToken.None);
    }

    public async Task SaleVoidedAsync(Guid saleId, string? saleNumber, IReadOnlyList<string> transactionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionIds);
        foreach (var transactionId in transactionIds.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.Ordinal))
        {
            if (await store.FindByTransactionAsync(transactionId, cancellationToken) is not { } attempt || attempt.CompletedSaleId != saleId
                || !attempt.SaleVoided(clock.UtcNow))
            {
                continue;
            }

            await audit.WriteAsync(
                new AuditEntry("cash", "SISTECREDITO_REVIEW_REQUIRED", nameof(SistecreditoAttempt), attempt.Id, $"Sistecrédito {attempt.TransactionId}",
                    $"Venta {saleNumber} anulada: estaba pagada con Sistecrédito ({attempt.Amount:N0}, {attempt.DocumentType} {attempt.MaskedDocument}). "
                    + "Reverse el pago en el portal de Sistecrédito.",
                    Severity: AuditSeverity.Warning),
                cancellationToken);
        }
    }

    public Task<int> CountPendingReviewsAsync(CancellationToken cancellationToken = default) => store.CountPendingReviewsAsync(cancellationToken);
}

/// <summary>
/// Revisión automática (D7B-05), cada ⚙️ 60 s: pregunta a Sistecrédito por los cobros pendientes, por los cancelados o vencidos en la caja
/// (el cliente pudo aprobar después) y por los aprobados sin usar, también en revisión (por si se reversaron); marca "Requiere revisión"
/// los aprobados que llevan ⚙️ 15 minutos sin venta completada. Nada queda atascado: primero va el que lleva más tiempo sin consultarse,
/// cada cobro se consulta con el ambiente en que se creó y se guarda en su propia transacción corta (un conflicto con un cobro no hace
/// perder los demás). Un pendiente de más de ⚙️ 24 h vence solo si Sistecrédito respondió en esa vuelta que sigue sin aprobar; uno que
/// Sistecrédito ya no encuentra (708) vence o, si estaba aprobado, se revisa. Lo que cambia queda en la bitácora.
/// </summary>
public sealed class SistecreditoReconciler(
    ISistecreditoStore store, ISistecreditoCredentialProtector protector, ISistecreditoGateway gateway, IInstallationContext installation,
    SistecreditoOptions options, IAuditWriter audit, IShortTransactions transactions, IClock clock)
{
    /// <summary>Un pendiente sin número de transacción (el equipo se cayó mientras se creaba) vence a los 10 minutos.</summary>
    private static readonly TimeSpan NotStartedExpiry = TimeSpan.FromMinutes(10);

    /// <summary>Un pendiente que Sistecrédito no encuentra (708) vence solo después de 10 minutos (recién creado puede tardar en aparecer).</summary>
    private static readonly TimeSpan NotFoundGrace = TimeSpan.FromMinutes(10);

    public async Task<(int Checked, int Changed)> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return (0, 0);
        }

        var settings = await store.GetSettingsAsync(companyId, cancellationToken);
        var credentials = settings is { HasCredentials: true } ? protector.Unprotect(settings.Credentials!) : null;
        var attempts = await store.ListToReconcileAsync(now, 50, cancellationToken);
        var unreachable = new HashSet<SistecreditoEnvironment>();
        var expiry = TimeSpan.FromHours(Math.Max(1, options.PendingExpiryHours));
        var changed = 0;

        // Hora de cada consulta: la de la vuelta más lo que ha pasado desde que empezó (no la misma hora para todo el lote).
        var startedAt = clock.UtcNow;
        DateTimeOffset At() => now + (clock.UtcNow - startedAt);
        foreach (var attempt in attempts)
        {
            var before = attempt.Status;
            var at = At();
            if (attempt.TransactionId is null)
            {
                if (attempt.Status == SistecreditoAttemptStatus.Pending && at - attempt.StartedAt >= NotStartedExpiry)
                {
                    attempt.Expire("El cobro no se alcanzó a crear en Sistecrédito: se da por vencido.", at);
                }
            }
            else if (credentials is not null && !unreachable.Contains(attempt.Environment))
            {
                var answered = false;

                // Cada cobro con el ambiente en que se creó (si después se cambió el ambiente, sus cobros se siguen consultando bien).
                try
                {
                    var transaction = await gateway.GetTransactionAsync(
                        new SistecreditoConnection(credentials, attempt.Environment), attempt.TransactionId, cancellationToken);
                    at = At();
                    attempt.Apply(transaction.Status, transaction.RedirectUrl, transaction.Installments, transaction.Description, at);
                    answered = !string.IsNullOrWhiteSpace(transaction.Status);
                }
                catch (SistecreditoGatewayException ex) when (ex.Failure == SistecreditoFailure.Network)
                {
                    unreachable.Add(attempt.Environment); // sin internet: se reintenta en la próxima vuelta
                }
                catch (SistecreditoGatewayException ex) when (ex.ErrorCode == SistecreditoProtocol.NotFoundErrorCode)
                {
                    if (attempt.ApprovedAt is not null || at - attempt.StartedAt >= NotFoundGrace)
                    {
                        attempt.NotFoundAtProvider(at);
                    }
                }
                catch (SistecreditoGatewayException)
                {
                    // Otro error de Sistecrédito con este cobro (p. ej. llaves que no sirven en su ambiente): se vuelve a mirar después.
                }

                // Pendiente demasiado viejo: vence solo si Sistecrédito acaba de responder que sigue sin aprobar (sin respuesta, se sigue
                // mirando: el cliente pudo aprobar mientras no había internet). Si aprueba después, queda "Requiere revisión".
                if (answered && attempt.Status == SistecreditoAttemptStatus.Pending && at - attempt.StartedAt >= expiry)
                {
                    attempt.Expire($"El cliente no terminó el cobro en {expiry.TotalHours:N0} horas: se da por vencido.", at);
                }
            }

            if (attempt.Status == SistecreditoAttemptStatus.Approved && attempt.CompletedSaleId is null && attempt.ApprovedAt is { } approvedAt
                && at - approvedAt >= TimeSpan.FromMinutes(Math.Max(1, options.ReviewAfterMinutes)))
            {
                attempt.RequireReview(
                    "Sistecrédito aprobó el cobro pero la venta no se completó en la caja: complete la venta o reverse el pago en el portal de Sistecrédito.", at);
            }

            attempt.Checked(at); // turno rotativo: el próximo en consultarse es el que lleva más tiempo sin mirar

            // Cada cobro en su propia transacción corta. Si no se pudo guardar (otro proceso lo cambió o una restricción), lo pendiente ya se
            // descartó y se recargó: se anota el fallo con espera creciente para que no ocupe siempre el primer turno.
            if ((await transactions.SaveAsync(cancellationToken: cancellationToken)).IsFailure)
            {
                attempt.CheckFailed(at);
                await transactions.SaveAsync(cancellationToken: cancellationToken);
                continue;
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

            await transactions.SaveAsync(cancellationToken: cancellationToken);
        }

        return (attempts.Count, changed);
    }
}

internal static class CashAppErrors
{
    public static readonly Error SetupRequired = Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
}
