using FluentValidation;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Catalog.Application;

internal static class MasterMapping
{
    public static CategoryDto ToDto(this Category c) => new(c.Id, c.ParentId, c.Name, c.Level, c.Path, c.SortOrder, c.Status.Db());

    public static BrandDto ToDto(this Brand b) => new(b.Id, b.Name, b.Status.Db());

    public static PriceListDto ToDto(this PriceList l) =>
        new(l.Id, l.Code, l.Name, l.IsDefault, l.PricesIncludeTax, l.Status.Db(), l.AdjustmentPercent, l.RoundingIncrement, l.AllowsPromotions);

    public static BarcodeRuleDto ToDto(this VariableBarcodeRule r) =>
        new(r.Id, r.Prefix, r.Content.Db(), r.PluStart, r.PluLength, r.ValueStart, r.ValueLength, r.ValueDecimals, r.Status.Db());

    public static TaxRateDto ToDto(this TaxRate r) => new(r.Id, r.Rate, r.FixedAmount, r.ValidFrom, r.ValidTo);

    public static TaxDto ToDto(this Tax t, IReadOnlyList<TaxRate> rates, DateOnly today)
    {
        var ordered = rates.Where(r => r.TaxId == t.Id).OrderBy(r => r.ValidFrom).Select(r => r.ToDto()).ToList();
        var current = rates.FirstOrDefault(r => r.TaxId == t.Id && r.IsValidOn(today));
        return new TaxDto(t.Id, t.Code, t.Name, t.Kind.Db(), t.Calculation.Db(), t.IsExempt, t.IsExcluded, t.DianCode, t.IsSystem, t.Status.Db(),
            current?.ToDto(), ordered);
    }
}

// ─────────────────────────────── Unidades ───────────────────────────────

public sealed record ListUnitsQuery : IQuery<IReadOnlyList<UnitDto>>;

internal sealed class ListUnitsHandler(ICatalogQueries queries) : IQueryHandler<ListUnitsQuery, IReadOnlyList<UnitDto>>
{
    public async Task<Result<IReadOnlyList<UnitDto>>> Handle(ListUnitsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await queries.ListUnitsAsync(cancellationToken));
}

// ─────────────────────────────── Categorías ───────────────────────────────

public sealed record ListCategoriesQuery : IQuery<IReadOnlyList<CategoryDto>>;

internal sealed class ListCategoriesHandler(ICatalogStore store) : IQueryHandler<ListCategoriesQuery, IReadOnlyList<CategoryDto>>
{
    public async Task<Result<IReadOnlyList<CategoryDto>>> Handle(ListCategoriesQuery request, CancellationToken cancellationToken) =>
        (await store.GetCategoriesAsync(cancellationToken)).OrderBy(c => c.Path.Length).ThenBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => c.ToDto()).ToList();
}

public sealed record CreateCategoryCommand(string Name, Guid? ParentId, int SortOrder) : ICommand<CategoryDto>;

internal sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(Category.MaxNameLength);
}

internal sealed class CreateCategoryHandler(IInstallationContext installation, ICatalogStore store, IIdGenerator ids)
    : ICommandHandler<CreateCategoryCommand, CategoryDto>
{
    public async Task<Result<CategoryDto>> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return companyId.Error;
        }

        Category? parent = null;
        if (request.ParentId is { } parentId && (parent = await store.GetCategoryAsync(parentId, cancellationToken)) is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        var category = Category.Create(ids.NewId(), companyId.Value, request.Name, parent, request.SortOrder);
        if (category.IsFailure)
        {
            return category.Error;
        }

        store.Add(category.Value);
        return category.Value.ToDto();
    }
}

/// <summary>Renombra y, si cambia el padre, mueve la categoría con todo su subárbol (máximo 4 niveles, sin ciclos).</summary>
public sealed record UpdateCategoryCommand(Guid CategoryId, string Name, Guid? ParentId, int SortOrder) : ICommand<CategoryDto>;

internal sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(Category.MaxNameLength);
}

