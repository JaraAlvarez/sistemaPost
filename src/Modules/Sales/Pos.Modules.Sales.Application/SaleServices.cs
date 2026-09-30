using Pos.Application.Abstractions.Security;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Customers.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.Printing;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Sales.Application;

/// <summary>Caja desde la que se opera: sesión de caja (código + PIN), su bodega y su jornada abierta.</summary>
public sealed record TerminalScope(Guid CompanyId, Guid BranchId, Guid PosTerminalId, Guid WarehouseId, Guid UserId, CashSessionInfo? Session);

/// <summary>Resuelve la caja de la sesión (RN-SAL-01: se vende desde una caja con su jornada abierta).</summary>
public sealed class TerminalResolver(ICurrentUser current, ITerminalDirectory terminals, ICashRegister cash)
{
    public async Task<Result<TerminalScope>> ResolveAsync(bool requireOpenSession, CancellationToken cancellationToken)
    {
        if (!current.IsTerminalSession || current.PosTerminalId is not { } terminalId || current.UserId is not { } userId
            || current.CompanyId is not { } companyId || current.BranchId is not { } branchId)
        {
            return SalesErrors.TerminalRequired;
        }

        var terminal = await terminals.GetAsync(terminalId, cancellationToken);
        if (terminal is not { IsActive: true })
        {
            return SalesErrors.TerminalRequired;
        }

        var session = await cash.GetOpenSessionAsync(terminalId, cancellationToken);
        if (requireOpenSession && session is null)
        {
            return SalesErrors.NoOpenCashSession;
        }

        return new TerminalScope(companyId, branchId, terminalId, terminal.WarehouseId, userId, session);
    }

    /// <summary>La venta es de esta caja (se opera solo desde la caja en la que se inició).</summary>
    public static Result EnsureSameTerminal(Sale sale, TerminalScope scope) =>
        sale.PosTerminalId == scope.PosTerminalId ? Result.Success() : SalesErrors.OtherTerminal;
}

/// <summary>Promociones vigentes convertidas a reglas del motor (D7-16).</summary>
public sealed class PromotionRules(IActivePromotions promotions, IClock clock)
{
    public async Task<IReadOnlyList<PromotionRule>> ActiveAsync(Guid branchId, CancellationToken cancellationToken) =>
        Map(await promotions.GetActiveAsync(branchId, clock.UtcNow, cancellationToken));

    public static IReadOnlyList<PromotionRule> Map(IReadOnlyList<PromotionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return [.. definitions.Select(d => new PromotionRule(
            d.Id, d.Name, Kind(d.Kind), [.. d.Targets.Select(t => new PromotionTarget(t.ProductId, t.PackagingId, t.CategoryIds, t.BrandId, t.Quantity))],
            d.BuyQuantity, d.PayQuantity, d.Price, d.Percent, d.MinQuantity, d.MaxApplications, d.TicketText))];
    }

    private static PromotionKind Kind(string kind) => kind switch
    {
        "MULTI_BUY" => PromotionKind.MultiBuy,
        "SPECIAL_PRICE" => PromotionKind.SpecialPrice,
        "PERCENT_OFF" => PromotionKind.PercentOff,
        "QUANTITY_PRICE" => PromotionKind.QuantityPrice,
        "COMBO" => PromotionKind.Combo,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo de promoción desconocido."),
    };
}

/// <summary>
/// Existencias para vender (RN-SAL-17/18): no se vende más de lo que hay en la bodega de la caja y un lote vencido exige
/// autorización. Es un aviso temprano; la verificación definitiva la hace el kardex al cobrar, con el saldo bloqueado.
/// </summary>
public sealed class StockGuard(IInventoryQueries inventory, IClock clock)
{
    public async Task<Result> CheckAsync(Sale sale, Guid productId, bool isStockable, bool expiredAuthorized, string productLabel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);
        if (!isStockable)
        {
            return Result.Success();
        }

