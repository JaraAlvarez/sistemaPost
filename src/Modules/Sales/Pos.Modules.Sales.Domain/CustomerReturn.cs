using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Domain;

public enum ReturnKind
{
    /// <summary>Cambio de mercancía: el crédito paga una venta nueva de igual o mayor valor (D7-11).</summary>
    Exchange,

    /// <summary>Excepción de garantía (Ley 1480): reintegro en efectivo autorizado solo por el propietario.</summary>
    WarrantyRefund,
}

public enum ReturnStatus
{
    Draft,
    Completed,
    Cancelled,
}

/// <summary>Destino de la mercancía recibida (RN-RET-04).</summary>
public enum ReturnDestination
{
    /// <summary>Vuelve a la bodega de venta de la caja.</summary>
    ReturnToStock,

    /// <summary>Va a la bodega de averías.</summary>
    SendToDamaged,

    /// <summary>Se descarta: entra a averías y sale como daño en el mismo documento.</summary>
    Discard,
}

/// <summary>Línea recibida: cantidad en la unidad de venta, crédito al precio pagado y costo con que salió (D7-11).</summary>
public sealed class CustomerReturnLine : Entity<Guid>
{
    private CustomerReturnLine(Guid id)
        : base(id)
    {
    }

    public Guid SaleLineId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal UnitCost { get; private set; }

    public Guid? LotId { get; private set; }

    public decimal CreditAmount { get; private set; }

    public ReturnDestination Destination { get; private set; }

    internal static CustomerReturnLine Create(Guid id, SaleLine line, decimal quantity, decimal credit, ReturnDestination destination) => new(id)
    {
        SaleLineId = line.Id,
        ProductId = line.ProductId,
        Sku = line.Sku,
        Name = line.Name,
        Quantity = quantity,
        BaseQuantity = decimal.Round(quantity * line.Factor, 4, MidpointRounding.AwayFromZero),
        UnitCost = line.UnitCost ?? 0m,
        LotId = line.LotId,
        CreditAmount = credit,
        Destination = destination,
    };
}

public sealed record ReturnLineRequest(Guid SaleLineId, decimal Quantity, ReturnDestination Destination);

/// <summary>
/// Cambio de mercancía (reemplaza la devolución con reintegro, D7-11) o reintegro por garantía. El crédito es lo que el
/// cliente pagó por esas unidades (promoción y descuentos prorrateados). Un cambio nace en borrador junto con la venta nueva
/// que lo usa y se completa al cobrarla; cancelar la venta nueva lo cancela.
/// </summary>
public sealed class CustomerReturn : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<CustomerReturnLine> _lines = [];

    private CustomerReturn(Guid id, Guid companyId, Guid branchId, Guid posTerminalId, Guid originalSaleId)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        PosTerminalId = posTerminalId;
        OriginalSaleId = originalSaleId;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid PosTerminalId { get; private set; }

    public Guid? CashSessionId { get; private set; }

    public Guid OriginalSaleId { get; private set; }

    public string OriginalSaleNumber { get; private set; } = string.Empty;

    public ReturnKind Kind { get; private set; }

    public ReturnStatus Status { get; private set; } = ReturnStatus.Draft;

    public string? Number { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public decimal CreditTotal { get; private set; }

    /// <summary>Venta nueva que se paga con el crédito (solo en un cambio).</summary>
    public Guid? ReplacementSaleId { get; private set; }

    public Guid? RefundPaymentMethodId { get; private set; }

    public Guid ReceivedBy { get; private set; }

    public Guid? AuthorizedBy { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public IReadOnlyList<CustomerReturnLine> Lines => _lines;

    public string AuditLabel => $"{(Kind == ReturnKind.Exchange ? "Cambio" : "Reintegro por garantía")} {Number ?? Id.ToString()} de la venta {OriginalSaleNumber}";

    /// <summary>Crédito de una cantidad de la línea: proporcional a lo pagado; al completar la línea, el saldo exacto.</summary>
    public static decimal CreditFor(SaleLine line, decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rounding = RoundingPolicy.Colombia;
        if (line.ReturnedQuantity + quantity == line.Quantity)
        {
            return line.Total - rounding.RoundMoney(line.Total * line.ReturnedQuantity / line.Quantity);
        }

        return rounding.RoundMoney(line.Total * quantity / line.Quantity);
    }

    public static Result<CustomerReturn> Create(
        Guid id, ReturnKind kind, Sale sale, IReadOnlyList<ReturnLineRequest> lines, string reason, Guid posTerminalId, DateOnly businessDate, int maxDays,
        Guid userId, Guid? authorizedBy, DateTimeOffset now, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(newId);
        if (sale.Status != SaleStatus.Completed || sale.ReturnStatus == SaleReturnStatus.Full || businessDate.DayNumber - sale.BusinessDate.DayNumber > maxDays)
        {
            return SalesErrors.ExchangeNotAllowed;
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 300)
        {
            return SalesErrors.ReasonRequired;
        }

        if (lines.Count == 0 || lines.Select(l => l.SaleLineId).Distinct().Count() != lines.Count || lines.Any(l => l.Quantity <= 0m)
            || lines.Any(l => !Enum.IsDefined(l.Destination)))
        {
            return SalesErrors.InvalidExchange;
        }

        var result = new CustomerReturn(id, sale.CompanyId, sale.BranchId, posTerminalId, sale.Id)
        {
            Kind = kind,
            OriginalSaleNumber = sale.Number!,
            BusinessDate = businessDate,
            Reason = trimmed,
            ReceivedBy = userId,
            AuthorizedBy = authorizedBy,
            ReceivedAt = now,
        };
        foreach (var request in lines)
        {
            if (sale.Lines.FirstOrDefault(l => l.Id == request.SaleLineId && l.IsActive) is not { } line)
            {
                return SalesErrors.LineNotFound;
            }

            if (line.ReturnedQuantity + request.Quantity > line.Quantity || (!line.AllowsDecimalQuantity && decimal.Truncate(request.Quantity) != request.Quantity))
            {
                return SalesErrors.ExchangeQuantityExceeded;
            }

            result._lines.Add(CustomerReturnLine.Create(newId(), line, request.Quantity, CreditFor(line, request.Quantity), request.Destination));
        }

        result.CreditTotal = result._lines.Sum(l => l.CreditAmount);
        return result;
    }

    public void LinkReplacementSale(Guid saleId) => ReplacementSaleId = saleId;

    /// <summary>Completa el cambio al cobrar la venta nueva (o el reintegro de garantía, con la jornada que entrega el dinero).</summary>
    public Result Complete(string number, Guid cashSessionId, Guid? refundPaymentMethodId, DateTimeOffset now)
    {
        if (Status != ReturnStatus.Draft)
        {
            return SalesErrors.InvalidStatus;
        }

        Number = number;
        CashSessionId = cashSessionId;
        RefundPaymentMethodId = refundPaymentMethodId;
        CompletedAt = now;
        Status = ReturnStatus.Completed;
        return Result.Success();
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (Status != ReturnStatus.Draft)
        {
            return SalesErrors.InvalidStatus;
        }

        Status = ReturnStatus.Cancelled;
        CancelledAt = now;
        return Result.Success();
    }
}