internal sealed class UpdateCategoryHandler(ICatalogStore store) : ICommandHandler<UpdateCategoryCommand, CategoryDto>
{
    public async Task<Result<CategoryDto>> Handle(UpdateCategoryCommand request, CancellationToken cancellationToken)
    {
        var all = await store.GetCategoriesAsync(cancellationToken);
        var category = all.SingleOrDefault(c => c.Id == request.CategoryId);
        if (category is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        var renamed = category.Rename(request.Name, request.SortOrder);
        if (renamed.IsFailure || request.ParentId == category.ParentId)
        {
            return renamed.IsFailure ? renamed.Error : category.ToDto();
        }

        Category? parent = null;
        if (request.ParentId is { } parentId && (parent = all.SingleOrDefault(c => c.Id == parentId)) is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        var descendants = all.Where(c => c.Id != category.Id && c.Path.StartsWith(category.Path, StringComparison.Ordinal)).ToList();
        var height = descendants.Count == 0 ? 1 : descendants.Max(d => d.Level) - category.Level + 1;
        var oldLevel = category.Level;
        var moved = category.MoveTo(parent, height);
        if (moved.IsFailure)
        {
            return moved.Error;
        }

        foreach (var descendant in descendants)
        {
            descendant.Rebase(moved.Value, category.Path, category.Level - oldLevel);
        }

        return category.ToDto();
    }
}

public sealed record DeleteCategoryCommand(Guid CategoryId) : ICommand;

internal sealed class DeleteCategoryHandler(ICatalogStore store) : ICommandHandler<DeleteCategoryCommand>
{
    public async Task<Result> Handle(DeleteCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = await store.GetCategoryAsync(request.CategoryId, cancellationToken);
        if (category is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        if (await store.CategoryInUseAsync(category.Id, cancellationToken))
        {
            return CatalogErrors.CategoryInUse;
        }

        store.Remove(category);
        return Result.Success();
    }
}

// ─────────────────────────────── Marcas ───────────────────────────────

public sealed record ListBrandsQuery : IQuery<IReadOnlyList<BrandDto>>;

internal sealed class ListBrandsHandler(ICatalogStore store) : IQueryHandler<ListBrandsQuery, IReadOnlyList<BrandDto>>
{
    public async Task<Result<IReadOnlyList<BrandDto>>> Handle(ListBrandsQuery request, CancellationToken cancellationToken) =>
        (await store.GetBrandsAsync(cancellationToken)).OrderBy(b => b.Name, StringComparer.CurrentCulture).Select(b => b.ToDto()).ToList();
}

public sealed record CreateBrandCommand(string Name) : ICommand<BrandDto>;

internal sealed class CreateBrandValidator : AbstractValidator<CreateBrandCommand>
{
    public CreateBrandValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(Brand.MaxNameLength);
}

internal sealed class CreateBrandHandler(IInstallationContext installation, ICatalogStore store, IIdGenerator ids) : ICommandHandler<CreateBrandCommand, BrandDto>
{
    public Task<Result<BrandDto>> Handle(CreateBrandCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return Task.FromResult<Result<BrandDto>>(companyId.Error);
        }

        var brand = Brand.Create(ids.NewId(), companyId.Value, request.Name);
        if (brand.IsSuccess)
        {
            store.Add(brand.Value);
        }

        return Task.FromResult(brand.IsSuccess ? Result.Success(brand.Value.ToDto()) : Result.Failure<BrandDto>(brand.Error));
    }
}

public sealed record UpdateBrandCommand(Guid BrandId, string Name, bool IsActive) : ICommand<BrandDto>;

internal sealed class UpdateBrandHandler(ICatalogStore store) : ICommandHandler<UpdateBrandCommand, BrandDto>
{
    public async Task<Result<BrandDto>> Handle(UpdateBrandCommand request, CancellationToken cancellationToken)
    {
        var brand = await store.GetBrandAsync(request.BrandId, cancellationToken);
        if (brand is null)
        {
            return CatalogErrors.BrandNotFound;
        }

        var renamed = brand.Rename(request.Name);
        if (renamed.IsFailure)
        {
            return renamed.Error;
        }

        if (request.IsActive)
        {
            brand.Activate();
        }
        else
        {
            brand.Deactivate();
        }

        return brand.ToDto();
    }
}

public sealed record DeleteBrandCommand(Guid BrandId) : ICommand;

internal sealed class DeleteBrandHandler(ICatalogStore store) : ICommandHandler<DeleteBrandCommand>
{
    public async Task<Result> Handle(DeleteBrandCommand request, CancellationToken cancellationToken)
    {
        var brand = await store.GetBrandAsync(request.BrandId, cancellationToken);
        if (brand is null)
        {
            return CatalogErrors.BrandNotFound;
        }

        if (await store.BrandInUseAsync(brand.Id, cancellationToken))
        {
            return CatalogErrors.BrandInUse;
        }

        store.Remove(brand);
        return Result.Success();
    }
}

// ─────────────────────────────── Impuestos ───────────────────────────────

public sealed record ListTaxesQuery : IQuery<IReadOnlyList<TaxDto>>;

internal sealed class ListTaxesHandler(ICatalogStore store, IClock clock) : IQueryHandler<ListTaxesQuery, IReadOnlyList<TaxDto>>
{
    public async Task<Result<IReadOnlyList<TaxDto>>> Handle(ListTaxesQuery request, CancellationToken cancellationToken)
    {
        var result = new List<TaxDto>();
        foreach (var tax in (await store.GetTaxesAsync(cancellationToken)).OrderBy(t => t.Code, StringComparer.Ordinal))
        {
            result.Add(tax.ToDto(await store.GetTaxRatesAsync(tax.Id, cancellationToken), clock.Today));
        }

        return result;
    }
}

public sealed record CreateTaxCommand(
    string Code, string Name, TaxKind Kind, TaxCalculation Calculation, bool IsExempt, bool IsExcluded, string? DianCode,
    decimal? Rate, decimal? FixedAmount, DateOnly? ValidFrom) : ICommand<TaxDto>;

internal sealed class CreateTaxHandler(IInstallationContext installation, ICatalogStore store, IIdGenerator ids, IClock clock)
    : ICommandHandler<CreateTaxCommand, TaxDto>
{
    public Task<Result<TaxDto>> Handle(CreateTaxCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return Task.FromResult<Result<TaxDto>>(companyId.Error);
        }

        var tax = Tax.Create(ids.NewId(), companyId.Value, request.Code, request.Name, request.Kind, request.Calculation, request.IsExempt,
            request.IsExcluded, request.DianCode);
        if (tax.IsFailure)
        {
            return Task.FromResult<Result<TaxDto>>(tax.Error);
        }

        store.Add(tax.Value);
        var rates = new List<TaxRate>();
        if (request.Rate is not null || request.FixedAmount is not null)
        {
            var rate = TaxRate.Create(ids.NewId(), tax.Value, request.Rate, request.FixedAmount, request.ValidFrom ?? clock.Today);
            if (rate.IsFailure)
            {
                return Task.FromResult<Result<TaxDto>>(rate.Error);
            }

            store.Add(rate.Value);
            rates.Add(rate.Value);
        }

        return Task.FromResult(Result.Success(tax.Value.ToDto(rates, clock.Today)));
    }
}

public sealed record UpdateTaxCommand(Guid TaxId, string Name, string? DianCode, bool IsActive) : ICommand<TaxDto>;

internal sealed class UpdateTaxHandler(ICatalogStore store, IClock clock) : ICommandHandler<UpdateTaxCommand, TaxDto>
{
    public async Task<Result<TaxDto>> Handle(UpdateTaxCommand request, CancellationToken cancellationToken)
    {
        var tax = await store.GetTaxAsync(request.TaxId, cancellationToken);
        if (tax is null)
        {
            return CatalogErrors.TaxNotFound;
        }

        var updated = tax.Update(request.Name, request.DianCode);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        if (request.IsActive)
        {
            tax.Activate();
        }
        else
        {
            tax.Deactivate();
        }

        return tax.ToDto(await store.GetTaxRatesAsync(tax.Id, cancellationToken), clock.Today);
    }
}

/// <summary>Programa una tarifa desde una fecha: cierra la vigente en esa fecha (o reemplaza la que empieza ese mismo día).</summary>
public sealed record SetTaxRateCommand(Guid TaxId, decimal? Rate, decimal? FixedAmount, DateOnly ValidFrom) : ICommand<TaxDto>;

internal sealed class SetTaxRateHandler(ICatalogStore store, Pos.Application.Abstractions.Data.IUnitOfWork unitOfWork, IIdGenerator ids, IClock clock)
    : ICommandHandler<SetTaxRateCommand, TaxDto>
{
    public async Task<Result<TaxDto>> Handle(SetTaxRateCommand request, CancellationToken cancellationToken)
    {
        var tax = await store.GetTaxAsync(request.TaxId, cancellationToken);
        if (tax is null)
        {
            return CatalogErrors.TaxNotFound;
        }

        var valid = tax.ValidateRate(request.Rate, request.FixedAmount);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var rates = (await store.GetTaxRatesAsync(tax.Id, cancellationToken)).OrderBy(r => r.ValidFrom).ToList();
        var plan = ValidityPlanner.PlanInsert<DateOnly>([.. rates.Select(r => (r.ValidFrom, r.ValidTo))], request.ValidFrom);
        if (plan.ReplaceIndex is { } replace)
        {
            rates[replace].ChangeValue(request.Rate, request.FixedAmount);
        }
        else
        {
            if (plan.CloseIndex is { } close)
            {
                rates[close].CloseAt(request.ValidFrom);

                // La restricción de exclusión exige cerrar la tarifa anterior ANTES de insertar la nueva.
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            var rate = TaxRate.Create(ids.NewId(), tax, request.Rate, request.FixedAmount, request.ValidFrom, plan.NewValidTo).Value;
            store.Add(rate);
            rates.Add(rate);
        }

        return tax.ToDto(rates, clock.Today);
    }
}

// ─────────────────────────────── Listas de precios ───────────────────────────────

public sealed record ListPriceListsQuery : IQuery<IReadOnlyList<PriceListDto>>;

internal sealed class ListPriceListsHandler(ICatalogStore store) : IQueryHandler<ListPriceListsQuery, IReadOnlyList<PriceListDto>>
{
    public async Task<Result<IReadOnlyList<PriceListDto>>> Handle(ListPriceListsQuery request, CancellationToken cancellationToken) =>
        (await store.GetPriceListsAsync(cancellationToken)).OrderByDescending(l => l.IsDefault).ThenBy(l => l.Code, StringComparer.Ordinal)
            .Select(l => l.ToDto()).ToList();
}

/// <summary>Lista de precio. Fase 8: <c>AdjustmentPercent</c> (% sobre la general), redondeo y si admite promociones.</summary>
public sealed record CreatePriceListCommand(
    string Code, string Name, bool PricesIncludeTax, decimal? AdjustmentPercent = null, decimal? RoundingIncrement = null, bool? AllowsPromotions = null)
    : ICommand<PriceListDto>;

internal sealed class CreatePriceListHandler(IInstallationContext installation, ICatalogStore store, IIdGenerator ids)
    : ICommandHandler<CreatePriceListCommand, PriceListDto>
{
    public Task<Result<PriceListDto>> Handle(CreatePriceListCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return Task.FromResult<Result<PriceListDto>>(companyId.Error);
        }

        var list = PriceList.Create(ids.NewId(), companyId.Value, request.Code, request.Name, request.PricesIncludeTax, isDefault: false);
        if (list.IsSuccess)
        {
            var rules = list.Value.ConfigureRules(request.AdjustmentPercent, request.RoundingIncrement ?? 50m, request.AllowsPromotions ?? true);
            if (rules.IsFailure)
            {
                return Task.FromResult(Result.Failure<PriceListDto>(rules.Error));
            }

            store.Add(list.Value);
        }

        return Task.FromResult(list.IsSuccess ? Result.Success(list.Value.ToDto()) : Result.Failure<PriceListDto>(list.Error));
    }
}

public sealed record UpdatePriceListCommand(
    Guid PriceListId, string Name, bool PricesIncludeTax, bool IsDefault, bool IsActive, decimal? AdjustmentPercent = null, decimal? RoundingIncrement = null,
    bool? AllowsPromotions = null) : ICommand<PriceListDto>;

internal sealed class UpdatePriceListHandler(ICatalogStore store, Pos.Application.Abstractions.Data.IUnitOfWork unitOfWork)
    : ICommandHandler<UpdatePriceListCommand, PriceListDto>
{
    public async Task<Result<PriceListDto>> Handle(UpdatePriceListCommand request, CancellationToken cancellationToken)
    {
        var lists = await store.GetPriceListsAsync(cancellationToken);
        var list = lists.SingleOrDefault(l => l.Id == request.PriceListId);
        if (list is null)
        {
            return CatalogErrors.PriceListNotFound;
        }

        var updated = list.Update(request.Name, request.PricesIncludeTax);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        var rules = list.ConfigureRules(
            request.IsDefault ? null : request.AdjustmentPercent, request.RoundingIncrement ?? list.RoundingIncrement, request.AllowsPromotions ?? list.AllowsPromotions);
        if (rules.IsFailure)
        {
            return rules.Error;
        }

        if (request.IsDefault && !list.IsDefault)
        {
            // El índice único parcial exige quitar la marca a la anterior ANTES de ponérsela a la nueva.
            foreach (var other in lists.Where(l => l.IsDefault))
            {
                other.UnsetDefault();
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            list.MakeDefault();
        }

        if (request.IsActive)
        {
            list.Activate();
        }
        else
        {
            var deactivated = list.Deactivate();
            if (deactivated.IsFailure)
            {
                return deactivated.Error;
            }
        }

        return list.ToDto();
    }
}

// ─────────────────────────────── Reglas de báscula ───────────────────────────────

public sealed record ListBarcodeRulesQuery : IQuery<IReadOnlyList<BarcodeRuleDto>>;

internal sealed class ListBarcodeRulesHandler(ICatalogStore store) : IQueryHandler<ListBarcodeRulesQuery, IReadOnlyList<BarcodeRuleDto>>
{
    public async Task<Result<IReadOnlyList<BarcodeRuleDto>>> Handle(ListBarcodeRulesQuery request, CancellationToken cancellationToken) =>
        (await store.GetBarcodeRulesAsync(cancellationToken)).OrderBy(r => r.Prefix, StringComparer.Ordinal).Select(r => r.ToDto()).ToList();
}

public sealed record BarcodeRuleInput(
    string Prefix, VariableBarcodeContent Content, short PluStart, short PluLength, short ValueStart, short ValueLength, short ValueDecimals, bool IsActive);

public sealed record SaveBarcodeRuleCommand(Guid? RuleId, BarcodeRuleInput Rule) : ICommand<BarcodeRuleDto>;

internal sealed class SaveBarcodeRuleHandler(IInstallationContext installation, ICatalogStore store, IIdGenerator ids)
    : ICommandHandler<SaveBarcodeRuleCommand, BarcodeRuleDto>
{
    public async Task<Result<BarcodeRuleDto>> Handle(SaveBarcodeRuleCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return companyId.Error;
        }

        var input = request.Rule;
        VariableBarcodeRule rule;
        if (request.RuleId is { } id)
        {
            var existing = (await store.GetBarcodeRulesAsync(cancellationToken)).SingleOrDefault(r => r.Id == id);
            if (existing is null)
            {
                return CatalogErrors.BarcodeRuleNotFound;
            }

            var configured = existing.Configure(input.Content, input.PluStart, input.PluLength, input.ValueStart, input.ValueLength, input.ValueDecimals);
            if (configured.IsFailure)
            {
                return configured.Error;
            }

            rule = existing;
        }
        else
        {
            var created = VariableBarcodeRule.Create(ids.NewId(), companyId.Value, input.Prefix, input.Content, input.PluStart, input.PluLength,
                input.ValueStart, input.ValueLength, input.ValueDecimals);
            if (created.IsFailure)
            {
                return created.Error;
            }

            rule = created.Value;
            store.Add(rule);
        }

        if (input.IsActive)
        {
            rule.Activate();
        }
        else
        {
            rule.Deactivate();
        }

        return rule.ToDto();
    }
}
