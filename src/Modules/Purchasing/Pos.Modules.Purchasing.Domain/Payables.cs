using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Domain;

public enum PayableStatus
{
    /// <summary>Con saldo pendiente.</summary>
    Open,

    /// <summary>Saldo cero o a favor de la empresa.</summary>
    Settled,

    Voided,
}

public enum PayableEntryType
{
    Charge,
    Payment,
    PaymentVoid,
    Return,
    Refund,
    Replacement,
    Void,
}

/// <summary>Asiento del libro de una cuenta por pagar (solo inserción). <c>Amount</c> &gt; 0 aumenta la deuda.</summary>
public sealed class PayableEntry : Entity<Guid>
{
    private PayableEntry(Guid id, PayableEntryType entryType, decimal amount, decimal balanceAfter, string sourceType, Guid sourceId)
        : base(id)
    {
        EntryType = entryType;
        Amount = amount;
        BalanceAfter = balanceAfter;
        SourceType = sourceType;
        SourceId = sourceId;
    }

    public PayableEntryType EntryType { get; private set; }

    public decimal Amount { get; private set; }

    public decimal BalanceAfter { get; private set; }

    public string SourceType { get; private set; }

    public Guid SourceId { get; private set; }

    public string? SourceNumber { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid UserId { get; private set; }

    internal static PayableEntry Create(
        Guid id, PayableEntryType type, decimal amount, decimal balanceAfter, string sourceType, Guid sourceId, string? sourceNumber, Guid userId, DateTimeOffset now) =>
        new(id, type, amount, balanceAfter, sourceType, sourceId) { SourceNumber = sourceNumber, UserId = userId, OccurredAt = now };

    /// <summary>Signo obligatorio de cada tipo de asiento (igual que el CHECK de la BD).</summary>
    public static bool IsIncrease(PayableEntryType type) =>
        type is PayableEntryType.Charge or PayableEntryType.PaymentVoid or PayableEntryType.Refund or PayableEntryType.Replacement;
}

/// <summary>
/// Cuenta por pagar como LIBRO (D5-09): nace con el cargo de la compra y su saldo cambia solo por asientos (pagos,
/// devoluciones, reintegros, anulaciones). Nunca se edita el saldo.
/// </summary>
[Audited("purchasing")]
public sealed class AccountPayable : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<PayableEntry> _entries = [];

    private AccountPayable(Guid id, Guid companyId, Guid branchId, Guid supplierId, Guid purchaseId, string documentNumber)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        SupplierId = supplierId;
        PurchaseId = purchaseId;
        DocumentNumber = documentNumber;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid PurchaseId { get; private set; }

    public string DocumentNumber { get; private set; }

    public DateOnly IssueDate { get; private set; }

    public DateOnly DueDate { get; private set; }

    public decimal OriginalAmount { get; private set; }

    public decimal Balance { get; private set; }

    public PayableStatus Status { get; private set; } = PayableStatus.Open;

    public IReadOnlyList<PayableEntry> Entries => _entries;

    /// <summary>Neto pagado (pagos menos anulaciones de pagos).</summary>
    public decimal PaidAmount => -_entries.Where(e => e.EntryType is PayableEntryType.Payment or PayableEntryType.PaymentVoid).Sum(e => e.Amount);

    public string AuditLabel => $"Cuenta por pagar {DocumentNumber}";

    public static AccountPayable Open(
        Guid id, Purchase purchase, Guid userId, DateTimeOffset now, Guid entryId)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        var account = new AccountPayable(id, purchase.CompanyId, purchase.BranchId, purchase.SupplierId, purchase.Id, purchase.SupplierInvoiceNumber)
        {
            IssueDate = purchase.InvoiceDate,
            DueDate = purchase.DueDate,
            OriginalAmount = purchase.PayableTotal,
        };
        if (purchase.PayableTotal > 0m)
        {
            account.Record(entryId, PayableEntryType.Charge, purchase.PayableTotal, "PURCHASE", purchase.Id, purchase.Number, userId, now);
        }
        else
        {
            account.Status = PayableStatus.Settled;
        }