        var availability = (await inventory.GetSaleAvailabilityAsync(sale.WarehouseId, [productId], clock.Today, cancellationToken)).GetValueOrDefault(productId)
            ?? new SaleAvailability(0m, 0m);
        var requested = sale.ActiveLines.Where(l => l.ProductId == productId).Sum(l => l.BaseQuantity);
        if (requested > availability.OnHand)
        {
            return Error.BusinessRule(
                SalesErrors.InsufficientStock.Code,
                $"{SalesErrors.InsufficientStock.Message} {productLabel}: hay {Math.Max(0m, availability.OnHand):0.###}, se requieren {requested:0.###}.");
        }

        return availability.ExpiredQuantity > 0m && !expiredAuthorized
            ? Error.BusinessRule(
                SalesErrors.ExpiredLotRequiresAuthorization.Code,
                $"{SalesErrors.ExpiredLotRequiresAuthorization.Message} {productLabel}: {availability.ExpiredQuantity:0.###} en lotes vencidos.")
            : Result.Success();
    }
}

/// <summary>Cliente de la venta: el tercero elegido o el Consumidor final.</summary>
/// <summary>Cliente resuelto para la venta: snapshot del comprador, lista de precio y si siempre pide factura.</summary>
public sealed record ResolvedCustomer(CustomerSnapshot Snapshot, SalePricing Pricing, bool AlwaysRequestsInvoice);

/// <summary>
/// Cliente de la venta (Fase 8): el tercero con sus datos fiscales completos (D8-05), su rol de cliente (se crea la primera vez que
/// compra, D8-02; bloqueado = rechazo) y su lista de precio efectiva (propia → grupo → general, D8-09).
/// </summary>
public sealed class CustomerResolver(IPartyDirectory parties, IPartyRegistry registry, ICustomerDirectory customers, ICatalogSaleItems catalog)
{
    public async Task<CustomerSnapshot> FinalConsumerAsync(CancellationToken cancellationToken) =>
        await parties.GetFinalConsumerAsync(cancellationToken) is { } consumer
            ? new CustomerSnapshot(null, consumer.DisplayName, consumer.IdentificationType, consumer.IdentificationNumber, null)
            : new CustomerSnapshot(null, "Consumidor final", "CC", "222222222222", null);

    public async Task<Result<ResolvedCustomer>> ResolveAsync(Guid? partyId, Guid branchId, CancellationToken cancellationToken)
    {
        if (partyId is not { } id)
        {
            return new ResolvedCustomer(await FinalConsumerAsync(cancellationToken), SalePricing.General, false);
        }

        if ((await registry.GetProfilesAsync([id], cancellationToken)).GetValueOrDefault(id) is not { Status: "ACTIVE" } party)
        {
            return SalesErrors.CustomerNotFound;
        }

        if (party.IsSystem)
        {
            return new ResolvedCustomer(await FinalConsumerAsync(cancellationToken), SalePricing.General, false);
        }

        var profile = await customers.ResolveForSaleAsync(id, branchId, cancellationToken);
        if (profile.IsFailure)
        {
            return profile.Error;
        }

        var pricing = new SalePricing(null, null, profile.Value.GroupCode, true);
        if (profile.Value.PriceListId is { } listId && await catalog.GetPriceListAsync(listId, cancellationToken) is { IsActive: true, IsDefault: false } list)
        {
            pricing = new SalePricing(list.Id, list.Code, profile.Value.GroupCode, list.AllowsPromotions);
        }

        return new ResolvedCustomer(
            Snapshot(party, profile.Value.ServiceConsent ? profile.Value.ConsentPolicyVersion : null), pricing, profile.Value.AlwaysRequestsInvoice);
    }

    /// <summary>Datos fiscales vigentes del comprador (se toman de nuevo al cobrar, RN-SAL-19).</summary>
    public async Task<PartyProfile?> ProfileAsync(Guid partyId, CancellationToken cancellationToken) =>
        (await registry.GetProfilesAsync([partyId], cancellationToken)).GetValueOrDefault(partyId);

    public static CustomerSnapshot Snapshot(PartyProfile party, int? consentPolicyVersion)
    {
        ArgumentNullException.ThrowIfNull(party);
        return new CustomerSnapshot(
            party.Id, party.DisplayName, party.IdentificationType,
            party.CheckDigit is null ? party.IdentificationNumber : $"{party.IdentificationNumber}-{party.CheckDigit}", party.Email,
            new CustomerFiscal(party.PersonType, party.CheckDigit, party.TaxRegime, party.FiscalResponsibilities, party.Address, party.MunicipalityCode, party.Phone,
                consentPolicyVersion));
    }

