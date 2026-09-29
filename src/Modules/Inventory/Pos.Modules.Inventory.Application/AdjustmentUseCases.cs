using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Files;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.Modules.Organization.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Inventory.Application;

/// <summary>Validaciones comunes de los documentos: bodega de ESTE nodo (D4-04) y productos inventariables.</summary>
public sealed class InventoryGuards(IWarehouseDirectory warehouses, ICatalogReader catalog, IInstallationContext installation)
{
    public async Task<Result<WarehouseInfo>> LocalWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var warehouse = await warehouses.GetAsync(warehouseId, cancellationToken);
        if (warehouse is null)
        {
            return InventoryErrors.WarehouseNotFound;
        }

        if (warehouse.BranchId != installation.BranchId)
        {
            return InventoryErrors.WarehouseNotLocal;
        }

        return warehouse.IsActive ? warehouse : InventoryErrors.WarehouseInactive;
    }

    public async Task<Result<WarehouseInfo>> InTransitWarehouseAsync(Guid branchId, CancellationToken cancellationToken) =>
        (await warehouses.ListByBranchAsync(branchId, cancellationToken)).FirstOrDefault(w => w.Kind == "IN_TRANSIT") is { } warehouse
            ? warehouse
            : InventoryErrors.InTransitWarehouseMissing;

    /// <summary>Productos existentes e inventariables; cantidades enteras si el producto no admite decimales.</summary>
    public async Task<Result<IReadOnlyDictionary<Guid, CatalogProductInfo>>> StockableAsync(
        IReadOnlyCollection<(Guid ProductId, decimal Quantity)> lines, CancellationToken cancellationToken)
    {
        var products = await catalog.GetProductsAsync([.. lines.Select(l => l.ProductId).Distinct()], cancellationToken);
        foreach (var (productId, quantity) in lines)
        {
            if (!products.TryGetValue(productId, out var product))
            {
                return InventoryErrors.ProductNotFound;
            }

            if (!product.IsStockable)
            {
                return InventoryErrors.ServiceProduct;
            }

            if (!product.AllowsDecimalQuantity && decimal.Truncate(quantity) != quantity)
            {
                return InventoryErrors.InvalidQuantity;
            }
        }

        return Result.Success(products);
    }
}

public sealed record AdjustmentLineRequest(Guid ProductId, decimal Quantity, decimal? UnitCost = null, string? Notes = null);

internal static class AdjustmentMapping
{
    public static async Task<AdjustmentDto> ToDtoAsync(this InventoryAdjustment a, IInventoryStore store, ICatalogReader catalog, CancellationToken cancellationToken)
    {
        var reason = await store.GetReasonAsync(a.ReasonId, cancellationToken);
        var products = await catalog.GetProductsAsync([.. a.Lines.Select(l => l.ProductId)], cancellationToken);
        return new AdjustmentDto(
            a.Id, a.Number, a.BranchId, a.WarehouseId, a.BusinessDate, a.ReasonId, reason?.Code ?? string.Empty, a.Status.Db(), a.Notes, a.TotalValue,
            a.ApprovalRequired, a.CreatedBy, a.ApprovedBy, a.PostedAt,
            [.. a.Lines.OrderBy(l => l.LineNumber).Select(l => new AdjustmentLineDto(
                l.Id, l.LineNumber, l.ProductId, products.GetValueOrDefault(l.ProductId)?.Sku ?? string.Empty,
                products.GetValueOrDefault(l.ProductId)?.Name ?? string.Empty, l.Quantity, l.UnitCost, l.Notes))]);
    }
}

