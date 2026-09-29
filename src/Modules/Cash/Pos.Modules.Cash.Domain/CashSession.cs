using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Cash.Domain;

public enum CashSessionStatus
{
    Open,

    /// <summary>Cierre iniciado: no admite más ventas (RN-CSH-03); se cuenta y se confirma.</summary>
    Closing,

    Closed,
}

public enum CashMovementType
{
    OpeningFloat,
    Sale,
    SaleVoid,
    CustomerRefund,
    CashIn,
    CashOutWithdrawal,
    Expense,
    SupplierPayment,
    Correction,
    NoSaleDrawerOpen,
}

public enum CashCountKind
{
    Opening,
    Partial,
    Closing,
}

public enum DenominationKind
{
    Bill,
    Coin,
}

/// <summary>Billete o moneda de la empresa para el arqueo por denominación (D6-10).</summary>
[Audited("cash")]
public sealed class Denomination : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private Denomination(Guid id, Guid companyId, string currencyCode, decimal value)
        : base(id)
    {
        CompanyId = companyId;
        CurrencyCode = currencyCode;
        Value = value;
    }

    public Guid CompanyId { get; private set; }

    public string CurrencyCode { get; private set; }

    public decimal Value { get; private set; }

    public DenominationKind Kind { get; private set; }

    public int SortOrder { get; private set; }

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public string AuditLabel => $"Denominación {CurrencyCode} {Value:N0}";

    public static Result<Denomination> Create(Guid id, Guid companyId, string currencyCode, decimal value, DenominationKind kind, int sortOrder)
    {
        var currency = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        if (currency.Length != 3 || value <= 0m || decimal.Round(value, 2) != value)
        {
            return CashErrors.InvalidDenomination;
        }

        return new Denomination(id, companyId, currency, value) { Kind = kind, SortOrder = sortOrder };
    }

    public void SetActive(bool active) => Status = active ? MasterStatus.Active : MasterStatus.Inactive;
}

/// <summary>Dirección fija de cada tipo de movimiento de caja (igual que el CHECK de la BD); la corrección la elige quien la registra.</summary>
public static class CashMovementRules
{
    public static int Direction(CashMovementType type) => type switch
    {
        CashMovementType.OpeningFloat or CashMovementType.Sale or CashMovementType.CashIn => 1,
        CashMovementType.SaleVoid or CashMovementType.CustomerRefund or CashMovementType.CashOutWithdrawal or CashMovementType.Expense
            or CashMovementType.SupplierPayment => -1,
        CashMovementType.NoSaleDrawerOpen => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(type), "La dirección de una corrección la indica quien la registra."),
    };

    /// <summary>Tipos que exigen un motivo escrito.</summary>
    public static bool RequiresReason(CashMovementType type) =>
        type is CashMovementType.CashIn or CashMovementType.CashOutWithdrawal or CashMovementType.NoSaleDrawerOpen or CashMovementType.Correction;
}

/// <summary>Datos de un movimiento de caja nuevo.</summary>
public sealed record CashMovementData(
    CashMovementType Type,
    Guid PaymentMethodId,
    decimal Amount,
    string? Reason,
    Guid? AuthorizedBy = null,
    string? SourceType = null,
    Guid? SourceId = null,
    string? SourceNumber = null,
    int? CorrectionDirection = null);

/// <summary>
/// Movimiento de caja (D6-01): SOLO INSERCIÓN. Entra (+1), sale (−1) o no mueve dinero (0, apertura sin venta). Solo se
/// registra en una jornada abierta (RN-CSH-02). Una corrección es otro movimiento, nunca una edición.
/// </summary>
public sealed class CashMovement : AggregateRoot<Guid>, ICompanyOwned
{
    private CashMovement(Guid id, Guid companyId, Guid sessionId, int lineNo)
        : base(id)
    {
        CompanyId = companyId;
        SessionId = sessionId;
        LineNo = lineNo;
    }

