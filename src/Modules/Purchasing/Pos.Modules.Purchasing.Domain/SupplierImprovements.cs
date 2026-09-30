using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Domain;

// ─────────────────────────────── Agenda del proveedor (Fase 8, D8-14) ───────────────────────────────

public enum ScheduleKind
{
    /// <summary>Día en que visita el vendedor.</summary>
    Visit,

    /// <summary>Día en que se le hace el pedido.</summary>
    Order,

    /// <summary>Día en que entrega la mercancía.</summary>
    Delivery,
}

/// <summary>Entrada de la agenda. <c>DayOfWeek</c> ISO: 1 = lunes … 7 = domingo. Sin sucursal = todas.</summary>
public sealed record ScheduleEntryInput(int DayOfWeek, ScheduleKind Kind, Guid? BranchId, string? Notes);

/// <summary>Día de visita, pedido o entrega de un proveedor (por sucursal o para todas). Base del sugerido de pedido (fases 9/10).</summary>
[Audited("purchasing")]
public sealed class SupplierSchedule : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const int MaxEntries = 60;

    private static readonly string[] DayNames = ["lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo"];

    private SupplierSchedule(Guid id, Guid companyId, Guid supplierId)
        : base(id)
    {
        CompanyId = companyId;
        SupplierId = supplierId;
    }

    public Guid CompanyId { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid? BranchId { get; private set; }

    public short DayOfWeek { get; private set; }

    public ScheduleKind Kind { get; private set; }

    public string? Notes { get; private set; }

    public string AuditLabel => $"Agenda de proveedor: {Kind.ToString().ToUpperInvariant()} {DayName(DayOfWeek)}";

    public static string DayName(int isoDay) => isoDay is >= 1 and <= 7 ? DayNames[isoDay - 1] : isoDay.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Valida la agenda completa: días 1–7, notas ≤ 200, sin repetir (tipo, día, sucursal), a lo sumo <see cref="MaxEntries"/>.</summary>
    public static Result Validate(IReadOnlyList<ScheduleEntryInput> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaxEntries || entries.Any(e => e is null || e.DayOfWeek is < 1 or > 7 || e.Notes?.Trim().Length > 200))
        {
            return PurchasingErrors.InvalidSchedule;
        }

        return entries.Select(e => (e.Kind, e.DayOfWeek, e.BranchId)).Distinct().Count() == entries.Count
            ? Result.Success()
            : PurchasingErrors.InvalidSchedule;
    }

    public static SupplierSchedule Create(Guid id, Guid companyId, Guid supplierId, ScheduleEntryInput entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var schedule = new SupplierSchedule(id, companyId, supplierId)
        {
            BranchId = entry.BranchId,
            DayOfWeek = (short)entry.DayOfWeek,
            Kind = entry.Kind,
        };
        schedule.SetNotes(entry.Notes);
        return schedule;
    }

    public bool Matches(ScheduleEntryInput entry) =>
        entry is not null && entry.Kind == Kind && entry.DayOfWeek == DayOfWeek && entry.BranchId == BranchId;

    public void SetNotes(string? notes) => Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

    /// <summary>Próxima fecha (desde <paramref name="from"/>, inclusive) que cae en el día de la agenda.</summary>
    public DateOnly NextOn(DateOnly from)
    {
        var today = from.DayOfWeek == System.DayOfWeek.Sunday ? 7 : (int)from.DayOfWeek;
        return from.AddDays((DayOfWeek - today + 7) % 7);
    }
}

// ─────────────────────────────── Retenciones sugeridas (RN-PUR-10) ───────────────────────────────

/// <summary>
/// Retención que se sugiere al crear una compra del proveedor (tipo y tarifa en %). Solo pre-llena el borrador: el
/// usuario ajusta la base y la confirma al contabilizar (D5-11 sigue vigente).
/// </summary>
[Audited("purchasing")]
public sealed class SupplierWithholdingDefault : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private SupplierWithholdingDefault(Guid id, Guid companyId, Guid supplierId, WithholdingKind kind)
        : base(id)
    {
        CompanyId = companyId;
        SupplierId = supplierId;
        Kind = kind;
    }

    public Guid CompanyId { get; private set; }

    public Guid SupplierId { get; private set; }

    public WithholdingKind Kind { get; private set; }

    public decimal Rate { get; private set; }

    public string? Concept { get; private set; }

    public string AuditLabel => $"Retención sugerida {Kind.ToString().ToUpperInvariant()} {Rate:0.####} %";

    public static Result<SupplierWithholdingDefault> Create(Guid id, Guid companyId, Guid supplierId, WithholdingKind kind, decimal rate, string? concept)
    {
        var item = new SupplierWithholdingDefault(id, companyId, supplierId, kind);
        var result = item.Update(rate, concept);
        return result.IsSuccess ? item : result.Error;
    }

    public Result Update(decimal rate, string? concept)
    {
        if (rate is <= 0m or > 100m || !Guard.HasAtMostDecimals(rate, 4) || concept?.Trim().Length > 100)
        {
            return PurchasingErrors.InvalidWithholdingDefault;
        }

        Rate = rate;
        Concept = string.IsNullOrWhiteSpace(concept) ? null : concept.Trim();
        return Result.Success();
    }
}

