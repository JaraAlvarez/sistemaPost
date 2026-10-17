using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Billing.Domain;

/// <summary>
/// Rango de numeración autorizado por la DIAN, sincronizado desde el proveedor y asignado a una sucursal (y opcionalmente a una caja)
/// (D11B-05). El proveedor asigna el número; aquí se lleva el consecutivo conocido para alertar al 90 % de uso y a 30 días del vencimiento.
/// </summary>
public sealed class FiscalNumberingRange : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private FiscalNumberingRange(Guid id, Guid companyId, string provider)
        : base(id)
    {
        CompanyId = companyId;
        Provider = provider;
    }

    public Guid CompanyId { get; private set; }

    public string Provider { get; private set; }

    public string ProviderRangeId { get; private set; } = string.Empty;

    public FiscalDocumentType DocumentType { get; private set; }

    public string Prefix { get; private set; } = string.Empty;

    public long RangeFrom { get; private set; }

    public long RangeTo { get; private set; }

    public long CurrentNumber { get; private set; }

    public string? ResolutionNumber { get; private set; }

    public DateOnly? ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public bool IsActive { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid? PosTerminalId { get; private set; }

    public DateTimeOffset SyncedAt { get; private set; }

    /// <summary>Ambiente del proveedor del que vino el rango (N2): un rango de pruebas nunca numera documentos de producción.</summary>
    public FiscalEnvironment Environment { get; private set; } = FiscalEnvironment.Sandbox;

    public string AuditLabel => $"Rango {Prefix} {RangeFrom}-{RangeTo} ({DocumentType})";

    /// <summary>Porcentaje usado del rango (0–100).</summary>
    public decimal UsagePercent => Math.Round((CurrentNumber - RangeFrom + 1) * 100m / (RangeTo - RangeFrom + 1), 2);

    public long Remaining => RangeTo - CurrentNumber;

    public static FiscalNumberingRange Create(
        Guid id, Guid companyId, string provider, FiscalProviderRange data, DateTimeOffset now, FiscalEnvironment environment = FiscalEnvironment.Sandbox)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var range = new FiscalNumberingRange(id, companyId, provider) { ProviderRangeId = data.ProviderRangeId, Environment = environment };
        range.Refresh(data, now);
        return range;
    }

    /// <summary>Actualiza con lo que informa el proveedor (el consecutivo nunca retrocede).</summary>
    public void Refresh(FiscalProviderRange data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.From <= 0 || data.To < data.From || data.Prefix is null || data.Prefix.Trim().Length > 10)
        {
            throw new DomainException($"Rango inválido del proveedor: {data.Prefix} {data.From}-{data.To}.");
        }

        DocumentType = data.DocumentType;
        Prefix = data.Prefix.Trim();
        RangeFrom = data.From;
        RangeTo = data.To;
        CurrentNumber = Math.Clamp(Math.Max(CurrentNumber, data.Current), data.From - 1, data.To);
        ResolutionNumber = data.ResolutionNumber;
        ValidFrom = data.ValidFrom;
        ValidTo = data.ValidTo;
        IsActive = data.IsActive;
        SyncedAt = now;
    }

    /// <summary>Asigna el rango a una sucursal (y opcionalmente a una caja) o lo desasigna (<paramref name="branchId"/> nulo).</summary>
    public Result Assign(Guid? branchId, Guid? posTerminalId)
    {
        if (posTerminalId is not null && branchId is null)
        {
            return BillingErrors.InvalidAssignment;
        }

        BranchId = branchId;
        PosTerminalId = posTerminalId;
        return Result.Success();
    }

    /// <summary>Sirve para emitir en la fecha: activo, con números disponibles y vigente.</summary>
    public bool IsUsableOn(DateOnly date) =>
        IsActive && CurrentNumber < RangeTo && (ValidFrom is null || ValidFrom <= date) && (ValidTo is null || ValidTo >= date);

    /// <summary>Días que faltan para el vencimiento (nulo si no vence).</summary>
    public int? DaysToExpire(DateOnly today) => ValidTo is { } to ? to.DayNumber - today.DayNumber : null;

    /// <summary>Requiere alerta: uso ≥ ⚙️ 90 % o vence en ⚙️ 30 días o menos (o ya no sirve).</summary>
    public bool NeedsAlert(DateOnly today, decimal usagePercent, int days) =>
        IsActive && (UsagePercent >= usagePercent || DaysToExpire(today) <= days || !IsUsableOn(today));

    /// <summary>
    /// El proveedor informó el consecutivo usado al aceptar un documento. Devuelve si con él el rango cruzó el umbral de alerta.
    /// </summary>
    public bool Advance(long consecutive, decimal alertPercent)
    {
        var before = UsagePercent;
        CurrentNumber = Math.Clamp(Math.Max(CurrentNumber, consecutive), RangeFrom - 1, RangeTo);
        return before < alertPercent && UsagePercent >= alertPercent;
    }

    /// <summary>
    /// Elige el rango para emitir: primero el asignado a la caja, luego el de toda la sucursal; activo, vigente y con números.
    /// </summary>
    /// <remarks>Solo rangos del <paramref name="environment"/> del documento (N2).</remarks>
    public static FiscalNumberingRange? Select(
        IEnumerable<FiscalNumberingRange> ranges, Guid branchId, Guid? posTerminalId, FiscalDocumentType type, DateOnly date,
        FiscalEnvironment environment = FiscalEnvironment.Sandbox)
    {
        var candidates = (ranges ?? [])
            .Where(r => r.Environment == environment && r.BranchId == branchId && r.DocumentType == type && r.IsUsableOn(date)).ToList();
        return candidates.FirstOrDefault(r => posTerminalId is not null && r.PosTerminalId == posTerminalId)
            ?? candidates.Where(r => r.PosTerminalId is null).OrderBy(r => r.ValidTo ?? DateOnly.MaxValue).FirstOrDefault();
    }
}

