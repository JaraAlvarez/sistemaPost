using Pos.Application.Abstractions.Security;
using Pos.Modules.Promotions.Contracts;
using Pos.Printing;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Contracts;

/// <summary>Permisos del módulo Sales (docs/fases/fase-07-propuesta.md §7). Los que "admiten supervisor" se autorizan con código y PIN.</summary>
public static class SalesPermissions
{
    public const string SaleCreate = "sales.sale.create";
    public const string LineVoid = "sales.line.void";
    public const string DiscountApply = "sales.discount.apply";
    public const string PriceOverride = "sales.price.override";
    public const string SaleCancel = "sales.sale.cancel";
    public const string SaleVoid = "sales.sale.void";
    public const string ExpiredSell = "sales.expired.sell";
    public const string ExchangeCreate = "sales.exchange.create";
    public const string WarrantyRefund = "sales.refund.warranty";
    public const string SaleReprint = "sales.sale.reprint";
    public const string SaleView = "sales.sale.view";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(SaleCreate, "Vender: iniciar, escanear, suspender, recuperar y cobrar", isSensitive: false),
        new(LineVoid, "Eliminar una línea de la venta en curso (queda registrada)", isSensitive: false),
        new(DiscountApply, "Aplicar descuentos manuales de línea o globales (admite autorización de supervisor)", isSensitive: true),
        new(PriceOverride, "Fijar el precio de un producto con precio abierto (admite autorización de supervisor)", isSensitive: true),
        new(SaleCancel, "Cancelar una venta en curso o suspendida (admite autorización de supervisor)", isSensitive: true),
        new(SaleVoid, "Anular una venta completada con su jornada abierta (admite autorización de supervisor)", isSensitive: true),
        new(ExpiredSell, "Vender un producto con existencias de un lote vencido (admite autorización de supervisor)", isSensitive: true),
        new(ExchangeCreate, "Recibir un cambio de mercancía (admite autorización de supervisor)", isSensitive: true),
        new(WarrantyRefund, "Reintegrar dinero por garantía (excepción legal, solo el propietario)", isSensitive: true),
        new(SaleReprint, "Reimprimir el tiquete de una venta (marcado COPIA)", isSensitive: false),
        new(SaleView, "Consultar ventas, cambios y reportes de ventas", isSensitive: false),
    ];
}

public sealed record SaleLineTaxDto(string Code, string Kind, decimal? Rate, decimal? FixedAmount, decimal Base, decimal Amount);

public sealed record SaleLineDto(
    Guid Id,
    int LineNo,
    Guid ProductId,
    string Sku,
    string Name,
    string? ScannedCode,
    string Source,
    string BaseUnitCode,
    Guid? PackagingId,
    string? PackagingName,
    decimal Factor,
    decimal Quantity,
    decimal UnitPrice,
    bool PriceOverridden,
    decimal Gross,
    Guid? PromotionId,
    string? PromotionName,
    decimal PromotionDiscount,
    decimal LineDiscount,
    decimal GlobalDiscountShare,
    decimal TaxBase,
    decimal TaxTotal,
    decimal Total,
    string Status,
    bool ExpiredLotAuthorized,
    decimal ReturnedQuantity,
    IReadOnlyList<SaleLineTaxDto> Taxes,
    Guid? PriceListId = null,
    string PriceSource = "DEFAULT");

public sealed record SalePaymentDto(
    Guid Id, Guid PaymentMethodId, string MethodCode, string MethodKind, decimal Tendered, decimal Applied, decimal Change, string? Reference, string? CardFranchise,
    string? CardLast4);

public sealed record SaleDiscountDto(Guid Id, string Scope, Guid? SaleLineId, decimal? Percent, decimal? Amount, string Reason, Guid? AuthorizedBy, string Status);

public sealed record SaleCustomerDto(Guid? PartyId, string Name, string IdentificationType, string Identification, string? Email);

/// <summary>
/// Venta. <c>AmountDue</c>: lo que falta pagar descontando el crédito de un cambio. <c>Warnings</c>: avisos para la caja
/// (p. ej. un producto con pocas existencias o una promoción que dejó de regir).
/// </summary>
public sealed record SaleDto(
    Guid Id,
    string? Number,
    string Status,
    string ReturnStatus,
    Guid BranchId,
    Guid PosTerminalId,
    Guid CashSessionId,
    Guid CashierId,
    DateOnly BusinessDate,
    SaleCustomerDto Customer,
    DateTimeOffset OpenedAt,
    DateTimeOffset? CompletedAt,
    string? HoldLabel,
    Guid? ExchangeId,
    decimal ExchangeCredit,
    decimal Gross,
    decimal PromotionTotal,
    decimal DiscountTotal,
    decimal Subtotal,
    decimal TaxTotal,
    decimal RoundingAdjustment,
    decimal Total,
    decimal AmountDue,
    decimal PaidTotal,
    decimal ChangeTotal,
    string? VoidReason,
    IReadOnlyList<SaleLineDto> Lines,
    IReadOnlyList<SalePaymentDto> Payments,
    IReadOnlyList<SaleDiscountDto> Discounts,
    IReadOnlyList<string> Warnings,
    Guid? PriceListId = null,
    string? PriceListCode = null,
    string? CustomerGroupCode = null,
    bool InvoiceRequested = false);

