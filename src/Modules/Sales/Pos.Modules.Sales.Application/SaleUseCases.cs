using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Sales.Application;

/// <summary>Carga una venta de esta caja para editarla y la recalcula con las promociones vigentes.</summary>
public sealed class SaleEditor(ISalesStore store, TerminalResolver terminals, PromotionRules promotions)
{
    public async Task<Result<(Sale Sale, TerminalScope Scope)>> LoadAsync(Guid saleId, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: false, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var sale = await store.GetSaleAsync(saleId, cancellationToken);
        if (sale is null)
        {
            return SalesErrors.SaleNotFound;
        }

        var same = TerminalResolver.EnsureSameTerminal(sale, scope.Value);
        return same.IsSuccess ? (sale, scope.Value) : same.Error;
    }

    public async Task RecalculateAsync(Sale sale, CancellationToken cancellationToken) =>
        sale.Recalculate(await promotions.ActiveAsync(sale.BranchId, cancellationToken));
}

// ─────────────────────────────── Iniciar y consultar la venta en curso ───────────────────────────────

/// <summary>
/// Inicia una venta en la caja de la sesión (D7-01): exige la jornada ABIERTA de la caja (RN-SAL-01) y que no haya otra venta
/// en curso (una a la vez; las demás se suspenden). La fecha de negocio es la de la jornada (D6-04).
/// </summary>
public sealed record StartSaleCommand : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class StartSaleHandler(ISalesStore store, TerminalResolver terminals, CustomerResolver customers, IIdGenerator ids, IClock clock)
    : ICommandHandler<StartSaleCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(StartSaleCommand request, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: true, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        if (await store.GetOpenSaleAsync(scope.Value.PosTerminalId, cancellationToken) is { } open)
        {
            return Error.Conflict(SalesErrors.OpenSaleExists.Code, $"{SalesErrors.OpenSaleExists.Message} Venta en curso: {open.Id}.");
        }

        var consumer = await customers.FinalConsumerAsync(cancellationToken);
        var session = scope.Value.Session!;
        var sale = Sale.Start(
            ids.NewId(), scope.Value.CompanyId, scope.Value.BranchId, scope.Value.PosTerminalId, scope.Value.WarehouseId, session.Id, scope.Value.UserId,
            session.BusinessDate, clock.UtcNow, consumer);
        store.Add(sale);
        return sale.ToDto();
    }
}

/// <summary>Venta en curso de esta caja (para retomarla tras un corte de luz o de red).</summary>
public sealed record GetCurrentSaleQuery : IQuery<SaleDto>;

internal sealed class GetCurrentSaleHandler(ISalesStore store, TerminalResolver terminals) : IQueryHandler<GetCurrentSaleQuery, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(GetCurrentSaleQuery request, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: false, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        return await store.GetOpenSaleAsync(scope.Value.PosTerminalId, cancellationToken) is { } sale
            ? sale.ToDto()
            : Error.NotFound("SALES.NO_OPEN_SALE", "La caja no tiene una venta en curso.");
    }
}

// ─────────────────────────────── Líneas ───────────────────────────────