    /// <summary>
    /// Datos que faltan para la factura electrónica (RN-SAL-13): correo, régimen y responsabilidades; la persona jurídica además
    /// dirección y municipio.
    /// </summary>
    public static IReadOnlyList<string> MissingInvoiceData(PartyProfile party)
    {
        ArgumentNullException.ThrowIfNull(party);
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(party.Email))
        {
            missing.Add("correo electrónico");
        }

        if (string.IsNullOrWhiteSpace(party.TaxRegime))
        {
            missing.Add("régimen");
        }

        if (party.FiscalResponsibilities.Count == 0)
        {
            missing.Add("responsabilidades fiscales");
        }

        if (party.PersonType == "LEGAL" && string.IsNullOrWhiteSpace(party.Address))
        {
            missing.Add("dirección");
        }

        if (party.PersonType == "LEGAL" && string.IsNullOrWhiteSpace(party.MunicipalityCode))
        {
            missing.Add("municipio");
        }

        return missing;
    }
}

/// <summary>Venta → DTO de la API.</summary>
public static class SaleViews
{
    public static SaleDto ToDto(this Sale sale, IReadOnlyList<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return new SaleDto(
            sale.Id, sale.Number, sale.Status.Db(), sale.ReturnStatus.Db(), sale.BranchId, sale.PosTerminalId, sale.CashSessionId, sale.CashierId, sale.BusinessDate,
            new SaleCustomerDto(sale.CustomerId, sale.CustomerName, sale.CustomerIdentificationType, sale.CustomerIdentification, sale.CustomerEmail),
            sale.OpenedAt, sale.CompletedAt, sale.HoldLabel, sale.ExchangeId, sale.ExchangeCredit, sale.Gross, sale.PromotionTotal, sale.DiscountTotal, sale.Subtotal,
            sale.TaxTotal, sale.RoundingAdjustment, sale.Total, Math.Max(0m, sale.Total - sale.ExchangeCredit), sale.PaidTotal, sale.ChangeTotal, sale.VoidReason,
            [.. sale.Lines.OrderBy(l => l.LineNo).Select(l => new SaleLineDto(
                l.Id, l.LineNo, l.ProductId, l.Sku, l.Name, l.ScannedCode, l.Source, l.BaseUnitCode, l.PackagingId, l.PackagingName, l.Factor, l.Quantity, l.UnitPrice,
                l.PriceOverridden, l.Gross, l.PromotionId, l.PromotionName, l.PromotionDiscount, l.LineDiscount, l.GlobalDiscountShare, l.TaxBase, l.TaxTotal, l.Total,
                l.Status.Db(), l.ExpiredAuthorizedBy is not null, l.ReturnedQuantity,
                [.. l.Taxes.Select(t => new SaleLineTaxDto(t.Code, t.Kind, t.Rate, t.FixedAmount, t.TaxBase, t.Amount))], l.PriceListId, l.PriceSource))],
            [.. sale.Payments.OrderBy(p => p.LineNo).Select(p => new SalePaymentDto(
                p.Id, p.PaymentMethodId, p.MethodCode, p.MethodKind, p.Tendered, p.Applied, p.Change, p.Reference, p.CardFranchise, p.CardLast4))],
            [.. sale.Discounts.Select(d => new SaleDiscountDto(d.Id, d.Scope.Db(), d.SaleLineId, d.Percent, d.Amount, d.Reason, d.AuthorizedBy, d.Status.Db()))],
            warnings ?? [], sale.PriceListId, sale.PriceListCode, sale.CustomerGroupCode, sale.InvoiceRequested);
    }
}

