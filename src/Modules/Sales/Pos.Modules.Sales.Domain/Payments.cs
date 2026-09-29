using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Domain;

/// <summary>
/// Pago entregado por el cliente. <c>GivesChange</c>: solo el efectivo. <c>Kind</c> es el tipo del medio (CASH, DEBIT_CARD…,
/// EXCHANGE_CREDIT). La tarjeta guarda franquicia y últimos 4 dígitos, nunca el número completo.
/// </summary>
public sealed record Tender(
    Guid PaymentMethodId,
    string MethodCode,
    string Kind,
    bool GivesChange,
    bool AffectsCashDrawer,
    decimal Amount,
    string? Reference = null,
    string? CardFranchise = null,
    string? CardLast4 = null);

/// <summary>Pago aplicado a la venta: <c>Applied</c> es lo que cubre del total; <c>Change</c>, lo que se devuelve.</summary>
public sealed record AllocatedPayment(Tender Tender, decimal Applied, decimal Change);

public sealed record PaymentAllocation(IReadOnlyList<AllocatedPayment> Payments, decimal RoundingAdjustment, decimal Total, decimal Paid, decimal Change);

/// <summary>
/// Reglas de pago (doc 08 §M, D7-08, D7-09): primero los medios sin cambio, que no pueden exceder el saldo (RN-SAL-09); el
/// efectivo cubre el resto y da el cambio. La parte en efectivo se redondea al múltiplo configurado (p. ej. $50) y la
/// diferencia queda como ajuste de redondeo. Σ aplicado = total a pagar.
/// </summary>
public static class PaymentAllocator
{
    public static Result<PaymentAllocation> Allocate(decimal total, IReadOnlyList<Tender> tenders, decimal cashIncrement)
    {
        ArgumentNullException.ThrowIfNull(tenders);
        if (tenders.Count == 0 || tenders.Any(t => t.Amount < 0m || decimal.Round(t.Amount, 2) != t.Amount))
        {
            return SalesErrors.InvalidPayment;
        }

        if (tenders.Count(t => t.GivesChange) > 1)
        {
            return SalesErrors.SingleCashTender;
        }

        var nonCash = tenders.Where(t => !t.GivesChange).ToList();
        if (nonCash.Any(t => t.Amount == 0m))
        {
            return SalesErrors.InvalidPayment;
        }

        var nonCashTotal = nonCash.Sum(t => t.Amount);
        if (nonCashTotal > total)
        {
            return SalesErrors.NonCashOverpayment;
        }

        var payments = nonCash.Select(t => new AllocatedPayment(t, t.Amount, 0m)).ToList();
        var remaining = total - nonCashTotal;
        var adjustment = 0m;
        if (tenders.FirstOrDefault(t => t.GivesChange) is { } cash)
        {
            var due = cashIncrement > 0m ? new RoundingPolicy(2, cashIncrement).RoundToCash(remaining) : remaining;
            adjustment = due - remaining;
            if (cash.Amount < due)
            {
                return Error.BusinessRule(SalesErrors.InsufficientPayment.Code, $"{SalesErrors.InsufficientPayment.Message} Faltan {due - cash.Amount:N2}.");
            }

            if (due == 0m && remaining == 0m)
            {
                return SalesErrors.CashNotNeeded;
            }

            payments.Add(new AllocatedPayment(cash, due, cash.Amount - due));
        }
        else if (remaining > 0m)
        {
            return Error.BusinessRule(SalesErrors.InsufficientPayment.Code, $"{SalesErrors.InsufficientPayment.Message} Faltan {remaining:N2}.");
        }

        var payable = total + adjustment;
        return new PaymentAllocation(payments, adjustment, payable, payments.Sum(p => p.Tender.Amount), payments.Sum(p => p.Change));
    }
}