/// <summary>
/// Agrega un producto por código leído (barras, báscula o SKU) o elegido por búsqueda. Con código de báscula la cantidad sale
/// de la etiqueta; si no, <c>Quantity</c> (1 por defecto). Valida que se pueda vender (RN-SAL-02), las existencias (RN-SAL-17)
/// y los lotes vencidos (RN-SAL-18: <c>AuthorizeExpired</c> solo llega desde el endpoint que exige esa autorización).
/// </summary>
public sealed record AddLineCommand(Guid SaleId, string? Code, Guid? ProductId, Guid? PackagingId, decimal? Quantity, bool AuthorizeExpired = false) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class AddLineHandler(
    SaleEditor editor,
    ICatalogSaleItems catalog,
    StockGuard stock,
    ISettingsReader settings,
    IAuthorizationScope authorization,
    IActorContext actor,
    IAuditWriter audit,
    IIdGenerator ids) : ICommandHandler<AddLineCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(AddLineCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (sale, scope) = loaded.Value;
        var item = !string.IsNullOrWhiteSpace(request.Code)
            ? await catalog.FindByCodeAsync(request.Code, sale.BranchId, sale.PriceListId, cancellationToken)
            : request.ProductId is { } productId ? await catalog.GetAsync(productId, request.PackagingId, sale.BranchId, sale.PriceListId, cancellationToken) : null;
        if (item is null)
        {
            return SalesAppErrors.CodeNotFound;
        }

        if (!item.IsSellable)
        {
            return Error.BusinessRule(SalesErrors.ProductNotSellable.Code, $"{item.Sku} · {item.Name}: {string.Join(" ", item.NotSellableReasons)}");
        }

        var scale = item.Source is "SCALE_WEIGHT" or "SCALE_PRICE";
        var quantity = scale ? item.Quantity : request.Quantity ?? 1m;
        Guid? expiredBy = request.AuthorizeExpired ? authorization.Current?.AuthorizedBy ?? actor.ActorId : null;
        var input = new SaleLineInput(
            item.ProductId, item.Sku, item.Name, request.Code?.Trim(), item.Source, item.BaseUnitCode, item.PackagingId, item.PackagingName, item.Factor, quantity,
            item.UnitPrice ?? 0m, item.PriceIncludesTax, item.CategoryId, item.BrandId, item.IsStockable, item.AllowsDecimalQuantity, item.AllowsOpenPrice,
            [.. item.Taxes.Select(t => new PricingTax(t.TaxId, t.Code, t.Kind, t.Rate, t.FixedAmount))], expiredBy, item.PriceListId,
            item.UnitPrice is null ? PriceSources.Open : item.PriceSource);
        var merge = await settings.GetAsync(SalesSettings.MergeSameProduct, new SettingContext(sale.CompanyId, sale.BranchId, scope.PosTerminalId), cancellationToken);
        var line = sale.AddLine(input, merge, ids.NewId);
        if (line.IsFailure)
        {
            return line.Error;
        }

        var available = await stock.CheckAsync(sale, item.ProductId, item.IsStockable, line.Value.ExpiredAuthorizedBy is not null, $"{item.Sku} · {item.Name}", cancellationToken);
        if (available.IsFailure)
        {
            return available.Error;
        }

        if (expiredBy is not null)
        {
            await audit.WriteAsync(
                new AuditEntry("sales", "SALE_EXPIRED_LOT_AUTHORIZED", nameof(Sale), sale.Id, sale.AuditLabel,
                    $"Venta de {item.Sku} · {item.Name} con existencias de un lote vencido autorizada.", AuthorizedBy: authorization.Current?.AuthorizedBy,
                    Severity: AuditSeverity.Warning),
                cancellationToken);
        }

        await editor.RecalculateAsync(sale, cancellationToken);
        return sale.ToDto(item.UnitPrice is null ? [$"{item.Name}: fije el precio (precio abierto) antes de cobrar."] : null);
    }
}

public sealed record ChangeQuantityCommand(Guid SaleId, Guid LineId, decimal Quantity) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class ChangeQuantityHandler(SaleEditor editor, StockGuard stock) : ICommandHandler<ChangeQuantityCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(ChangeQuantityCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var sale = loaded.Value.Sale;
        var line = sale.ChangeQuantity(request.LineId, request.Quantity);
        if (line.IsFailure)
        {
            return line.Error;
        }

        var available = await stock.CheckAsync(
            sale, line.Value.ProductId, line.Value.IsStockable, line.Value.ExpiredAuthorizedBy is not null, $"{line.Value.Sku} · {line.Value.Name}", cancellationToken);
        if (available.IsFailure)
        {
            return available.Error;
        }

        await editor.RecalculateAsync(sale, cancellationToken);
        return sale.ToDto();
    }
}

/// <summary>Elimina una línea: queda VOIDED con quién y cuándo (RN-SAL-06).</summary>
public sealed record VoidLineCommand(Guid SaleId, Guid LineId) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class VoidLineHandler(SaleEditor editor, IActorContext actor, IClock clock, IAuditWriter audit, IAuthorizationScope authorization)
    : ICommandHandler<VoidLineCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(VoidLineCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var sale = loaded.Value.Sale;
        var voided = sale.VoidLine(request.LineId, actor.ActorId!.Value, clock.UtcNow);
        if (voided.IsFailure)
        {
            return voided.Error;
        }

        // Fase 10 (§5.1): la línea eliminada también queda en la bitácora, con quién autorizó si fue un supervisor.
        var line = sale.Lines.First(l => l.Id == request.LineId);
        await audit.WriteAsync(
            new AuditEntry("sales", "SALE_LINE_VOIDED", nameof(Sale), sale.Id, sale.AuditLabel,
                $"Línea eliminada: {line.Quantity:0.####} × {line.Name} ({line.Gross:N2}).", AuthorizedBy: authorization.Current?.AuthorizedBy),
            cancellationToken);
        await editor.RecalculateAsync(sale, cancellationToken);
        return sale.ToDto();
    }
}

