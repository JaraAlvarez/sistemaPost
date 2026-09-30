using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Domain;

/// <summary>Errores de negocio de compras con código estable.</summary>
public static class PurchasingErrors
{
    public static readonly Error SupplierNotFound = Error.NotFound("PURCHASING.SUPPLIER_NOT_FOUND", "El proveedor no existe.");

    public static readonly Error SupplierBlocked = Error.BusinessRule(
        "PURCHASING.SUPPLIER_BLOCKED", "El proveedor está bloqueado o inactivo: no admite órdenes ni compras nuevas.");

    public static readonly Error SupplierDuplicated = Error.Conflict("PURCHASING.SUPPLIER_DUPLICATED", "El tercero ya es proveedor (o el código ya existe).");

    public static readonly Error InvalidSupplier = Error.Validation(
        "PURCHASING.INVALID_SUPPLIER", "Proveedor inválido: código de 1 a 20 (mayúsculas, dígitos, _ o -), plazo 0–365 días y cupo ≥ 0.");

    public static readonly Error SupplierProductNotFound = Error.NotFound("PURCHASING.SUPPLIER_PRODUCT_NOT_FOUND", "El proveedor no tiene registrado ese producto.");

    public static readonly Error SupplierProductDuplicated = Error.Conflict(
        "PURCHASING.SUPPLIER_PRODUCT_DUPLICATED", "El producto (o el código del proveedor) ya está registrado para este proveedor.");

    public static readonly Error InvalidSupplierProduct = Error.Validation(
        "PURCHASING.INVALID_SUPPLIER_PRODUCT", "Código del proveedor de hasta 40 caracteres y días de entrega entre 0 y 365.");

    public static readonly Error OrderNotFound = Error.NotFound("PURCHASING.ORDER_NOT_FOUND", "La orden de compra no existe.");

    public static readonly Error PurchaseNotFound = Error.NotFound("PURCHASING.PURCHASE_NOT_FOUND", "La compra no existe.");

    public static readonly Error ReturnNotFound = Error.NotFound("PURCHASING.RETURN_NOT_FOUND", "La devolución no existe.");

    public static readonly Error PaymentNotFound = Error.NotFound("PURCHASING.PAYMENT_NOT_FOUND", "El pago no existe.");

    public static readonly Error PayableNotFound = Error.NotFound("PURCHASING.PAYABLE_NOT_FOUND", "La cuenta por pagar no existe.");

    public static readonly Error InvalidStatus = Error.BusinessRule("PURCHASING.INVALID_STATUS", "El documento no está en un estado que permita esta acción.");

    public static readonly Error NoLines = Error.Validation("PURCHASING.NO_LINES", "El documento debe tener al menos una línea.");

    public static readonly Error DuplicatedLine = Error.Validation("PURCHASING.DUPLICATED_LINE", "Un producto (con la misma presentación) aparece más de una vez.");

    public static readonly Error InvalidLine = Error.Validation(
        "PURCHASING.INVALID_LINE", "Línea inválida: cantidad > 0 (hasta 4 decimales), factor > 0, costo ≥ 0 y descuento entre 0 y el valor de la línea.");

    public static readonly Error ProductNotPurchasable = Error.BusinessRule(
        "PURCHASING.PRODUCT_NOT_PURCHASABLE", "Uno de los productos no existe, es un servicio, está descontinuado o la presentación no se compra.");

    public static readonly Error InvalidHeader = Error.Validation(
        "PURCHASING.INVALID_HEADER", "Factura inválida: número de 1 a 40 caracteres, vencimiento ≥ fecha de factura y recepción ≥ fecha de factura.");

    public static readonly Error InvoiceDuplicated = Error.Conflict(
        "PURCHASING.INVOICE_DUPLICATED", "Esa factura del proveedor ya está registrada (RN-PUR-02).");

    public static readonly Error InvoiceTotalRequired = Error.Validation(
        "PURCHASING.INVOICE_TOTAL_REQUIRED", "Digite el total de la factura del proveedor antes de contabilizar.");

    public static readonly Error TotalsMismatch = Error.BusinessRule(
        "PURCHASING.TOTALS_MISMATCH", "El total calculado no coincide con el total de la factura: revise cantidades, costos, descuentos e impuestos.");

    public static readonly Error ChargesMismatch = Error.Validation(
        "PURCHASING.CHARGES_MISMATCH", "Con prorrateo manual, la suma de los cargos de las líneas debe ser igual al total de cargos.");

    public static readonly Error InvalidWithholding = Error.Validation(
        "PURCHASING.INVALID_WITHHOLDING", "Retenciones inválidas: una por tipo (RETEFUENTE, RETEIVA, RETEICA), base ≥ 0, valor > 0, tarifa 0–100.");

    public static readonly Error WithholdingsExceedTotal = Error.BusinessRule(
        "PURCHASING.WITHHOLDINGS_EXCEED_TOTAL", "Las retenciones no pueden superar el total de la compra.");

    public static readonly Error PaymentMethodRequired = Error.Validation(
        "PURCHASING.PAYMENT_METHOD_REQUIRED", "Una compra de contado exige el medio de pago (y su referencia si el medio la pide).");

    public static readonly Error LotRequired = Error.Validation(
        "PURCHASING.LOT_REQUIRED", "Los productos con lote exigen número de lote (y vencimiento si lo controlan) al contabilizar (RN-INV-08/09).");