    public Guid CompanyId { get; private set; }

    public Guid SessionId { get; private set; }

    public int LineNo { get; private set; }

    public CashMovementType MovementType { get; private set; }

    public Guid PaymentMethodId { get; private set; }

    public short Direction { get; private set; }

    public decimal Amount { get; private set; }

    public string? SourceType { get; private set; }

    public Guid? SourceId { get; private set; }

    public string? SourceNumber { get; private set; }

    public string? Reason { get; private set; }

    public Guid? AuthorizedBy { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Valor con signo: lo que suma (o resta) al esperado de su medio de pago.</summary>
    public decimal SignedAmount => Direction * Amount;

    public static Result<CashMovement> Create(Guid id, CashSession session, int lineNo, CashMovementData data, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(data);
        if (session.Status != CashSessionStatus.Open)
        {
            return CashErrors.SessionNotOpen;
        }

        var reason = string.IsNullOrWhiteSpace(data.Reason) ? null : data.Reason.Trim();
        if ((CashMovementRules.RequiresReason(data.Type) && reason is null) || reason?.Length > 300)
        {
            return CashErrors.ReasonRequired;
        }

        int direction;
        if (data.Type == CashMovementType.Correction)
        {
            if (data.CorrectionDirection is not (1 or -1))
            {
                return CashErrors.InvalidMovement;
            }

            direction = data.CorrectionDirection.Value;
        }
        else
        {
            direction = CashMovementRules.Direction(data.Type);
        }

        var validAmount = data.Type switch
        {
            CashMovementType.NoSaleDrawerOpen => data.Amount == 0m,
            CashMovementType.OpeningFloat => data.Amount >= 0m,
            _ => data.Amount > 0m,
        };
        if (!validAmount || decimal.Round(data.Amount, 2) != data.Amount)
        {
            return CashErrors.InvalidMovement;
        }

        return new CashMovement(id, session.CompanyId, session.Id, lineNo)
        {
            MovementType = data.Type,
            PaymentMethodId = data.PaymentMethodId,
            Direction = (short)direction,
            Amount = data.Amount,
            Reason = reason,
            AuthorizedBy = data.AuthorizedBy,
            SourceType = data.SourceType,
            SourceId = data.SourceId,
            SourceNumber = data.SourceNumber,
            UserId = userId,
            OccurredAt = now,
        };
    }
}

/// <summary>Resumen de los movimientos de un medio de pago en la jornada.</summary>
public sealed record MethodMovements(Guid PaymentMethodId, decimal Expected, int Transactions);

/// <summary>Línea de un conteo: por denominación (efectivo) o por total del medio de pago.</summary>
public sealed record CountLineInput(Guid PaymentMethodId, Guid? DenominationId, decimal? DenominationValue, int? Quantity, decimal? Amount);

/// <summary>Esperado, contado y diferencia de un medio al cerrar.</summary>
public sealed record MethodTotal(Guid PaymentMethodId, decimal Expected, decimal Counted, int Transactions)
{
    public decimal Difference => Counted - Expected;
}

/// <summary>Cálculos de la caja: esperado desde los movimientos (D6-02, RN-CSH-04) y contado desde el arqueo.</summary>
public static class CashCalculator
{
    /// <summary>Esperado por medio = Σ dirección × valor de sus movimientos; transacciones = movimientos que mueven dinero.</summary>
    public static IReadOnlyList<MethodMovements> Expected(IEnumerable<(Guid PaymentMethodId, int Direction, decimal Amount)> movements) =>
        [.. movements.GroupBy(m => m.PaymentMethodId)
            .Select(g => new MethodMovements(g.Key, g.Sum(m => m.Direction * m.Amount), g.Count(m => m.Direction != 0)))];

    /// <summary>Contado por medio: cantidad × denominación en el efectivo, o el total digitado en los demás medios.</summary>
    public static Result<IReadOnlyDictionary<Guid, decimal>> Counted(IReadOnlyList<CountLineInput> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Select(l => (l.PaymentMethodId, l.DenominationId)).Distinct().Count() != lines.Count)
        {
            return CashErrors.InvalidCount;
        }