/// <summary>Precio abierto (RN-SAL-05): el endpoint exige el permiso o la autorización de supervisor.</summary>
public sealed record OverridePriceCommand(Guid SaleId, Guid LineId, decimal Price) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class OverridePriceHandler(SaleEditor editor, IAuthorizationScope authorization, IActorContext actor, IAuditWriter audit)
    : ICommandHandler<OverridePriceCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(OverridePriceCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var sale = loaded.Value.Sale;
        var line = sale.OverridePrice(request.LineId, request.Price, authorization.Current?.AuthorizedBy ?? actor.ActorId);
        if (line.IsFailure)
        {
            return line.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("sales", "SALE_PRICE_OVERRIDDEN", nameof(Sale), sale.Id, sale.AuditLabel,
                $"Precio de {line.Value.Sku} · {line.Value.Name} fijado en {request.Price:N2} (lista: {line.Value.OriginalUnitPrice:N2}).",
                AuthorizedBy: authorization.Current?.AuthorizedBy, Severity: AuditSeverity.Warning),
            cancellationToken);
        await editor.RecalculateAsync(sale, cancellationToken);
        return sale.ToDto();
    }
}

// ─────────────────────────────── Descuentos ───────────────────────────────

/// <summary>
/// Descuento manual de línea (<c>LineId</c>) o global (sin línea): SIEMPRE autorizado (RN-SAL-04, Fase 7 pregunta 4); el
/// endpoint exige el permiso o la autorización de supervisor. Se aplica después de la promoción.
/// </summary>
public sealed record ApplyDiscountCommand(Guid SaleId, Guid? LineId, decimal? Percent, decimal? Amount, string Reason) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class ApplyDiscountHandler(
    SaleEditor editor, IAuthorizationScope authorization, IActorContext actor, IAuditWriter audit, IIdGenerator ids, IClock clock)
    : ICommandHandler<ApplyDiscountCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(ApplyDiscountCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var sale = loaded.Value.Sale;
        var authorizedBy = authorization.Current?.AuthorizedBy ?? actor.ActorId;
        var applied = sale.ApplyDiscount(ids.NewId(), request.LineId, request.Percent, request.Amount, request.Reason, actor.ActorId!.Value, authorizedBy, clock.UtcNow);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        await editor.RecalculateAsync(sale, cancellationToken);

        // Un descuento en valor no puede superar el neto (el motor lo recorta): se rechaza en lugar de aplicarlo a medias.
        var effective = request.LineId is { } lineId
            ? sale.Lines.Single(l => l.Id == lineId).LineDiscount
            : sale.ActiveLines.Sum(l => l.GlobalDiscountShare);
        if (request.Amount is { } amount && effective != amount)
        {
            return SalesErrors.InvalidDiscount;
        }

        await audit.WriteAsync(
            new AuditEntry("sales", "SALE_DISCOUNT_APPLIED", nameof(Sale), sale.Id, sale.AuditLabel,
                $"Descuento {(request.LineId is null ? "global" : "de línea")} de {effective:N2}"
                + (request.Percent is { } p ? $" ({p:0.##} %)" : string.Empty) + $": {applied.Value.Reason}",
                AuthorizedBy: authorization.Current?.AuthorizedBy, Severity: AuditSeverity.Warning),
            cancellationToken);
        return sale.ToDto();
    }
}

public sealed record RemoveDiscountCommand(Guid SaleId, Guid DiscountId) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class RemoveDiscountHandler(SaleEditor editor) : ICommandHandler<RemoveDiscountCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(RemoveDiscountCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var sale = loaded.Value.Sale;
        var removed = sale.RemoveDiscount(request.DiscountId);
        if (removed.IsFailure)
        {
            return removed.Error;
        }

        await editor.RecalculateAsync(sale, cancellationToken);
        return sale.ToDto();
    }
}

// ─────────────────────────────── Cliente, suspender, recuperar, cancelar ───────────────────────────────