/// <summary>
/// Bases con que se sugieren las retenciones: retefuente y reteICA sobre el valor antes de impuestos (subtotal −
/// descuentos); reteIVA sobre el IVA de la compra. Los cargos (fletes) no entran: el usuario ajusta la base si aplica.
/// </summary>
public static class WithholdingSuggestion
{
    public static (decimal TaxableBase, decimal VatBase) BasesOf(CostingResult costing)
    {
        ArgumentNullException.ThrowIfNull(costing);
        return (costing.Totals.Subtotal - costing.Totals.DiscountTotal, costing.Lines.SelectMany(l => l.Taxes).Where(t => t.IsVat).Sum(t => t.Amount));
    }

    /// <summary>Una retención por tipo con base × tarifa redondeada a centavos; las que darían 0 no se sugieren.</summary>
    public static IReadOnlyList<WithholdingInput> Suggest(IEnumerable<SupplierWithholdingDefault> defaults, decimal taxableBase, decimal vatBase)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return
        [
            .. defaults.OrderBy(d => d.Kind).Select(d =>
                {
                    var @base = d.Kind == WithholdingKind.Reteiva ? vatBase : taxableBase;
                    return new WithholdingInput(d.Kind, @base, d.Rate, RoundingPolicy.Colombia.RoundMoney(@base * d.Rate / 100m));
                })
                .Where(w => w.Base > 0m && w.Amount > 0m),
        ];
    }
}

// ─────────────────────────────── Cuentas bancarias (RN-PUR-09) ───────────────────────────────

public enum BankAccountType
{
    Savings,
    Checking,
}

public enum BankAccountStatus
{
    /// <summary>Nueva o modificada: el pago al proveedor muestra una advertencia hasta verificarla.</summary>
    PendingVerification,

    /// <summary>Confirmada por otro usuario con el permiso (p. ej. por teléfono con el contacto de cartera).</summary>
    Verified,

    Inactive,
}

/// <summary>Datos sensibles de la cuenta: cambiar cualquiera la deja por verificar.</summary>
public sealed record BankAccountData(
    string BankCode, BankAccountType AccountType, string AccountNumber, string HolderName, string HolderIdentificationType, string HolderIdentificationNumber);

/// <summary>Qué cambió en una modificación (para la auditoría).</summary>
public sealed record BankAccountChange(bool Details, bool Status, bool Primary)
{
    public bool Any => Details || Status || Primary;
}

/// <summary>
/// Cuenta bancaria del proveedor. Toda cuenta nueva, modificada o reactivada queda <see cref="BankAccountStatus.PendingVerification"/>
/// y solo la verifica un usuario distinto del que hizo el último cambio (<see cref="ChangedBy"/>). La auditoría es crítica y
/// la escribe el caso de uso (con el número enmascarado).
/// </summary>
public sealed class SupplierBankAccount : AggregateRoot<Guid>, ICompanyOwned, ISyncVersioned, IHasAuditLabel
{
    private SupplierBankAccount(Guid id, Guid companyId, Guid supplierId)
        : base(id)
    {
        CompanyId = companyId;
        SupplierId = supplierId;
    }

    public Guid CompanyId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string BankCode { get; private set; } = string.Empty;

    public BankAccountType AccountType { get; private set; }

    public string AccountNumber { get; private set; } = string.Empty;

    public string HolderName { get; private set; } = string.Empty;

    public string HolderIdentificationType { get; private set; } = string.Empty;

    public string HolderIdentificationNumber { get; private set; } = string.Empty;

    public BankAccountStatus Status { get; private set; } = BankAccountStatus.PendingVerification;