/// <summary>
/// Tiquete de venta en el modelo neutro (D7-14): encabezado de la empresa, líneas con su promoción, totales, impuestos por
/// tarifa, pagos, cambio y el número en código de barras (para buscar la venta en un cambio). Hoy es un comprobante interno,
/// no una factura electrónica (Fase 7, pregunta 1).
/// </summary>
public static class SaleTicketBuilder
{
    public static TicketDocument Build(Sale sale, TicketHeader header, bool copy, bool openDrawer, string? documentType)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(header);
        var e = new List<TicketElement>();
        AddHeader(e, header);
        e.Add(new TextLine(sale.Status == SaleStatus.Voided ? "VENTA ANULADA" : "TIQUETE DE VENTA", TicketAlign.Center, Bold: true));
        if (copy)
        {
            e.Add(new TextLine("*** COPIA ***", TicketAlign.Center, Bold: true));
        }

        e.Add(new ColumnsLine("No.", sale.Number ?? string.Empty, Bold: true));
        e.Add(new ColumnsLine("Fecha", (sale.CompletedAt ?? sale.OpenedAt).ToOffset(TimeSpan.FromHours(-5)).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        e.Add(new ColumnsLine("Caja", $"{header.TerminalCode} · {header.CashierName}"));
        e.Add(new ColumnsLine("Cliente", sale.CustomerName));
        if (sale.CustomerId is not null)
        {
            e.Add(new ColumnsLine("Identificación", sale.CustomerIdentification));
            if (sale.PriceListCode is { } list)
            {
                e.Add(new ColumnsLine("Lista de precios", list));
            }
        }

        e.Add(new SeparatorLine());
        foreach (var line in sale.ActiveLines.OrderBy(l => l.LineNo))
        {
            var unit = line.PackagingName ?? line.BaseUnitCode;
            e.Add(new TextLine(line.Name));
            e.Add(new ColumnsLine($"  {TicketLayout.Quantity(line.Quantity)} {unit} x {TicketLayout.Money(line.UnitPrice)}", TicketLayout.Money(line.Gross)));
            if (line.PromotionDiscount > 0m)
            {
                e.Add(new ColumnsLine($"  {line.PromotionName}", $"-{TicketLayout.Money(line.PromotionDiscount)}"));
            }

            if (line.LineDiscount > 0m)
            {
                e.Add(new ColumnsLine("  Descuento", $"-{TicketLayout.Money(line.LineDiscount)}"));
            }
        }

        e.Add(new SeparatorLine());
        e.Add(new ColumnsLine("Subtotal", TicketLayout.Money(sale.Gross)));
        if (sale.PromotionTotal > 0m)
        {
            e.Add(new ColumnsLine("Promociones", $"-{TicketLayout.Money(sale.PromotionTotal)}"));
        }

        var globalDiscount = sale.ActiveLines.Sum(l => l.GlobalDiscountShare);
        if (globalDiscount > 0m)
        {
            e.Add(new ColumnsLine("Descuento", $"-{TicketLayout.Money(globalDiscount)}"));
        }

        if (sale.RoundingAdjustment != 0m)
        {
            e.Add(new ColumnsLine("Ajuste al peso", TicketLayout.Money(sale.RoundingAdjustment)));
        }

        e.Add(new TextLine($"TOTAL {TicketLayout.Money(sale.Total)}", TicketAlign.Right, Bold: true, DoubleSize: true));
        foreach (var payment in sale.Payments.OrderBy(p => p.LineNo))
        {
            var label = payment.MethodKind == "EXCHANGE_CREDIT" ? "Crédito por cambio" : payment.MethodCode;
            var reference = payment.CardLast4 is not null ? $" ****{payment.CardLast4}" : payment.Reference is not null ? $" ref {payment.Reference}" : string.Empty;
            e.Add(new ColumnsLine($"{label}{reference}", TicketLayout.Money(payment.Tendered)));
        }

        if (sale.ChangeTotal > 0m)
        {
            e.Add(new ColumnsLine("Cambio", TicketLayout.Money(sale.ChangeTotal), Bold: true));
        }

        e.Add(new SeparatorLine());
        e.Add(new TextLine("Impuestos", Bold: true));
        foreach (var tax in sale.ActiveLines.SelectMany(l => l.Taxes).GroupBy(t => (t.Code, t.Rate, t.FixedAmount)).OrderBy(g => g.Key.Code, StringComparer.Ordinal))
        {
            var rate = tax.Key.Rate is { } r ? $" {r:0.##}%" : string.Empty;
            e.Add(new ColumnsLine($"{tax.Key.Code}{rate} base {TicketLayout.Money(tax.Where(t => t.Rate is not null).Sum(t => t.TaxBase))}", TicketLayout.Money(tax.Sum(t => t.Amount))));
        }

        e.Add(new ColumnsLine("Unidades", TicketLayout.Quantity(sale.ActiveLines.Sum(l => l.Quantity))));
        e.Add(new SeparatorLine());
        e.Add(new TextLine(documentType is null or "INTERNAL_RECEIPT" ? "Comprobante de venta. No es factura electrónica." : "Documento equivalente electrónico POS.",
            TicketAlign.Center));
        e.Add(new TextLine("Cambios dentro del plazo con este tiquete.", TicketAlign.Center));
        if (sale.CustomerId is not null && sale.CustomerFiscal?.ConsentPolicyVersion is { } policy)
        {
            e.Add(new TextLine($"Autorizó el tratamiento de datos (política v{policy}).", TicketAlign.Center));
        }

        if (sale.InvoiceRequested)
        {
            e.Add(new TextLine("El cliente solicitó factura electrónica.", TicketAlign.Center));
        }
        if (sale.Number is { } number)
        {
            e.Add(new BarcodeElement(number));
        }

        e.Add(new TextLine("¡Gracias por su compra!", TicketAlign.Center));
        return new TicketDocument($"Venta {sale.Number}", e, Cut: true, OpenDrawer: openDrawer);
    }

    public static TicketDocument BuildReturn(CustomerReturn customerReturn, TicketHeader header, bool openDrawer)
    {
        ArgumentNullException.ThrowIfNull(customerReturn);
        ArgumentNullException.ThrowIfNull(header);
        var e = new List<TicketElement>();
        AddHeader(e, header);
        e.Add(new TextLine(customerReturn.Kind == ReturnKind.Exchange ? "CAMBIO DE MERCANCÍA" : "REINTEGRO POR GARANTÍA", TicketAlign.Center, Bold: true));
        e.Add(new ColumnsLine("No.", customerReturn.Number ?? string.Empty, Bold: true));
        e.Add(new ColumnsLine("Venta", customerReturn.OriginalSaleNumber));
        e.Add(new ColumnsLine("Caja", $"{header.TerminalCode} · {header.CashierName}"));
        e.Add(new SeparatorLine());
        foreach (var line in customerReturn.Lines)
        {
            e.Add(new TextLine(line.Name));
            e.Add(new ColumnsLine($"  {TicketLayout.Quantity(line.Quantity)} recibido", TicketLayout.Money(line.CreditAmount)));
        }

        e.Add(new SeparatorLine());
        e.Add(new ColumnsLine(customerReturn.Kind == ReturnKind.Exchange ? "Crédito aplicado" : "Reintegrado en efectivo", TicketLayout.Money(customerReturn.CreditTotal), Bold: true));
        e.Add(new TextLine($"Motivo: {customerReturn.Reason}"));
        if (customerReturn.Kind == ReturnKind.WarrantyRefund)
        {
            e.Add(new FeedElement(2));
            e.Add(new TextLine("Firma del cliente: ____________________"));
        }

        return new TicketDocument($"Cambio {customerReturn.Number}", e, Cut: true, OpenDrawer: openDrawer);
    }

    private static void AddHeader(List<TicketElement> e, TicketHeader header)
    {
        e.Add(new TextLine(header.TradeName, TicketAlign.Center, Bold: true, DoubleSize: true));
        if (!string.Equals(header.LegalName, header.TradeName, StringComparison.OrdinalIgnoreCase))
        {
            e.Add(new TextLine(header.LegalName, TicketAlign.Center));
        }

        e.Add(new TextLine($"NIT {header.Nit}", TicketAlign.Center));
        e.Add(new TextLine(header.BranchName, TicketAlign.Center));
        if ((header.BranchAddress ?? header.Address) is { } address)
        {
            e.Add(new TextLine(address, TicketAlign.Center));
        }

        if (header.Phone is { } phone)
        {
            e.Add(new TextLine($"Tel. {phone}", TicketAlign.Center));
        }

        e.Add(new SeparatorLine());
    }
}