/// <summary>
/// Cliente de la venta (null = Consumidor final) y "pide factura electrónica" (null = lo que diga el cliente). Fija la lista de
/// precio del cliente (D8-09) y RE-PRECIA las líneas activas, salvo precio abierto, modificado o de etiqueta de báscula
/// (RN-PRL-02); la respuesta avisa qué líneas cambiaron.
/// </summary>
public sealed record SetCustomerCommand(Guid SaleId, Guid? PartyId, bool? InvoiceRequested = null) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class SetCustomerHandler(SaleEditor editor, CustomerResolver customers, ICatalogSaleItems catalog) : ICommandHandler<SetCustomerCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(SetCustomerCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var sale = loaded.Value.Sale;
        var customer = await customers.ResolveAsync(request.PartyId, sale.BranchId, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error;
        }

        var set = sale.SetCustomer(customer.Value.Snapshot, customer.Value.Pricing, request.InvoiceRequested ?? customer.Value.AlwaysRequestsInvoice);
        if (set.IsFailure)
        {
            return set.Error;
        }

        var warnings = new List<string>();
        foreach (var line in sale.ActiveLines.Where(l => !l.PriceOverridden && !PriceSources.IsFixed(l.PriceSource)).ToList())
        {
            var before = line.UnitPrice;
            if (await catalog.GetAsync(line.ProductId, line.PackagingId, sale.BranchId, sale.PriceListId, cancellationToken) is { UnitPrice: { } price } item
                && sale.RepriceLine(line.Id, price, item.PriceIncludesTax, item.PriceListId, item.PriceSource))
            {
                warnings.Add($"{line.Name}: precio {before:N0} → {price:N0} ({sale.PriceListCode ?? "lista general"}).");
            }
        }

        await editor.RecalculateAsync(sale, cancellationToken);
        return sale.ToDto(warnings);
    }
}

/// <summary>Suspende la venta con una etiqueta (RN-SAL-07): máximo por caja ⚙️; se resuelve antes del cierre.</summary>
public sealed record HoldSaleCommand(Guid SaleId, string? Label) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class HoldSaleHandler(SaleEditor editor, ISalesStore store, ISettingsReader settings, IClock clock) : ICommandHandler<HoldSaleCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(HoldSaleCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var sale = loaded.Value.Sale;
        var max = await settings.GetAsync(SalesSettings.MaxHeldPerTerminal, new SettingContext(sale.CompanyId, sale.BranchId), cancellationToken);
        if (await store.CountHeldAsync(sale.PosTerminalId, cancellationToken) >= max)
        {
            return SalesErrors.HoldLimitReached;
        }

        var held = sale.Hold(request.Label, clock.UtcNow);
        return held.IsSuccess ? sale.ToDto() : held.Error;
    }
}

/// <summary>Recupera una venta suspendida en la misma caja (si no hay otra en curso) y la recalcula con las promociones de ahora.</summary>
public sealed record ResumeSaleCommand(Guid SaleId) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class ResumeSaleHandler(SaleEditor editor, ISalesStore store) : ICommandHandler<ResumeSaleCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(ResumeSaleCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (sale, scope) = loaded.Value;
        if (await store.GetOpenSaleAsync(scope.PosTerminalId, cancellationToken) is { } open && open.Id != sale.Id)
        {
            return Error.Conflict(SalesErrors.OpenSaleExists.Code, $"{SalesErrors.OpenSaleExists.Message} Venta en curso: {open.Id}.");
        }

        var resumed = sale.Resume();
        if (resumed.IsFailure)
        {
            return resumed.Error;
        }

        await editor.RecalculateAsync(sale, cancellationToken);
        return sale.ToDto();
    }
}

/// <summary>Cancela una venta en curso o suspendida (RN-SAL-08): con motivo, sin número; si era de un cambio, lo cancela.</summary>
public sealed record CancelSaleCommand(Guid SaleId, string Reason) : ICommand<SaleDto>, IAllowedWhenRestricted;

internal sealed class CancelSaleHandler(
    SaleEditor editor, ISalesStore store, IAuthorizationScope authorization, IActorContext actor, IAuditWriter audit, IClock clock)
    : ICommandHandler<CancelSaleCommand, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(CancelSaleCommand request, CancellationToken cancellationToken)
    {
        var loaded = await editor.LoadAsync(request.SaleId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var sale = loaded.Value.Sale;
        var now = clock.UtcNow;
        var cancelled = sale.Cancel(request.Reason, actor.ActorId!.Value, authorization.Current?.AuthorizedBy, now);
        if (cancelled.IsFailure)
        {
            return cancelled.Error;
        }

        if (sale.ExchangeId is { } exchangeId && await store.GetReturnAsync(exchangeId, cancellationToken) is { Status: ReturnStatus.Draft } exchange)
        {
            exchange.Cancel(now);
        }

        await audit.WriteAsync(
            new AuditEntry("sales", "SALE_CANCELLED", nameof(Sale), sale.Id, sale.AuditLabel,
                $"Venta cancelada ({sale.ActiveLines.Count()} productos, {sale.Total:N2}): {sale.CancelReason}", AuthorizedBy: authorization.Current?.AuthorizedBy,
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return sale.ToDto();
    }
}