/// <summary>
/// Configuración del proveedor por empresa (D11B-01, D11B-08): modo de emisión, ambiente y credenciales cifradas con DPAPI. El
/// dominio solo ve bytes: cifrar y descifrar lo hace la infraestructura, y la API nunca devuelve las credenciales.
/// </summary>
public sealed class BillingProviderSettings : ICompanyOwned
{
    private BillingProviderSettings(Guid companyId, string provider)
    {
        CompanyId = companyId;
        Provider = provider;
    }

    public Guid CompanyId { get; private set; }

    public string Provider { get; private set; }

    public FiscalEnvironment Environment { get; private set; } = FiscalEnvironment.Sandbox;

    public BillingMode Mode { get; private set; } = BillingMode.Off;

#pragma warning disable CA1819 // Bytes cifrados que EF guarda tal cual.
    public byte[]? Credentials { get; private set; }
#pragma warning restore CA1819

    public DateTimeOffset? CredentialsUpdatedAt { get; private set; }

    public DateTimeOffset? LastSyncAt { get; private set; }

    public string? LastSyncError { get; private set; }

    /// <summary>Usuario enmascarado (p. ej. <c>ti***@correo.co</c>) para reconocer las credenciales sin mostrarlas.</summary>
    public string? CredentialHint { get; private set; }

    /// <summary>Última verificación contra el proveedor de las credenciales y el ambiente VIGENTES (se borra al cambiar cualquiera).</summary>
    public DateTimeOffset? LastVerifiedAt { get; private set; }

    public bool? LastVerificationOk { get; private set; }

    public string? LastVerificationMessage { get; private set; }

    /// <summary>
    /// Primera verificación correcta (PA1): los documentos electrónicos emitidos ANTES de ese momento (atraso de cuando no había proveedor)
    /// no se envían solos; la administración decide "Enviar ahora" (<see cref="BacklogReleased"/>) o "No enviar" (NOT_SENT).
    /// </summary>
    public DateTimeOffset? BacklogCutoff { get; private set; }