        var totals = new Dictionary<Guid, decimal>();
        foreach (var line in lines)
        {
            decimal amount;
            if (line.DenominationId is not null)
            {
                if (line.Quantity is not >= 0 || line.DenominationValue is not > 0m)
                {
                    return CashErrors.InvalidCount;
                }

                amount = line.Quantity.Value * line.DenominationValue.Value;
            }
            else
            {
                if (line.Amount is not >= 0m || decimal.Round(line.Amount.Value, 2) != line.Amount.Value)
                {
                    return CashErrors.InvalidCount;
                }

                amount = line.Amount.Value;
            }

            totals[line.PaymentMethodId] = totals.GetValueOrDefault(line.PaymentMethodId) + amount;
        }

        return totals;
    }

    /// <summary>Totales del cierre: todos los medios con movimientos o con conteo.</summary>
    public static IReadOnlyList<MethodTotal> Totals(IReadOnlyList<MethodMovements> expected, IReadOnlyDictionary<Guid, decimal> counted) =>
        [.. expected.Select(e => e.PaymentMethodId).Union(counted.Keys)
            .Select(id => new MethodTotal(
                id,
                expected.FirstOrDefault(e => e.PaymentMethodId == id)?.Expected ?? 0m,
                counted.GetValueOrDefault(id),
                expected.FirstOrDefault(e => e.PaymentMethodId == id)?.Transactions ?? 0))];
}

public sealed class CashCountLine : Entity<Guid>
{
    private CashCountLine(Guid id)
        : base(id)
    {
    }

    public Guid PaymentMethodId { get; private set; }

    public Guid? DenominationId { get; private set; }

    public int? Quantity { get; private set; }

    public decimal Amount { get; private set; }

    internal static CashCountLine Create(Guid id, CountLineInput input) => new(id)
    {
        PaymentMethodId = input.PaymentMethodId,
        DenominationId = input.DenominationId,
        Quantity = input.DenominationId is null ? null : input.Quantity,
        Amount = input.DenominationId is null ? input.Amount!.Value : input.Quantity!.Value * input.DenominationValue!.Value,
    };
}

/// <summary>Conteo de la caja (apertura, parcial o cierre) con sus líneas.</summary>
public sealed class CashCount : Entity<Guid>
{
    private readonly List<CashCountLine> _lines = [];

    private CashCount(Guid id)
        : base(id)
    {
    }

    public CashCountKind Kind { get; private set; }

    public decimal Total { get; private set; }

    public DateTimeOffset CountedAt { get; private set; }

    public Guid CountedBy { get; private set; }

    public IReadOnlyList<CashCountLine> Lines => _lines;

    internal static CashCount Create(Guid id, CashCountKind kind, IReadOnlyList<CountLineInput> lines, Guid countedBy, DateTimeOffset now, Func<Guid> newId)
    {
        var count = new CashCount(id) { Kind = kind, CountedAt = now, CountedBy = countedBy };
        count._lines.AddRange(lines.Select(l => CashCountLine.Create(newId(), l)));
        count.Total = count._lines.Sum(l => l.Amount);
        return count;
    }
}

/// <summary>Esperado, contado y diferencia de un medio de pago guardados al cerrar.</summary>
public sealed class CashSessionTotal : Entity<Guid>
{
    private CashSessionTotal(Guid id)
        : base(id)
    {
    }

    public Guid PaymentMethodId { get; private set; }

    public decimal Expected { get; private set; }

    public decimal Counted { get; private set; }

    public decimal Difference { get; private set; }

    public int Transactions { get; private set; }

    internal static CashSessionTotal Create(Guid id, MethodTotal total) => new(id)
    {
        PaymentMethodId = total.PaymentMethodId,
        Expected = total.Expected,
        Counted = total.Counted,
        Difference = total.Difference,
        Transactions = total.Transactions,
    };
}