/// <summary>Publica un ajuste en el kardex (tipo de movimiento según motivo y signo; saldo inicial con su costo).</summary>
public sealed class AdjustmentPoster(
    IInventoryPosting posting, IStockLedger ledger, ISettingsReader settings, IActorContext actor, IAuditWriter audit, IClock clock)
{
    public async Task<Result> PostAsync(InventoryAdjustment adjustment, AdjustmentReason reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        ArgumentNullException.ThrowIfNull(reason);
        if (reason.Kind == ReasonKind.InitialBalance
            && (await ledger.ProductsWithMovementsAsync(adjustment.WarehouseId, [.. adjustment.Lines.Select(l => l.ProductId)], cancellationToken)).Count > 0)
        {
            return InventoryErrors.InitialBalanceNotAllowed;
        }

        var allowNegative = await settings.GetAsync(
            InventorySettings.AllowNegativeStock, new SettingContext(adjustment.CompanyId, adjustment.BranchId), cancellationToken);
        var lines = adjustment.Lines.Select(l => new PostingLine(
            adjustment.WarehouseId, l.ProductId, MovementRules.ForAdjustment(reason.Kind, l.Quantity), Math.Abs(l.Quantity), l.UnitCost, l.Id, reason.Id))
            .ToList();
        var posted = await posting.PostAsync(
            new InventoryPosting("ADJUSTMENT", adjustment.Id, adjustment.Number, adjustment.BranchId, adjustment.BusinessDate, lines, allowNegative),
            cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        adjustment.MarkPosted(actor.ActorId!.Value, clock.UtcNow);
        await audit.WriteAsync(
            new AuditEntry("inventory", "INVENTORY_ADJUSTMENT_POSTED", nameof(InventoryAdjustment), adjustment.Id, adjustment.AuditLabel,
                $"Ajuste {adjustment.Number} ({reason.Name}) publicado: {lines.Count} productos, valor {posted.Value.Sum(m => m.TotalCost * MovementRules.Direction(m.MovementType)):0.##}.",
                AuthorizedBy: adjustment.ApprovedBy,
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }
}

public sealed record CreateAdjustmentCommand(Guid WarehouseId, Guid ReasonId, DateOnly? BusinessDate, string? Notes, IReadOnlyList<AdjustmentLineRequest> Lines)
    : ICommand<AdjustmentDto>;

internal sealed class CreateAdjustmentValidator : AbstractValidator<CreateAdjustmentCommand>
{
    public CreateAdjustmentValidator()
    {
        RuleFor(x => x.Lines).NotEmpty();
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

internal sealed class CreateAdjustmentHandler(
    IInstallationContext installation,
    IInventoryStore store,
    InventoryGuards guards,
    IStockLedger ledger,
    ICatalogReader catalog,
    IDocumentNumberAllocator numbers,
    IActorContext actor,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CreateAdjustmentCommand, AdjustmentDto>
{
    public async Task<Result<AdjustmentDto>> Handle(CreateAdjustmentCommand request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        var warehouse = await guards.LocalWarehouseAsync(request.WarehouseId, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error;
        }

        var reason = await store.GetReasonAsync(request.ReasonId, cancellationToken);
        if (reason is not { Status: MasterStatus.Active })
        {
            return InventoryErrors.ReasonNotFound;
        }

        var products = await guards.StockableAsync([.. request.Lines.Select(l => (l.ProductId, l.Quantity))], cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        if (reason.Kind == ReasonKind.InitialBalance
            && (await ledger.ProductsWithMovementsAsync(request.WarehouseId, [.. request.Lines.Select(l => l.ProductId)], cancellationToken)).Count > 0)
        {
            return InventoryErrors.InitialBalanceNotAllowed;
        }

        var number = await numbers.NextForBranchAsync("INVENTORY_ADJUSTMENT", local.Value.BranchId, cancellationToken);
        var adjustment = InventoryAdjustment.Create(
            ids.NewId(), local.Value.CompanyId, local.Value.BranchId, request.WarehouseId, number.Number, request.BusinessDate ?? clock.Today, reason,
            request.Notes, [.. request.Lines.Select(l => new AdjustmentLineInput(l.ProductId, l.Quantity, l.UnitCost, l.Notes))], ids.NewId,
            actor.ActorId!.Value);
        if (adjustment.IsFailure)
        {
            return adjustment.Error;
        }

        store.Add(adjustment.Value);
        return await adjustment.Value.ToDtoAsync(store, catalog, cancellationToken);
    }
}

public sealed record UpdateAdjustmentCommand(Guid AdjustmentId, Guid ReasonId, string? Notes, IReadOnlyList<AdjustmentLineRequest> Lines) : ICommand<AdjustmentDto>;

internal sealed class UpdateAdjustmentHandler(IInventoryStore store, InventoryGuards guards, ICatalogReader catalog, IIdGenerator ids)
    : ICommandHandler<UpdateAdjustmentCommand, AdjustmentDto>
{
    public async Task<Result<AdjustmentDto>> Handle(UpdateAdjustmentCommand request, CancellationToken cancellationToken)
    {
        var adjustment = await store.GetAdjustmentAsync(request.AdjustmentId, cancellationToken);
        if (adjustment is null)
        {
            return InventoryErrors.AdjustmentNotFound;
        }

        var reason = await store.GetReasonAsync(request.ReasonId, cancellationToken);
        if (reason is not { Status: MasterStatus.Active })
        {
            return InventoryErrors.ReasonNotFound;
        }

        var products = await guards.StockableAsync([.. (request.Lines ?? []).Select(l => (l.ProductId, l.Quantity))], cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        var edited = adjustment.Edit(reason, request.Notes, [.. (request.Lines ?? []).Select(l => new AdjustmentLineInput(l.ProductId, l.Quantity, l.UnitCost, l.Notes))], ids.NewId);
        return edited.IsSuccess ? await adjustment.ToDtoAsync(store, catalog, cancellationToken) : edited.Error;
    }
}

/// <summary>
/// Solicita publicar: si su valor supera el umbral queda PENDING_APPROVAL (otro usuario aprueba); si no, se publica. El
/// saldo inicial no pasa por el umbral, pero solo lo publica quien tiene <c>inventory.adjustment.approve</c>.
/// </summary>
public sealed record PostAdjustmentCommand(Guid AdjustmentId) : ICommand<AdjustmentDto>;

internal sealed class PostAdjustmentHandler(
    IInventoryStore store,
    IStockLedger ledger,
    AdjustmentPoster poster,
    IPermissionChecker permissions,
    ISettingsReader settings,
    ICatalogReader catalog,
    IClock clock) : ICommandHandler<PostAdjustmentCommand, AdjustmentDto>
{
    public async Task<Result<AdjustmentDto>> Handle(PostAdjustmentCommand request, CancellationToken cancellationToken)
    {
        var adjustment = await store.GetAdjustmentAsync(request.AdjustmentId, cancellationToken);
        if (adjustment is null)
        {
            return InventoryErrors.AdjustmentNotFound;
        }

        var reason = (await store.GetReasonAsync(adjustment.ReasonId, cancellationToken))!;
        var states = await ledger.GetStatesAsync(adjustment.WarehouseId, [.. adjustment.Lines.Select(l => l.ProductId)], cancellationToken);
        var value = adjustment.Lines.Sum(l => Math.Abs(l.Quantity) * (l.UnitCost ?? states.GetValueOrDefault(l.ProductId).AverageCost));
        decimal threshold;
        if (reason.Kind == ReasonKind.InitialBalance)
        {
            if (!await permissions.HasPermissionAsync(InventoryPermissions.AdjustmentApprove, cancellationToken: cancellationToken))
            {
                return Error.Forbidden("AUTH.PERMISSION_DENIED", "Publicar un saldo inicial requiere el permiso inventory.adjustment.approve.");
            }

            threshold = decimal.MaxValue;
        }
        else
        {
            threshold = await settings.GetAsync(InventorySettings.AdjustmentApprovalThreshold, new SettingContext(adjustment.CompanyId), cancellationToken);
        }

        var decision = adjustment.RequestPosting(decimal.Round(value, 2), threshold, clock.UtcNow);
        if (decision.IsFailure)
        {
            return decision.Error;
        }

        if (decision.Value == PostingDecision.ReadyToPost)
        {
            var posted = await poster.PostAsync(adjustment, reason, cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }
        }

        return await adjustment.ToDtoAsync(store, catalog, cancellationToken);
    }
}

/// <summary>Aprueba (otro usuario, RN-INV-04) y publica en la misma transacción.</summary>
public sealed record ApproveAdjustmentCommand(Guid AdjustmentId) : ICommand<AdjustmentDto>;

internal sealed class ApproveAdjustmentHandler(IInventoryStore store, AdjustmentPoster poster, ICatalogReader catalog, IActorContext actor, IClock clock)
    : ICommandHandler<ApproveAdjustmentCommand, AdjustmentDto>
{
    public async Task<Result<AdjustmentDto>> Handle(ApproveAdjustmentCommand request, CancellationToken cancellationToken)
    {
        var adjustment = await store.GetAdjustmentAsync(request.AdjustmentId, cancellationToken);
        if (adjustment is null)
        {
            return InventoryErrors.AdjustmentNotFound;
        }

        var approved = adjustment.Approve(actor.ActorId!.Value, clock.UtcNow);
        if (approved.IsFailure)
        {
            return approved.Error;
        }

        var posted = await poster.PostAsync(adjustment, (await store.GetReasonAsync(adjustment.ReasonId, cancellationToken))!, cancellationToken);
        return posted.IsSuccess ? await adjustment.ToDtoAsync(store, catalog, cancellationToken) : posted.Error;
    }
}

public sealed record CancelAdjustmentCommand(Guid AdjustmentId) : ICommand;

internal sealed class CancelAdjustmentHandler(IInventoryStore store, IActorContext actor, IClock clock) : ICommandHandler<CancelAdjustmentCommand>
{
    public async Task<Result> Handle(CancelAdjustmentCommand request, CancellationToken cancellationToken) =>
        await store.GetAdjustmentAsync(request.AdjustmentId, cancellationToken) is { } adjustment
            ? adjustment.Cancel(actor.ActorId!.Value, clock.UtcNow)
            : InventoryErrors.AdjustmentNotFound;
}

public sealed record GetAdjustmentQuery(Guid AdjustmentId) : IQuery<AdjustmentDto>;

internal sealed class GetAdjustmentHandler(IInventoryStore store, ICatalogReader catalog) : IQueryHandler<GetAdjustmentQuery, AdjustmentDto>
{
    public async Task<Result<AdjustmentDto>> Handle(GetAdjustmentQuery request, CancellationToken cancellationToken) =>
        await store.GetAdjustmentAsync(request.AdjustmentId, cancellationToken) is { } adjustment
            ? await adjustment.ToDtoAsync(store, catalog, cancellationToken)
            : InventoryErrors.AdjustmentNotFound;
}

/// <summary>
/// Saldo inicial desde un archivo (columnas: producto = SKU o código de barras, cantidad, costo). Valida todas las filas;
/// si alguna falla responde con los errores por fila y no crea nada; si no, crea un ajuste en borrador con el motivo
/// INITIAL_BALANCE para revisarlo y publicarlo.
/// </summary>
public sealed record ImportInitialBalanceCommand(Guid WarehouseId, string FileName, Stream Content) : ICommand<AdjustmentDto>;

internal sealed class ImportInitialBalanceHandler(
    IInstallationContext installation,
    ITabularFileReader reader,
    IInventoryStore store,
    ICatalogReader catalog,
    IDispatcher sender) : ICommandHandler<ImportInitialBalanceCommand, AdjustmentDto>
{
    public const int MaxRows = 5000;

    private static readonly string[] ProductColumns = ["producto", "sku", "codigo_barras", "codigo"];

    public async Task<Result<AdjustmentDto>> Handle(ImportInitialBalanceCommand request, CancellationToken cancellationToken)
    {
        if (installation.RequireLocal() is { IsFailure: true } local)
        {
            return local.Error;
        }

        var table = await reader.ReadAsync(request.Content, request.FileName, MaxRows, cancellationToken);
        if (table.IsFailure)
        {
            return table.Error;
        }

        var headers = table.Value.Headers;
        var productColumn = ProductColumns.FirstOrDefault(headers.Contains);
        if (productColumn is null || !headers.Contains("cantidad") || !headers.Contains("costo"))
        {
            return InventoryErrors.ImportMissingColumns;
        }

        var errors = new List<FieldError>();
        var lines = new List<AdjustmentLineRequest>();
        foreach (var row in table.Value.Rows)
        {
            var code = row.Get(productColumn);
            var product = code is null ? null : await catalog.FindByCodeAsync(code, cancellationToken);
            if (product is null)
            {
                errors.Add(new FieldError($"fila {row.Number}", "INVENTORY.PRODUCT_NOT_FOUND", $"Producto '{code}' no encontrado."));
                continue;
            }

            if (!DecimalParsing.TryParse(row.Get("cantidad"), out var quantity) || quantity <= 0m)
            {
                errors.Add(new FieldError($"fila {row.Number}", "INVENTORY.INVALID_QUANTITY", "cantidad: número mayor que cero."));
                continue;
            }

            if (!DecimalParsing.TryParse(row.Get("costo"), out var cost) || cost < 0m)
            {
                errors.Add(new FieldError($"fila {row.Number}", "INVENTORY.UNIT_COST_REQUIRED", "costo: número mayor o igual a cero."));
                continue;
            }

            if (lines.Any(l => l.ProductId == product.Id))
            {
                errors.Add(new FieldError($"fila {row.Number}", "INVENTORY.DUPLICATED_PRODUCT", $"El producto {product.Sku} está repetido."));
                continue;
            }

            lines.Add(new AdjustmentLineRequest(product.Id, quantity, cost));
        }

        if (errors.Count > 0)
        {
            return Error.Validation("INVENTORY.IMPORT_HAS_ERRORS", $"El archivo tiene {errors.Count} filas con errores; no se creó nada.", errors);
        }

        var reason = (await store.GetReasonsAsync(cancellationToken)).SingleOrDefault(r => r.Code == InventoryInitializer.InitialBalance);
        if (reason is null)
        {
            return InventoryErrors.ReasonNotFound;
        }

        return await sender.Send(
            new CreateAdjustmentCommand(request.WarehouseId, reason.Id, null, $"Saldo inicial importado de {Path.GetFileName(request.FileName)}", lines),
            cancellationToken);
    }
}

// ─────────────────────────────── Motivos ───────────────────────────────

public sealed record ListReasonsQuery : IQuery<IReadOnlyList<AdjustmentReasonDto>>;

internal sealed class ListReasonsHandler(IInventoryStore store) : IQueryHandler<ListReasonsQuery, IReadOnlyList<AdjustmentReasonDto>>
{
    public async Task<Result<IReadOnlyList<AdjustmentReasonDto>>> Handle(ListReasonsQuery request, CancellationToken cancellationToken) =>
        (await store.GetReasonsAsync(cancellationToken)).OrderBy(r => r.Code, StringComparer.Ordinal).Select(ToDto).ToList();

    internal static AdjustmentReasonDto ToDto(AdjustmentReason r) => new(r.Id, r.Code, r.Name, r.Kind.Db(), r.RequiresNote, r.IsSystem, r.Status.Db());
}

public sealed record CreateReasonCommand(string Code, string Name, ReasonKind Kind, bool RequiresNote) : ICommand<AdjustmentReasonDto>;

internal sealed class CreateReasonHandler(IInstallationContext installation, IInventoryStore store, IIdGenerator ids)
    : ICommandHandler<CreateReasonCommand, AdjustmentReasonDto>
{
    public Task<Result<AdjustmentReasonDto>> Handle(CreateReasonCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return Task.FromResult<Result<AdjustmentReasonDto>>(InventoryContext.SetupRequired);
        }

        var reason = AdjustmentReason.Create(ids.NewId(), companyId, request.Code, request.Name, request.Kind, request.RequiresNote);
        if (reason.IsSuccess)
        {
            store.Add(reason.Value);
        }

        return Task.FromResult(reason.IsSuccess ? Result.Success(ListReasonsHandler.ToDto(reason.Value)) : Result.Failure<AdjustmentReasonDto>(reason.Error));
    }
}

public sealed record UpdateReasonCommand(Guid ReasonId, string Name, bool RequiresNote, bool IsActive) : ICommand<AdjustmentReasonDto>;

internal sealed class UpdateReasonHandler(IInventoryStore store) : ICommandHandler<UpdateReasonCommand, AdjustmentReasonDto>
{
    public async Task<Result<AdjustmentReasonDto>> Handle(UpdateReasonCommand request, CancellationToken cancellationToken)
    {
        var reason = await store.GetReasonAsync(request.ReasonId, cancellationToken);
        if (reason is null)
        {
            return InventoryErrors.ReasonNotFound;
        }

        if (reason.IsSystem && !request.IsActive && reason.Code is InventoryInitializer.InitialBalance or InventoryInitializer.TransferShortage)
        {
            return InventoryErrors.SystemReason;
        }

        var updated = reason.Update(request.Name, request.RequiresNote, request.IsActive);
        return updated.IsSuccess ? ListReasonsHandler.ToDto(reason) : updated.Error;
    }
}
