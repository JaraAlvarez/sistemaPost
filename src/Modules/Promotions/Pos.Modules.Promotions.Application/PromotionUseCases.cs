using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Promotions.Domain;
using Pos.Modules.Sales.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Promotions.Application;

/// <summary>Promociones (EF Core).</summary>
public interface IPromotionStore
{
    void Add(Promotion promotion);

    Task<Promotion?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Promotion>> ListAsync(PromotionStatus? status, CancellationToken cancellationToken);

    /// <summary>Promociones ACTIVAS cuya vigencia incluye el instante (el horario y la sucursal se filtran en el dominio).</summary>
    Task<IReadOnlyList<Promotion>> ListActiveAsync(DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Reporte de promociones (cruza con las ventas completadas).</summary>
public interface IPromotionReports
{
    Task<IReadOnlyList<PromotionReportRowDto>> GetReportAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

public sealed class PromotionsPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => PromotionsPermissions.All;
}

internal static class PromotionMapping
{
    public static string Db<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }

    public static IReadOnlyList<string> DayNames(PromotionDays days) =>
        [.. Enum.GetValues<DayOfWeek>().Where(d => days.HasFlag(Promotion.DayFlag(d))).OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString().ToUpperInvariant())];

    public static PromotionDays Days(IReadOnlyList<DayOfWeek>? days) =>
        days is not { Count: > 0 } ? PromotionDays.All : days.Aggregate(PromotionDays.None, (mask, d) => mask | Promotion.DayFlag(d));
}

/// <summary>Promociones que rigen ahora en una sucursal, listas para el motor de la venta (D7-16).</summary>
public sealed class ActivePromotions(IPromotionStore store, ICatalogSaleItems catalog, IClock clock) : IActivePromotions
{
    public async Task<IReadOnlyList<PromotionDefinition>> GetActiveAsync(Guid branchId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var local = clock.ToBusinessTime(at).DateTime;
        var promotions = (await store.ListActiveAsync(at, cancellationToken)).Where(p => p.IsInEffect(branchId, at, local)).ToList();
        return await DefinitionsAsync(promotions, catalog, cancellationToken);
    }

    /// <summary>Promoción → definición del motor, con las categorías expandidas a sus subcategorías.</summary>
    public static async Task<IReadOnlyList<PromotionDefinition>> DefinitionsAsync(
        IReadOnlyList<Promotion> promotions, ICatalogSaleItems catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promotions);
        ArgumentNullException.ThrowIfNull(catalog);
        var categories = promotions.SelectMany(p => p.Items).Where(i => i.CategoryId is not null).Select(i => i.CategoryId!.Value).Distinct().ToList();
        var subtrees = categories.Count == 0 ? new Dictionary<Guid, IReadOnlySet<Guid>>() : await catalog.GetCategorySubtreesAsync(categories, cancellationToken);
        return [.. promotions.Select(p => new PromotionDefinition(
            p.Id, p.Name, p.Type.Db(),
            [.. p.Items.Select(i => new PromotionTargetDefinition(
                i.ProductId, i.PackagingId, i.CategoryId is { } c ? subtrees.GetValueOrDefault(c) ?? new HashSet<Guid> { c } : null, i.BrandId, i.Quantity))],
            p.BuyQuantity, p.PayQuantity, p.Price, p.Percent, p.MinQuantity, p.MaxApplications, p.TicketText))];
    }
}

/// <summary>Promoción → DTO con los nombres de productos, categorías y marcas.</summary>
public sealed class PromotionViews(ICatalogReader products, ICatalogSaleItems masters, IClock clock)
{
    public async Task<PromotionDto> ToDtoAsync(Promotion p, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(p);
        var productNames = await products.GetProductsAsync([.. p.Items.Where(i => i.ProductId is not null).Select(i => i.ProductId!.Value)], cancellationToken);
        var names = await masters.GetMasterNamesAsync(
            [.. p.Items.Where(i => i.CategoryId is not null).Select(i => i.CategoryId!.Value)],
            [.. p.Items.Where(i => i.BrandId is not null).Select(i => i.BrandId!.Value)], cancellationToken);
        return new PromotionDto(
            p.Id, p.Number, p.Name, p.Type.Db(), p.Status.Db(), p.ValidFrom, p.ValidTo, PromotionMapping.DayNames(p.Days), p.StartTime, p.EndTime, p.AllBranches,
            [.. p.Branches.Select(b => b.BranchId)], p.MaxApplications, p.TicketText, p.BuyQuantity, p.PayQuantity, p.Price, p.Percent, p.MinQuantity, p.ActivatedAt,
            p.EndedAt,
            [.. p.Items.Select(i => new PromotionItemDto(
                i.Id, i.ProductId, i.ProductId is { } pid ? productNames.GetValueOrDefault(pid)?.Name : null, i.PackagingId, i.CategoryId,
                i.CategoryId is { } cid ? names.GetValueOrDefault(cid) : null, i.BrandId, i.BrandId is { } bid ? names.GetValueOrDefault(bid) : null, i.Quantity))]);
    }

