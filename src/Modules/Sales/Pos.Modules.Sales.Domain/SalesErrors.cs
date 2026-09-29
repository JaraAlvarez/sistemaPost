using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Domain;

/// <summary>Errores de negocio de las ventas con código estable (la UI de caja depende de ellos).</summary>
public static class SalesErrors
{
    public static readonly Error SaleNotFound = Error.NotFound("SALES.SALE_NOT_FOUND", "La venta no existe.");

    public static readonly Error LineNotFound = Error.NotFound("SALES.LINE_NOT_FOUND", "La línea no existe o ya fue eliminada.");

    public static readonly Error InvalidStatus = Error.BusinessRule("SALES.INVALID_STATUS", "La venta no está en un estado que permita esta acción.");

    public static readonly Error NotOpen = Error.BusinessRule("SALES.NOT_OPEN", "La venta no está abierta: recupérela o inicie otra.");

    public static readonly Error TerminalRequired = Error.Forbidden(
        "SALES.TERMINAL_REQUIRED", "Se vende desde una caja (entrada con código y PIN en la caja emparejada o en el equipo Caja Única).");

    public static readonly Error NoOpenCashSession = Error.BusinessRule(
        "SALES.NO_OPEN_CASH_SESSION", "La caja no tiene una jornada abierta (o está en cierre): abra la jornada para vender (RN-SAL-01).");

    public static readonly Error OpenSaleExists = Error.Conflict(
        "SALES.OPEN_SALE_EXISTS", "La caja ya tiene una venta en curso: termínela, suspéndala o cancélela (GET /sales/current).");

    public static readonly Error OtherTerminal = Error.Forbidden("SALES.OTHER_TERMINAL", "La venta pertenece a otra caja.");

    public static readonly Error InvalidQuantity = Error.Validation(
        "SALES.INVALID_QUANTITY", "Cantidad inválida: mayor que cero y entera si el producto no se vende por fracciones (RN-SAL-03).");

    public static readonly Error ProductNotSellable = Error.BusinessRule("SALES.PRODUCT_NOT_SELLABLE", "El producto no se puede vender (RN-SAL-02).");

    public static readonly Error InsufficientStock = Error.BusinessRule(
        "SALES.INSUFFICIENT_STOCK", "No hay existencias suficientes en la bodega de la caja (RN-SAL-17). Un supervisor puede hacer un ajuste rápido.");

    public static readonly Error ExpiredLotRequiresAuthorization = Error.BusinessRule(
        "SALES.EXPIRED_LOT_REQUIRES_AUTHORIZATION",
        "El producto tiene existencias de un lote vencido: venderlo requiere autorización (RN-SAL-18). Retire los vencidos de la góndola.");

    public static readonly Error PriceRequired = Error.BusinessRule("SALES.PRICE_REQUIRED", "Hay productos sin precio: fije el precio (precio abierto) antes de cobrar.");

    public static readonly Error OpenPriceNotAllowed = Error.BusinessRule("SALES.OPEN_PRICE_NOT_ALLOWED", "El producto no admite precio abierto (RN-SAL-05).");

    public static readonly Error InvalidPrice = Error.Validation("SALES.INVALID_PRICE", "Precio inválido: mayor que cero con hasta 2 decimales.");

    public static readonly Error InvalidDiscount = Error.Validation(
        "SALES.INVALID_DISCOUNT", "Descuento inválido: porcentaje de 0,01 a 100 o valor mayor que cero y no mayor al neto; con motivo (RN-SAL-04).");

    public static readonly Error ReasonRequired = Error.Validation("SALES.REASON_REQUIRED", "Indique el motivo (de 5 a 300 caracteres).");

    public static readonly Error EmptySale = Error.BusinessRule("SALES.EMPTY_SALE", "La venta no tiene productos.");

    public static readonly Error HoldLimitReached = Error.BusinessRule("SALES.HOLD_LIMIT_REACHED", "La caja alcanzó el máximo de ventas suspendidas (RN-SAL-07).");

    public static readonly Error InvalidPayment = Error.Validation(
        "SALES.INVALID_PAYMENT", "Pago inválido: medios activos, valores mayores que cero con hasta 2 decimales.");

    public static readonly Error SingleCashTender = Error.Validation("SALES.SINGLE_CASH_TENDER", "Registre el efectivo en un solo pago.");

    public static readonly Error NonCashOverpayment = Error.BusinessRule(
        "SALES.NON_CASH_OVERPAYMENT", "Un medio que no da cambio no puede exceder el saldo de la venta (RN-SAL-09).");

    public static readonly Error InsufficientPayment = Error.BusinessRule("SALES.INSUFFICIENT_PAYMENT", "Los pagos no cubren el total de la venta.");

    public static readonly Error CashNotNeeded = Error.BusinessRule("SALES.CASH_NOT_NEEDED", "La venta ya está cubierta: no registre efectivo.");

    public static readonly Error ReferenceRequired = Error.Validation(
        "SALES.REFERENCE_REQUIRED", "El medio de pago exige una referencia (número de aprobación o de la transacción) (RN-SAL-10).");

    public static readonly Error ExchangeCreditNotAllowed = Error.Validation(
        "SALES.EXCHANGE_CREDIT_NOT_ALLOWED", "El crédito por cambio se aplica solo en un cambio de mercancía.");

    public static readonly Error CompletionKeyMismatch = Error.Conflict(
        "SALES.ALREADY_COMPLETED", "La venta ya fue completada con otra clave de idempotencia.");

    public static readonly Error VoidNotAllowed = Error.BusinessRule(
        "SALES.VOID_NOT_ALLOWED", "Solo se anula una venta completada, sin cambios de mercancía y con su jornada de caja abierta (RN-SAL-12).");

    public static readonly Error CustomerNotFound = Error.NotFound("SALES.CUSTOMER_NOT_FOUND", "El cliente no existe o está inactivo.");

    public static readonly Error CustomerRequired = Error.BusinessRule(
        "SALES.CUSTOMER_REQUIRED", "El total supera el tope para Consumidor final: identifique al cliente (RN-SAL-13).");

    public static readonly Error ExchangeNotFound = Error.NotFound("SALES.EXCHANGE_NOT_FOUND", "El cambio de mercancía no existe.");

    public static readonly Error ExchangeNotAllowed = Error.BusinessRule(
        "SALES.EXCHANGE_NOT_ALLOWED", "La venta no admite cambios: debe estar completada, en esta sucursal y dentro del plazo (RN-RET-01).");

    public static readonly Error ExchangeQuantityExceeded = Error.BusinessRule(
        "SALES.EXCHANGE_QUANTITY_EXCEEDED", "La cantidad supera lo vendido menos lo ya cambiado (RN-RET-02).");

    public static readonly Error ExchangeBelowCredit = Error.BusinessRule(
        "SALES.EXCHANGE_BELOW_CREDIT", "El cliente debe llevar productos por un valor igual o mayor al crédito del cambio (D7-11): no se devuelve dinero.");

    public static readonly Error InvalidExchange = Error.Validation(
        "SALES.INVALID_EXCHANGE", "Cambio inválido: al menos una línea, cantidades mayores que cero, una sola vez cada línea y un destino válido.");
}