    public bool BacklogReleased { get; private set; }

    /// <summary>
    /// Ambiente con que se fijó el corte: verificar en OTRO ambiente lo mueve a ese momento (PB1). Rotar llaves en el mismo ambiente no lo
    /// mueve (N1): los documentos en cola de ese ambiente siguen saliendo.
    /// </summary>
    public FiscalEnvironment? BacklogEnvironment { get; private set; }

    /// <summary>Ambiente de la última sincronización correcta de rangos (N2): sin rangos del ambiente vigente no se envía.</summary>
    public FiscalEnvironment? RangesEnvironment { get; private set; }

    /// <summary>Hay que (re)sincronizar los rangos: nunca se hizo o fue en otro ambiente.</summary>
    public bool RangesStale => RangesEnvironment != Environment;

    /// <summary>La migración V2026.10.060 la apagó por estar encendida sin verificación: el tablero lo avisa hasta que se conecte.</summary>
    public DateTimeOffset? AutoDisabledAt { get; private set; }

    public bool HasCredentials => Credentials is { Length: > 0 };

    /// <summary>Las credenciales y el ambiente vigentes ya se verificaron con éxito contra el proveedor.</summary>
    public bool IsVerified => HasCredentials && LastVerificationOk == true;

    /// <summary>La cola envía SOLO con el modo encendido y la conexión verificada (PA1). Con OFF no sale nada, ni lo que ya estaba en cola.</summary>
    public bool CanSend => Mode != BillingMode.Off && IsVerified && !RangesStale;

    /// <summary>Los documentos emitidos antes de este momento esperan la decisión de la administración (nulo: no hay atraso retenido).</summary>
    public DateTimeOffset? HeldBefore => BacklogReleased ? null : BacklogCutoff;

    public static BillingProviderSettings Create(Guid companyId, string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return new BillingProviderSettings(companyId, provider);
    }

    /// <summary>
    /// Cambia el modo y el ambiente. Encender (modo distinto de OFF) exige credenciales y, si estaba apagada o cambia el ambiente, una
    /// verificación correcta contra el proveedor ("guardar desactivado → verificar → activar"). Cambiar el ambiente borra la verificación.
    /// Pasar de ON_REQUEST a EVERY_SALE (o al revés) en el mismo ambiente no exige verificar de nuevo.
    /// </summary>
    public Result Configure(BillingMode mode, FiscalEnvironment environment, string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (mode != BillingMode.Off && !HasCredentials)
        {
            return BillingErrors.CredentialsRequired;
        }

        var environmentChanged = environment != Environment;
        if (mode != BillingMode.Off && (Mode == BillingMode.Off || environmentChanged) && (environmentChanged || !IsVerified))
        {
            return BillingErrors.VerificationRequired;
        }

        if (environmentChanged)
        {
            ClearVerification();
        }

        Mode = mode;
        Environment = environment;
        Provider = provider;
        if (mode != BillingMode.Off)
        {
            AutoDisabledAt = null;
        }

        return Result.Success();
    }

    /// <summary>
    /// Guarda las credenciales ya cifradas ("guardar desactivado"): la facturación electrónica queda APAGADA y sin verificación hasta que
    /// se verifiquen contra el proveedor y se vuelva a encender. <paramref name="hint"/>: usuario enmascarado.
    /// </summary>
    public void SetCredentials(byte[]? encrypted, DateTimeOffset now, string? hint = null)
    {
        Credentials = encrypted is { Length: > 0 } ? encrypted : null;
        CredentialsUpdatedAt = Credentials is null ? null : now;
        CredentialHint = Credentials is null ? null : hint is { Length: > 80 } ? hint[..80] : hint;
        Mode = BillingMode.Off;
        ClearVerification();
    }