    public bool IsPrimary { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public Guid ChangedBy { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    public Guid? VerifiedBy { get; private set; }

    public bool IsActive => Status != BankAccountStatus.Inactive;

    public string MaskedNumber => Mask(AccountNumber);

    public string AuditLabel => $"Cuenta bancaria {BankCode} {MaskedNumber}";

    /// <summary>Solo dígitos (se quitan espacios, guiones y puntos).</summary>
    public static string NormalizeNumber(string? number) =>
        new([.. (number ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '.')]);

    /// <summary>Solo se muestran los últimos 4 dígitos.</summary>
    public static string Mask(string? number) => number is { Length: > 4 } ? "****" + number[^4..] : "****";

    public static Result<SupplierBankAccount> Create(
        Guid id, Guid companyId, Guid supplierId, BankAccountData data, bool isPrimary, Guid userId, DateTimeOffset now)
    {
        var account = new SupplierBankAccount(id, companyId, supplierId);
        var normalized = Normalize(data);
        if (normalized.IsFailure)
        {
            return normalized.Error;
        }

        account.Apply(normalized.Value);
        account.IsPrimary = isPrimary;
        account.MarkChanged(userId, now);
        return account;
    }

    /// <summary>
    /// Modifica la cuenta. Si cambia cualquier dato sensible o se reactiva → queda por verificar (cambiado por
    /// <paramref name="userId"/>). Inactivarla le quita la marca de principal.
    /// </summary>
    public Result<BankAccountChange> Update(BankAccountData data, bool isActive, bool isPrimary, Guid userId, DateTimeOffset now)
    {
        var normalized = Normalize(data);
        if (normalized.IsFailure)
        {
            return normalized.Error;
        }

        var value = normalized.Value;
        var details = value.BankCode != BankCode || value.AccountType != AccountType || value.AccountNumber != AccountNumber
                      || value.HolderName != HolderName || value.HolderIdentificationType != HolderIdentificationType
                      || value.HolderIdentificationNumber != HolderIdentificationNumber;
        var reactivated = !IsActive && isActive;
        var statusBefore = Status;
        var primaryBefore = IsPrimary;
        Apply(value);
        if (!isActive)
        {
            Status = BankAccountStatus.Inactive;
            VerifiedAt = null;
            VerifiedBy = null;
            if (details || statusBefore != BankAccountStatus.Inactive)
            {
                ChangedBy = userId;
                ChangedAt = now;
            }
        }
        else if (details || reactivated)
        {
            MarkChanged(userId, now);
        }

        IsPrimary = isActive && isPrimary;
        return new BankAccountChange(details, Status != statusBefore, IsPrimary != primaryBefore);
    }

    /// <summary>RN-PUR-09: la verifica otro usuario (no quien la registró o cambió por última vez).</summary>
    public Result Verify(Guid verifierId, DateTimeOffset now)
    {
        if (Status != BankAccountStatus.PendingVerification)
        {
            return PurchasingErrors.BankAccountNotPending;
        }

        if (verifierId == ChangedBy)
        {
            return PurchasingErrors.BankAccountSameUser;
        }

        Status = BankAccountStatus.Verified;
        VerifiedBy = verifierId;
        VerifiedAt = now;
        return Result.Success();
    }

    /// <summary>Otra cuenta pasa a ser la principal.</summary>
    public void ClearPrimary() => IsPrimary = false;

    private static Result<BankAccountData> Normalize(BankAccountData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var bank = (data.BankCode ?? string.Empty).Trim().ToUpperInvariant();
        var number = NormalizeNumber(data.AccountNumber);
        var holder = (data.HolderName ?? string.Empty).Trim();
        var idType = (data.HolderIdentificationType ?? string.Empty).Trim().ToUpperInvariant();
        var idNumber = (data.HolderIdentificationNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (bank.Length is < 2 or > 10 || number.Length is < 5 or > 20 || !number.All(char.IsAsciiDigit) || holder.Length is 0 or > 150
            || idType.Length is 0 or > 10 || idNumber.Length is 0 or > 20 || !Enum.IsDefined(data.AccountType))
        {
            return PurchasingErrors.InvalidBankAccount;
        }

        return new BankAccountData(bank, data.AccountType, number, holder, idType, idNumber);
    }

    private void Apply(BankAccountData data)
    {
        BankCode = data.BankCode;
        AccountType = data.AccountType;
        AccountNumber = data.AccountNumber;
        HolderName = data.HolderName;
        HolderIdentificationType = data.HolderIdentificationType;
        HolderIdentificationNumber = data.HolderIdentificationNumber;
    }

    private void MarkChanged(Guid userId, DateTimeOffset now)
    {
        Status = BankAccountStatus.PendingVerification;
        VerifiedAt = null;
        VerifiedBy = null;
        ChangedBy = userId;
        ChangedAt = now;
    }
}