/// <summary>Cierre de la jornada: conteo, esperado por medio, umbral de diferencia y el sello de la auditoría del Z.</summary>
public sealed record SessionClosing(
    IReadOnlyList<CountLineInput> CountLines,
    IReadOnlyList<MethodMovements> Expected,
    decimal DifferenceThreshold,
    string? DifferenceNote,
    Guid ClosedBy,
    bool BySupervisor,
    string? SupervisorReason,
    DateTimeOffset Now);

/// <summary>
/// Jornada de caja: <c>OPEN</c> → <c>CLOSING</c> (sin ventas nuevas) → <c>CLOSED</c> (definitivo, RN-CSH-08). Una por caja
/// y una por cajero (D6-03); fecha de negocio = la de apertura (D6-04); ligada a la caja, al cajero y al equipo (D6-09).
/// Lo esperado se calcula de los movimientos al cerrar; una diferencia mayor al umbral exige observación y queda para
/// revisión del supervisor (RN-CSH-05). El cierre por supervisor (cajero ausente) siempre queda para revisión.
/// </summary>
[Audited("cash")]
public sealed class CashSession : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<CashCount> _counts = [];

    private readonly List<CashSessionTotal> _totals = [];

    private CashSession(Guid id, Guid companyId, Guid branchId, Guid posTerminalId, Guid cashierId, string number)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        PosTerminalId = posTerminalId;
        CashierId = cashierId;
        Number = number;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid PosTerminalId { get; private set; }

    public Guid? DeviceId { get; private set; }

    public Guid CashierId { get; private set; }

    public string Number { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public decimal OpeningFloat { get; private set; }

    public CashSessionStatus Status { get; private set; } = CashSessionStatus.Open;

    public bool BlindCount { get; private set; }

    public DateTimeOffset? ClosingStartedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? ClosedBy { get; private set; }

    public bool ClosedBySupervisor { get; private set; }

    public string? CloseReason { get; private set; }

    public decimal? ExpectedTotal { get; private set; }

    public decimal? CountedTotal { get; private set; }

    public decimal? Difference { get; private set; }

    public string? DifferenceNote { get; private set; }

    public bool ReviewRequired { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public string? ReviewNote { get; private set; }

    public long? ZSealNo { get; private set; }

    public string? ZSealCode { get; private set; }

    public IReadOnlyList<CashCount> Counts => _counts;

    public IReadOnlyList<CashSessionTotal> Totals => _totals;

    public string AuditLabel => $"Jornada de caja {Number}";

    public static Result<CashSession> Open(
        Guid id, Guid companyId, Guid branchId, Guid posTerminalId, Guid? deviceId, Guid cashierId, string number, DateTimeOffset now, DateOnly businessDate,
        decimal openingFloat, bool blindCount)
    {
        if (openingFloat < 0m || decimal.Round(openingFloat, 2) != openingFloat)
        {
            return CashErrors.InvalidMovement;
        }

        return new CashSession(id, companyId, branchId, posTerminalId, cashierId, number)
        {
            DeviceId = deviceId,
            OpenedAt = now,
            BusinessDate = businessDate,
            OpeningFloat = openingFloat,
            BlindCount = blindCount,
        };
    }

    /// <summary>Conteo de apertura o parcial (el de cierre lo registra <see cref="Close"/>).</summary>
    public Result<CashCount> RegisterCount(CashCountKind kind, IReadOnlyList<CountLineInput> lines, Guid countedBy, DateTimeOffset now, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (kind == CashCountKind.Closing || Status == CashSessionStatus.Closed)
        {
            return CashErrors.InvalidStatus;
        }

        var counted = CashCalculator.Counted(lines);
        if (counted.IsFailure)
        {
            return counted.Error;
        }

        var count = CashCount.Create(newId(), kind, lines, countedBy, now, newId);
        _counts.Add(count);
        return count;
    }

    public Result StartClosing(DateTimeOffset now)
    {
        if (Status != CashSessionStatus.Open)
        {
            return CashErrors.InvalidStatus;
        }

        Status = CashSessionStatus.Closing;
        ClosingStartedAt = now;
        return Result.Success();
    }

    public Result CancelClosing()
    {
        if (Status != CashSessionStatus.Closing)
        {
            return CashErrors.InvalidStatus;
        }

        Status = CashSessionStatus.Open;
        return Result.Success();
    }

    /// <summary>
    /// Confirma el arqueo: guarda el conteo de cierre y los totales por medio (esperado de los movimientos, contado y
    /// diferencia). El cajero cierra desde <c>CLOSING</c>; el supervisor puede cerrar una jornada abierta o en cierre.
    /// </summary>
    public Result<IReadOnlyList<MethodTotal>> Close(SessionClosing closing, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(closing);
        ArgumentNullException.ThrowIfNull(newId);
        if (Status == CashSessionStatus.Closed || (!closing.BySupervisor && Status != CashSessionStatus.Closing))
        {
            return CashErrors.InvalidStatus;
        }

        var reason = string.IsNullOrWhiteSpace(closing.SupervisorReason) ? null : closing.SupervisorReason.Trim();
        if (closing.BySupervisor && reason is not { Length: >= 5 and <= 300 })
        {
            return CashErrors.ReasonRequired;
        }

        var counted = CashCalculator.Counted(closing.CountLines);
        if (counted.IsFailure)
        {
            return counted.Error;
        }

        var totals = CashCalculator.Totals(closing.Expected, counted.Value);
        var difference = totals.Sum(t => t.Difference);
        var note = string.IsNullOrWhiteSpace(closing.DifferenceNote) ? null : closing.DifferenceNote.Trim();
        var overThreshold = Math.Abs(difference) > closing.DifferenceThreshold;
        if (note?.Length > 500 || (overThreshold && !closing.BySupervisor && note is null))
        {
            return CashErrors.DifferenceNoteRequired;
        }

        _counts.Add(CashCount.Create(newId(), CashCountKind.Closing, closing.CountLines, closing.ClosedBy, closing.Now, newId));
        _totals.Clear();
        _totals.AddRange(totals.Select(t => CashSessionTotal.Create(newId(), t)));
        Status = CashSessionStatus.Closed;
        ClosingStartedAt ??= closing.Now;
        ClosedAt = closing.Now;
        ClosedBy = closing.ClosedBy;
        ClosedBySupervisor = closing.BySupervisor;
        CloseReason = reason;
        ExpectedTotal = totals.Sum(t => t.Expected);
        CountedTotal = totals.Sum(t => t.Counted);
        Difference = difference;
        DifferenceNote = note;
        ReviewRequired = overThreshold || closing.BySupervisor;
        return Result.Success(totals);
    }

    /// <summary>Sello de la auditoría que se imprime en el reporte Z (D6-08).</summary>
    public void SetSeal(long sealNo, string sealCode)
    {
        if (Status != CashSessionStatus.Closed)
        {
            throw new DomainException("El sello del reporte Z se asigna al cerrar.");
        }

        ZSealNo = sealNo;
        ZSealCode = sealCode;
    }

    /// <summary>Revisión del supervisor de un cierre con diferencia o cerrado por supervisor (no la hace el propio cajero).</summary>
    public Result Review(Guid reviewerId, string? note, DateTimeOffset now)
    {
        if (Status != CashSessionStatus.Closed || !ReviewRequired || ReviewedAt is not null)
        {
            return CashErrors.InvalidStatus;
        }

        if (reviewerId == CashierId)
        {
            return CashErrors.SelfReview;
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is not { Length: >= 5 and <= 500 })
        {
            return CashErrors.ReasonRequired;
        }

        ReviewedAt = now;
        ReviewedBy = reviewerId;
        ReviewNote = trimmed;
        return Result.Success();
    }
}