    /// <summary>
    /// Resultado de verificar las credenciales y el ambiente vigentes contra el proveedor (el mensaje se recorta). Un fallo TRANSITORIO (sin
    /// red, tiempo agotado, proveedor caído) no quita una verificación correcta (PA9): solo un rechazo de las credenciales la quita. La
    /// primera verificación correcta fija el corte del atraso (<see cref="BacklogCutoff"/>).
    /// </summary>
    /// <returns><c>true</c> si cambió el estado guardado.</returns>
    public bool RecordVerification(DateTimeOffset now, bool ok, string? message, bool transient = false)
    {
        if (!ok && transient && LastVerificationOk == true)
        {
            return false;
        }

        LastVerifiedAt = now;
        LastVerificationOk = ok;
        LastVerificationMessage = message is { Length: > 500 } ? message[..500] : message;
        if (ok && (BacklogCutoff is null || BacklogEnvironment != Environment))
        {
            // Primera verificación correcta, o en otro ambiente: lo emitido antes espera la decisión de la administración.
            BacklogCutoff = now;
            BacklogReleased = false;
            BacklogEnvironment = Environment;
        }

        return true;
    }

    /// <summary>
    /// "Cambiar llaves" (PA6): las llaves candidatas YA se verificaron contra el proveedor en el ambiente vigente; se guardan sin apagar
    /// la facturación y quedan verificadas.
    /// </summary>
    public void ReplaceVerifiedCredentials(byte[] encrypted, DateTimeOffset now, string? hint, string? message)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        if (encrypted.Length == 0)
        {
            throw new DomainException("Las credenciales nuevas están vacías.");
        }

        Credentials = encrypted;
        CredentialsUpdatedAt = now;
        CredentialHint = hint is { Length: > 80 } ? hint[..80] : hint;
        RecordVerification(now, ok: true, message);
    }

    /// <summary>"Enviar ahora" el atraso retenido (PA1): desde ya la cola lo envía como cualquier otro documento.</summary>
    public void ReleaseBacklog() => BacklogReleased = true;

    /// <summary>Usuario enmascarado: las dos primeras letras y el dominio del correo (<c>ti***@correo.co</c>).</summary>
    public static string MaskUsername(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        var value = username.Trim();
        var at = value.IndexOf('@', StringComparison.Ordinal);
        var local = at >= 0 ? value[..at] : value;
        var visible = local.Length <= 2 ? local[..Math.Min(1, local.Length)] : local[..2];
        return visible + "***" + (at >= 0 ? value[at..] : string.Empty);
    }

    private void ClearVerification()
    {
        LastVerifiedAt = null;
        LastVerificationOk = null;
        LastVerificationMessage = null;
    }

    /// <summary>Resultado de la última sincronización de rangos (el error se recorta).</summary>
    /// <param name="now">Momento de la sincronización.</param>
    /// <param name="error">Error del proveedor, o <c>null</c> si salió bien.</param>
    /// <param name="environment">Ambiente sincronizado (solo cuenta si salió bien).</param>
    public void RecordSync(DateTimeOffset now, string? error, FiscalEnvironment? environment = null)
    {
        if (error is null && environment is { } synced)
        {
            RangesEnvironment = synced;
        }

        LastSyncAt = now;
        LastSyncError = error is { Length: > 500 } ? error[..500] : error;
    }

    /// <summary>¿El documento de esta venta es electrónico según el modo?</summary>
    public static bool IsElectronicSale(BillingMode mode, bool invoiceRequested) =>
        mode == BillingMode.EverySale || (mode == BillingMode.OnRequest && invoiceRequested);
}

/// <summary>Espera creciente entre reintentos (D11B-09): 1, 2, 4, 8… minutos con tope.</summary>
public static class FiscalRetryPolicy
{
    public static TimeSpan Delay(int attempts, int maxMinutes = 60)
    {
        var minutes = Math.Min(maxMinutes, Math.Pow(2, Math.Clamp(attempts, 0, 20)));
        return TimeSpan.FromMinutes(Math.Max(1, minutes));
    }
}