    public PromotionSummaryDto ToSummary(Promotion p, Guid? branchId)
    {
        ArgumentNullException.ThrowIfNull(p);
        var now = clock.UtcNow;
        return new PromotionSummaryDto(
            p.Id, p.Number, p.Name, p.Type.Db(), p.Status.Db(), p.ValidFrom, p.ValidTo,
            branchId is { } branch && p.IsInEffect(branch, now, clock.ToBusinessTime(now).DateTime));
    }
}

/// <summary>Datos de una promoción. <c>Days</c> vacío = todos; <c>BranchIds</c> vacío = todas las sucursales.</summary>
public sealed record PromotionRequest(
    string Name,
    PromotionType Type,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    IReadOnlyList<DayOfWeek>? Days,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    IReadOnlyList<Guid>? BranchIds,
    int? MaxApplications,
    string? TicketText,
    int? BuyQuantity,
    int? PayQuantity,
    decimal? Price,
    decimal? Percent,
    decimal? MinQuantity,
    IReadOnlyList<PromotionItemInput> Items)
{
    public PromotionInput ToInput() => new(
        Name, Type, ValidFrom, ValidTo, PromotionMapping.Days(Days), StartTime, EndTime, BranchIds ?? [], MaxApplications, TicketText,
        new PromotionRuleInput(BuyQuantity, PayQuantity, Price, Percent, MinQuantity), Items ?? []);
}

public sealed record CreatePromotionCommand(PromotionRequest Promotion) : ICommand<PromotionDto>;

internal sealed class CreatePromotionHandler(
    IInstallationContext installation, IPromotionStore store, PromotionViews views, IDocumentNumberAllocator numbers, IIdGenerator ids)
    : ICommandHandler<CreatePromotionCommand, PromotionDto>
{
    public async Task<Result<PromotionDto>> Handle(CreatePromotionCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId || installation.BranchId is not { } branchId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
        }

        var number = await numbers.NextForBranchAsync("PROMOTION", branchId, cancellationToken);
        var promotion = Promotion.Create(ids.NewId(), companyId, number.Number, request.Promotion.ToInput(), ids.NewId);
        if (promotion.IsFailure)
        {
            return promotion.Error;
        }

        store.Add(promotion.Value);
        return await views.ToDtoAsync(promotion.Value, cancellationToken);
    }
}

public sealed record UpdatePromotionCommand(Guid PromotionId, PromotionRequest Promotion) : ICommand<PromotionDto>;

internal sealed class UpdatePromotionHandler(IPromotionStore store, PromotionViews views, IIdGenerator ids) : ICommandHandler<UpdatePromotionCommand, PromotionDto>
{
    public async Task<Result<PromotionDto>> Handle(UpdatePromotionCommand request, CancellationToken cancellationToken)
    {
        var promotion = await store.GetAsync(request.PromotionId, cancellationToken);
        if (promotion is null)
        {
            return PromotionErrors.NotFound;
        }

        var updated = promotion.Update(request.Promotion.ToInput(), ids.NewId);
        return updated.IsSuccess ? await views.ToDtoAsync(promotion, cancellationToken) : updated.Error;
    }
}

public enum PromotionTransition
{
    Activate,
    Pause,
    End,
}

/// <summary>Activa (borrador o pausada), pausa o termina una promoción (RN-PRM-04: una activa no se edita).</summary>
public sealed record ChangePromotionStatusCommand(Guid PromotionId, PromotionTransition Transition) : ICommand<PromotionDto>;