/// <summary>
/// Resultado de cobrar, anular o reimprimir: la venta, el tiquete en el modelo neutro (lo imprime el agente de caja) y si hay
/// que abrir el cajón (hubo efectivo). El documento fiscal es el que respalda el tiquete (Fase 11-B, D11B-03): la factura de la venta
/// o, en una venta anulada, su nota crédito si la hay; <c>FiscalNumber</c> y <c>Cufe</c> llegan si la DIAN ya lo validó.
/// </summary>
public sealed record SaleReceiptDto(
    SaleDto Sale,
    TicketDocument Ticket,
    string TicketText,
    bool OpenDrawer,
    string? DocumentType,
    string? DocumentStatus,
    Guid? FiscalDocumentId = null,
    string? FiscalNumber = null,
    string? Cufe = null);

public sealed record SaleSummaryDto(
    Guid Id, string? Number, string Status, string ReturnStatus, DateOnly BusinessDate, string TerminalCode, string CashierName, string CustomerName, decimal Total,
    DateTimeOffset OpenedAt, DateTimeOffset? CompletedAt, string? HoldLabel);

public sealed record ExchangeLineDto(Guid SaleLineId, Guid ProductId, string Sku, string Name, decimal Quantity, decimal CreditAmount, string Destination);

/// <summary>Cambio de mercancía (EXCHANGE) o reintegro por garantía (WARRANTY_REFUND).</summary>
public sealed record ExchangeDto(
    Guid Id,
    string? Number,
    string Kind,
    string Status,
    Guid OriginalSaleId,
    string OriginalSaleNumber,
    Guid? ReplacementSaleId,
    decimal CreditTotal,
    string Reason,
    DateOnly BusinessDate,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<ExchangeLineDto> Lines);

public sealed record SimulationLineRequest(Guid ProductId, Guid? PackagingId, decimal Quantity);

public sealed record SimulationLineDto(Guid ProductId, string Sku, string Name, decimal Quantity, decimal UnitPrice, decimal Gross, string? PromotionName, decimal PromotionDiscount,
    decimal Total);

public sealed record SimulationDto(IReadOnlyList<SimulationLineDto> Lines, decimal Gross, decimal PromotionTotal, decimal Total);

/// <summary>
/// Simula lo que cobraría una venta de ejemplo con unas promociones (el encargado prueba una promoción en borrador antes de
/// activarla, RN-PRM). Usa el mismo motor que la caja.
/// </summary>
public interface IPriceSimulator
{
    Task<Result<SimulationDto>> SimulateAsync(
        Guid branchId, IReadOnlyList<SimulationLineRequest> lines, IReadOnlyList<PromotionDefinition> promotions, CancellationToken cancellationToken = default);
}

/// <summary>Movimiento del historial de un cliente. <c>Kind</c>: SALE, EXCHANGE o WARRANTY_REFUND.</summary>
public sealed record CustomerHistoryEntryDto(
    string Kind, Guid Id, string? Number, DateOnly BusinessDate, DateTimeOffset At, string BranchName, string TerminalCode, decimal Total, string Status,
    IReadOnlyList<string> PaymentMethods, string? OriginalSaleNumber);

public sealed record CustomerTopProductDto(Guid ProductId, string Sku, string Name, decimal Quantity, decimal Total);

/// <summary>
/// Resumen de compras del cliente (D8-13): total comprado = ventas completadas − créditos de cambios − reintegros (un cambio no
/// se cuenta dos veces); ticket promedio sobre las ventas completadas.
/// </summary>
public sealed record CustomerSalesSummaryDto(
    int Purchases, decimal TotalPurchased, decimal AverageTicket, DateTimeOffset? FirstPurchaseAt, DateTimeOffset? LastPurchaseAt, string? UsualBranch,
    int VoidedSales, int Exchanges, decimal ReturnedCredit, IReadOnlyList<CustomerTopProductDto> TopProducts);

/// <summary>Historial de compras de un cliente, calculado en línea desde las ventas (D8-13).</summary>
public interface ICustomerSalesHistory
{
    Task<IReadOnlyList<CustomerHistoryEntryDto>> GetHistoryAsync(
        Guid partyId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<CustomerSalesSummaryDto> GetSummaryAsync(Guid partyId, CancellationToken cancellationToken = default);
}
