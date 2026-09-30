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

    public string AuditLabel => $"Rango {Prefix} {RangeFrom}-{RangeTo} ({DocumentType})";

    /// <summary>Porcentaje usado del rango (0–100).</summary>
    public decimal UsagePercent => Math.Round((CurrentNumber - RangeFrom + 1) * 100m / (RangeTo - RangeFrom + 1), 2);

    public long Remaining => RangeTo - CurrentNumber;

    public static FiscalNumberingRange Create(Guid id, Guid companyId, string provider, FiscalProviderRange data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var range = new FiscalNumberingRange(id, companyId, provider) { ProviderRangeId = data.ProviderRangeId };
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
    public static FiscalNumberingRange? Select(
        IEnumerable<FiscalNumberingRange> ranges, Guid branchId, Guid? posTerminalId, FiscalDocumentType type, DateOnly date)
    {
        var candidates = (ranges ?? []).Where(r => r.BranchId == branchId && r.DocumentType == type && r.IsUsableOn(date)).ToList();
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

    public bool HasCredentials => Credentials is { Length: > 0 };

    public static BillingProviderSettings Create(Guid companyId, string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return new BillingProviderSettings(companyId, provider);
    }

    /// <summary>Cambia el modo y el ambiente. Encender (modo distinto de OFF) exige credenciales.</summary>
    public Result Configure(BillingMode mode, FiscalEnvironment environment, string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (mode != BillingMode.Off && !HasCredentials)
        {
            return BillingErrors.CredentialsRequired;
        }

        Mode = mode;
        Environment = environment;
        Provider = provider;
        return Result.Success();
    }

    /// <summary>Guarda las credenciales ya cifradas; sin credenciales, la facturación se apaga.</summary>
    public void SetCredentials(byte[]? encrypted, DateTimeOffset now)
    {
        Credentials = encrypted is { Length: > 0 } ? encrypted : null;
        CredentialsUpdatedAt = Credentials is null ? null : now;
        if (Credentials is null)
        {
            Mode = BillingMode.Off;
        }
    }

    /// <summary>Resultado de la última sincronización de rangos (el error se recorta).</summary>
    public void RecordSync(DateTimeOffset now, string? error)
    {
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