internal sealed class ChangePromotionStatusHandler(IPromotionStore store, PromotionViews views, IActorContext actor, IClock clock)
    : ICommandHandler<ChangePromotionStatusCommand, PromotionDto>
{
    public async Task<Result<PromotionDto>> Handle(ChangePromotionStatusCommand request, CancellationToken cancellationToken)
    {
        var promotion = await store.GetAsync(request.PromotionId, cancellationToken);
        if (promotion is null)
        {
            return PromotionErrors.NotFound;
        }

        var changed = request.Transition switch
        {
            PromotionTransition.Activate => promotion.Activate(actor.ActorId!.Value, clock.UtcNow),
            PromotionTransition.Pause => promotion.Pause(),
            _ => promotion.End(clock.UtcNow),
        };
        return changed.IsSuccess ? await views.ToDtoAsync(promotion, cancellationToken) : changed.Error;
    }
}

/// <summary>
/// Simula una venta de ejemplo con ESTA promoción (aunque esté en borrador) con el mismo motor de la caja: el encargado
/// verifica lo que se cobraría antes de activarla.
/// </summary>
public sealed record SimulatePromotionQuery(Guid PromotionId, IReadOnlyList<SimulationLineRequest> Lines) : IQuery<SimulationDto>;

internal sealed class SimulatePromotionHandler(
    IPromotionStore store, ICatalogSaleItems catalog, IPriceSimulator simulator, IInstallationContext installation)
    : IQueryHandler<SimulatePromotionQuery, SimulationDto>
{
    public async Task<Result<SimulationDto>> Handle(SimulatePromotionQuery request, CancellationToken cancellationToken)
    {
        var promotion = await store.GetAsync(request.PromotionId, cancellationToken);
        if (promotion is null)
        {
            return PromotionErrors.NotFound;
        }

        var definitions = await ActivePromotions.DefinitionsAsync([promotion], catalog, cancellationToken);
        return await simulator.SimulateAsync(installation.BranchId ?? Guid.Empty, request.Lines ?? [], definitions, cancellationToken);
    }
}

public sealed record GetPromotionQuery(Guid PromotionId) : IQuery<PromotionDto>;

internal sealed class GetPromotionHandler(IPromotionStore store, PromotionViews views) : IQueryHandler<GetPromotionQuery, PromotionDto>
{
    public async Task<Result<PromotionDto>> Handle(GetPromotionQuery request, CancellationToken cancellationToken) =>
        await store.GetAsync(request.PromotionId, cancellationToken) is { } promotion ? await views.ToDtoAsync(promotion, cancellationToken) : PromotionErrors.NotFound;
}

public sealed record ListPromotionsQuery(PromotionStatus? Status) : IQuery<IReadOnlyList<PromotionSummaryDto>>;

internal sealed class ListPromotionsHandler(IPromotionStore store, PromotionViews views, IInstallationContext installation)
    : IQueryHandler<ListPromotionsQuery, IReadOnlyList<PromotionSummaryDto>>
{
    public async Task<Result<IReadOnlyList<PromotionSummaryDto>>> Handle(ListPromotionsQuery request, CancellationToken cancellationToken) =>
        (await store.ListAsync(request.Status, cancellationToken)).Select(p => views.ToSummary(p, installation.BranchId)).ToList();
}

/// <summary>Reporte de la sucursal: ventas, unidades y descuento por promoción entre dos fechas de negocio.</summary>
public sealed record PromotionReportQuery(DateOnly From, DateOnly To) : IQuery<IReadOnlyList<PromotionReportRowDto>>;

internal sealed class PromotionReportHandler(IPromotionReports reports, IInstallationContext installation)
    : IQueryHandler<PromotionReportQuery, IReadOnlyList<PromotionReportRowDto>>
{
    public async Task<Result<IReadOnlyList<PromotionReportRowDto>>> Handle(PromotionReportQuery request, CancellationToken cancellationToken)
    {
        if (installation.BranchId is not { } branchId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
        }

        return request.To < request.From || request.To.DayNumber - request.From.DayNumber > 366
            ? Error.Validation("PROMOTIONS.INVALID_RANGE", "Rango de fechas inválido (hasta un año).")
            : Result.Success(await reports.GetReportAsync(branchId, request.From, request.To, cancellationToken));
    }
}