        return account;
    }

    /// <summary>Aplica un pago (no puede superar el saldo).</summary>
    public Result ApplyPayment(Guid entryId, decimal amount, Guid paymentId, string paymentNumber, Guid userId, DateTimeOffset now)
    {
        if (Status != PayableStatus.Open)
        {
            return PurchasingErrors.PayableNotOpen;
        }

        if (amount <= 0m || amount > Balance)
        {
            return PurchasingErrors.Overpayment;
        }

        Record(entryId, PayableEntryType.Payment, -amount, "PAYABLE_PAYMENT", paymentId, paymentNumber, userId, now);
        return Result.Success();
    }

    /// <summary>
    /// Asiento de devolución, reintegro, reposición o anulación de un pago. El signo lo da el tipo; una cuenta anulada no
    /// admite asientos.
    /// </summary>
    public Result Adjust(Guid entryId, PayableEntryType type, decimal amount, string sourceType, Guid sourceId, string? sourceNumber, Guid userId, DateTimeOffset now)
    {
        if (type is PayableEntryType.Charge or PayableEntryType.Payment or PayableEntryType.Void)
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Use Open, ApplyPayment o Void.");
        }

        if (Status == PayableStatus.Voided)
        {
            return PurchasingErrors.InvalidStatus;
        }

        if (amount > 0m)
        {
            Record(entryId, type, PayableEntry.IsIncrease(type) ? amount : -amount, sourceType, sourceId, sourceNumber, userId, now);
        }

        return Result.Success();
    }

    /// <summary>Anulación de la compra: solo sin pagos (RN-PUR-05); el saldo restante sale con un asiento VOID.</summary>
    public Result Void(Guid entryId, Guid purchaseId, string purchaseNumber, Guid userId, DateTimeOffset now)
    {
        if (Status == PayableStatus.Voided)
        {
            return PurchasingErrors.InvalidStatus;
        }

        if (PaidAmount != 0m)
        {
            return PurchasingErrors.PurchaseHasPayments;
        }

        if (Balance > 0m)
        {
            Record(entryId, PayableEntryType.Void, -Balance, "PURCHASE_VOID", purchaseId, purchaseNumber, userId, now);
        }

        Status = PayableStatus.Voided;
        return Result.Success();
    }

    private void Record(Guid entryId, PayableEntryType type, decimal signedAmount, string sourceType, Guid sourceId, string? sourceNumber, Guid userId, DateTimeOffset now)
    {
        Balance += signedAmount;
        _entries.Add(PayableEntry.Create(entryId, type, signedAmount, Balance, sourceType, sourceId, sourceNumber, userId, now));
        Status = Balance > 0m ? PayableStatus.Open : PayableStatus.Settled;
    }
}

public enum PaymentStatus
{
    Posted,
    Voided,
}

public sealed record AllocationInput(Guid AccountId, decimal Amount);

public sealed class PaymentAllocation : Entity<Guid>
{
    private PaymentAllocation(Guid id, Guid accountId, decimal amount)
        : base(id)
    {
        AccountId = accountId;
        Amount = amount;
    }

    public Guid AccountId { get; private set; }

    public decimal Amount { get; private set; }

    internal static PaymentAllocation Create(Guid id, AllocationInput input) => new(id, input.AccountId, input.Amount);
}

/// <summary>
/// Pago a proveedor (transferencia, cheque, efectivo fuera de caja; desde la caja en la Fase 6): puede cubrir varias
/// cuentas del mismo proveedor. Se anula con asientos inversos, nunca se borra.
/// </summary>
[Audited("purchasing")]
public sealed class PayablePayment : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<PaymentAllocation> _allocations = [];

    private PayablePayment(Guid id, Guid companyId, Guid branchId, Guid supplierId, string number)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        SupplierId = supplierId;
        Number = number;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string Number { get; private set; }

    public DateOnly PaymentDate { get; private set; }

    public Guid PaymentMethodId { get; private set; }

    public string? Reference { get; private set; }

    public decimal Amount { get; private set; }

    public string? Notes { get; private set; }

    public PaymentStatus Status { get; private set; } = PaymentStatus.Posted;

    public Guid? CashSessionId { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public Guid? VoidedBy { get; private set; }

    public string? VoidReason { get; private set; }

    public IReadOnlyList<PaymentAllocation> Allocations => _allocations;

    public string AuditLabel => $"Pago a proveedor {Number}";

    public static Result<PayablePayment> Create(
        Guid id, Guid companyId, Guid branchId, Guid supplierId, string number, DateOnly paymentDate, Guid paymentMethodId, string? reference,
        string? notes, IReadOnlyList<AllocationInput> allocations, Func<Guid> newId, Guid? cashSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(newId);
        if (allocations.Count == 0 || allocations.Any(a => a.Amount <= 0m || decimal.Round(a.Amount, 2) != a.Amount)
            || allocations.Select(a => a.AccountId).Distinct().Count() != allocations.Count
            || reference?.Trim().Length > 60 || notes?.Trim().Length > 500)
        {
            return PurchasingErrors.InvalidPayment;
        }

        var payment = new PayablePayment(id, companyId, branchId, supplierId, number)
        {
            PaymentDate = paymentDate,
            PaymentMethodId = paymentMethodId,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Amount = allocations.Sum(a => a.Amount),
            CashSessionId = cashSessionId,
        };
        payment._allocations.AddRange(allocations.Select(a => PaymentAllocation.Create(newId(), a)));
        return payment;
    }

    public Result Void(string reason, Guid userId, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Posted)
        {
            return PurchasingErrors.InvalidStatus;
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 300)
        {
            return PurchasingErrors.VoidReasonRequired;
        }

        Status = PaymentStatus.Voided;
        VoidedAt = now;
        VoidedBy = userId;
        VoidReason = trimmed;
        return Result.Success();
    }
}

/// <summary>Cartera por edades: días vencidos → rango (corriente, 1–30, 31–60, 61–90, más de 90).</summary>
public static class Aging
{
    public const string Current = "CURRENT";
    public const string Days1To30 = "1_30";
    public const string Days31To60 = "31_60";
    public const string Days61To90 = "61_90";
    public const string Over90 = "OVER_90";

    public static string Bucket(DateOnly dueDate, DateOnly asOf)
    {
        var overdue = asOf.DayNumber - dueDate.DayNumber;
        return overdue switch
        {
            <= 0 => Current,
            <= 30 => Days1To30,
            <= 60 => Days31To60,
            <= 90 => Days61To90,
            _ => Over90,
        };
    }
}