    public static readonly Error ReceiptExceedsOrder = Error.BusinessRule(
        "PURCHASING.RECEIPT_EXCEEDS_ORDER", "Lo recibido supera lo pedido más la tolerancia (RN-PUR-04).");

    public static readonly Error OrderLineMismatch = Error.Validation(
        "PURCHASING.ORDER_LINE_MISMATCH", "La línea no pertenece a la orden de compra o es de otro producto.");

    public static readonly Error OrderNotReceivable = Error.BusinessRule(
        "PURCHASING.ORDER_NOT_RECEIVABLE", "La orden debe estar aprobada o enviada (y no cerrada) para recibir contra ella.");

    public static readonly Error OrderSupplierMismatch = Error.Validation("PURCHASING.ORDER_SUPPLIER_MISMATCH", "La orden es de otro proveedor o de otra bodega.");

    public static readonly Error OrderHasReceipts = Error.BusinessRule("PURCHASING.ORDER_HAS_RECEIPTS", "La orden ya tiene recepciones: ciérrela en lugar de cancelarla.");

    public static readonly Error PurchaseHasPayments = Error.BusinessRule(
        "PURCHASING.PURCHASE_HAS_PAYMENTS", "La compra tiene pagos aplicados: anule primero los pagos (RN-PUR-05).");

    public static readonly Error PurchaseHasReturns = Error.BusinessRule(
        "PURCHASING.PURCHASE_HAS_RETURNS", "La compra tiene devoluciones: no se puede anular.");

    public static readonly Error VoidReasonRequired = Error.Validation("PURCHASING.REASON_REQUIRED", "Indique el motivo (de 5 a 300 caracteres).");

    public static readonly Error PayableNotOpen = Error.BusinessRule("PURCHASING.PAYABLE_NOT_OPEN", "La cuenta por pagar no tiene saldo pendiente.");

    public static readonly Error Overpayment = Error.BusinessRule("PURCHASING.OVERPAYMENT", "El valor aplicado supera el saldo de la cuenta por pagar.");

    public static readonly Error InvalidPayment = Error.Validation(
        "PURCHASING.INVALID_PAYMENT", "Pago inválido: valor > 0, cuentas distintas del mismo proveedor y Σ aplicaciones = valor del pago.");

    public static readonly Error ReturnExceedsPurchase = Error.BusinessRule(
        "PURCHASING.RETURN_EXCEEDS_PURCHASE", "La cantidad a devolver supera lo comprado menos lo ya devuelto (RN-PUR-06).");

    public static readonly Error InvalidSettlement = Error.Validation(
        "PURCHASING.INVALID_SETTLEMENT", "Liquidación inválida: NOTA_CREDITO, REINTEGRO o REPOSICION con su referencia.");

    // ─────────────── Fase 8 (8.4): mejoras de proveedores ───────────────

    public static readonly Error InvalidOrderingTerms = Error.Validation(
        "PURCHASING.INVALID_ORDERING_TERMS", "Pedido mínimo ≥ 0 y nota de hora de corte de hasta 200 caracteres.");

    public static readonly Error InvalidSchedule = Error.Validation(
        "PURCHASING.INVALID_SCHEDULE", "Agenda inválida: día de 1 (lunes) a 7 (domingo), notas de hasta 200 caracteres, sin repetir tipo, día y sucursal (máximo 60).");

    public static readonly Error InvalidWithholdingDefault = Error.Validation(
        "PURCHASING.INVALID_WITHHOLDING_DEFAULT", "Retención sugerida inválida: una por tipo, tarifa mayor que 0 y hasta 100 % (4 decimales), concepto de hasta 100 caracteres.");

    public static readonly Error InvalidBankAccount = Error.Validation(
        "PURCHASING.INVALID_BANK_ACCOUNT", "Cuenta bancaria inválida: banco, tipo, número de 5 a 20 dígitos, titular (hasta 150) y su identificación (tipo y número).");

    public static readonly Error BankNotFound = Error.Validation("PURCHASING.BANK_NOT_FOUND", "El banco no existe en el catálogo (GET /api/v1/purchasing/banks).");

    public static readonly Error BankAccountNotFound = Error.NotFound("PURCHASING.BANK_ACCOUNT_NOT_FOUND", "La cuenta bancaria no existe para este proveedor.");

    public static readonly Error BankAccountDuplicated = Error.Conflict(
        "PURCHASING.BANK_ACCOUNT_DUPLICATED", "Esa cuenta (banco y número) ya está registrada para este proveedor.");

    public static readonly Error BankAccountNotPending = Error.BusinessRule(
        "PURCHASING.BANK_ACCOUNT_NOT_PENDING", "La cuenta no está pendiente de verificación.");

    public static readonly Error BankAccountSameUser = Error.BusinessRule(
        "PURCHASING.BANK_ACCOUNT_SAME_USER", "La cuenta la debe verificar un usuario distinto del que la registró o modificó (RN-PUR-09).");

    public static readonly Error ProductNotFound = Error.NotFound("PURCHASING.PRODUCT_NOT_FOUND", "El producto no existe.");

    public static readonly Error InvalidPeriod = Error.Validation(
        "PURCHASING.INVALID_PERIOD", "Período inválido: la fecha inicial debe ser anterior o igual a la final (máximo 3 años) y los días entre 0 y 90.");
}
