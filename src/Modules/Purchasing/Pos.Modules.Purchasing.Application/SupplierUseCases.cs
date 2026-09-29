using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Application;

public sealed record SupplierInput(string? Code, int PaymentTermDays, Guid? PreferredPaymentMethodId, decimal? CreditLimit, bool IssuesInvoices, string? Notes);

/// <summary>Validaciones compartidas: medio de pago activo.</summary>
public sealed class PaymentMethodGuard(IPaymentMethodDirectory methods)
{
    public async Task<Result<PaymentMethodInfo>> ActiveAsync(Guid? id, CancellationToken cancellationToken) =>
        id is { } methodId && await methods.GetAsync(methodId, cancellationToken) is { IsActive: true } method
            ? method
            : Error.NotFound("CASH.PAYMENT_METHOD_NOT_FOUND", "El medio de pago no existe o está inactivo.");
}

public sealed record CreateSupplierCommand(Guid PartyId, SupplierInput Supplier) : ICommand<SupplierDto>;

internal sealed class CreateSupplierHandler(
    IInstallationContext installation, IPurchasingStore store, IPartyDirectory parties, PaymentMethodGuard methods, IIdGenerator ids)
    : ICommandHandler<CreateSupplierCommand, SupplierDto>
{
    public async Task<Result<SupplierDto>> Handle(CreateSupplierCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return PurchasingContext.SetupRequired;
        }

        if ((await parties.GetAsync([request.PartyId], cancellationToken)).GetValueOrDefault(request.PartyId) is not { Status: "ACTIVE", IsSystem: false } party)
        {
            return Error.BusinessRule("PARTIES.NOT_AVAILABLE", "El tercero no existe, está inactivo o es el Consumidor final.");
        }

        if (await store.GetSupplierByPartyAsync(request.PartyId, cancellationToken) is not null)
        {
            return PurchasingErrors.SupplierDuplicated;
        }

        if (request.Supplier.PreferredPaymentMethodId is not null
            && await methods.ActiveAsync(request.Supplier.PreferredPaymentMethodId, cancellationToken) is { IsFailure: true } method)
        {
            return method.Error;
        }

        var input = request.Supplier;
        var supplier = Supplier.Create(ids.NewId(), companyId, party.Id, new SupplierData(
            string.IsNullOrWhiteSpace(input.Code) ? Supplier.DefaultCode(party.IdentificationNumber) : input.Code, input.PaymentTermDays,
            input.PreferredPaymentMethodId, input.CreditLimit, input.IssuesInvoices, input.Notes));
        if (supplier.IsFailure)
        {
            return supplier.Error;
        }

        store.Add(supplier.Value);
        return SupplierMapping.ToDto(supplier.Value, party.DisplayName, Identification(party), 0m);
    }

    internal static string Identification(PartyInfo party) =>
        party.CheckDigit is null ? $"{party.IdentificationType} {party.IdentificationNumber}" : $"{party.IdentificationType} {party.IdentificationNumber}-{party.CheckDigit}";
}

internal static class SupplierMapping
{
    public static SupplierDto ToDto(Supplier s, string name, string identification, decimal balance) => new(
        s.Id, s.PartyId, s.Code, name, identification, s.PaymentTermDays, s.PreferredPaymentMethodId, s.CreditLimit, s.IssuesInvoices, s.Notes,
        s.Status.Db(), balance);
}

public sealed record UpdateSupplierCommand(Guid SupplierId, SupplierInput Supplier) : ICommand<SupplierDto>;

internal sealed class UpdateSupplierHandler(IPurchasingStore store, IPurchasingQueries queries, PaymentMethodGuard methods) : ICommandHandler<UpdateSupplierCommand, SupplierDto>
{
    public async Task<Result<SupplierDto>> Handle(UpdateSupplierCommand request, CancellationToken cancellationToken)
    {
        var supplier = await store.GetSupplierAsync(request.SupplierId, cancellationToken);
        if (supplier is null)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        if (request.Supplier.PreferredPaymentMethodId is not null
            && await methods.ActiveAsync(request.Supplier.PreferredPaymentMethodId, cancellationToken) is { IsFailure: true } method)
        {
            return method.Error;
        }

        var input = request.Supplier;
        var updated = supplier.Update(new SupplierData(
            string.IsNullOrWhiteSpace(input.Code) ? supplier.Code : input.Code, input.PaymentTermDays, input.PreferredPaymentMethodId, input.CreditLimit,
            input.IssuesInvoices, input.Notes));
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        var current = await queries.GetSupplierAsync(supplier.Id, cancellationToken);
        return SupplierMapping.ToDto(supplier, current?.Name ?? string.Empty, current?.Identification ?? string.Empty, current?.OpenBalance ?? 0m);
    }
}

public sealed record SetSupplierStatusCommand(Guid SupplierId, SupplierStatus Status) : ICommand<SupplierDto>;

internal sealed class SetSupplierStatusHandler(IPurchasingStore store, IPurchasingQueries queries) : ICommandHandler<SetSupplierStatusCommand, SupplierDto>
{
    public async Task<Result<SupplierDto>> Handle(SetSupplierStatusCommand request, CancellationToken cancellationToken)
    {
        var supplier = await store.GetSupplierAsync(request.SupplierId, cancellationToken);
        if (supplier is null)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        supplier.SetStatus(request.Status);
        var current = await queries.GetSupplierAsync(supplier.Id, cancellationToken);
        return SupplierMapping.ToDto(supplier, current?.Name ?? string.Empty, current?.Identification ?? string.Empty, current?.OpenBalance ?? 0m);
    }
}

public sealed record ListSuppliersQuery(string? Search, bool IncludeInactive) : IQuery<IReadOnlyList<SupplierDto>>;

internal sealed class ListSuppliersHandler(IPurchasingQueries queries) : IQueryHandler<ListSuppliersQuery, IReadOnlyList<SupplierDto>>
{
    public async Task<Result<IReadOnlyList<SupplierDto>>> Handle(ListSuppliersQuery request, CancellationToken cancellationToken) =>
        Result.Success(await queries.ListSuppliersAsync(request.Search, request.IncludeInactive, cancellationToken));
}

public sealed record GetSupplierQuery(Guid SupplierId) : IQuery<SupplierDto>;

internal sealed class GetSupplierHandler(IPurchasingQueries queries) : IQueryHandler<GetSupplierQuery, SupplierDto>
{
    public async Task<Result<SupplierDto>> Handle(GetSupplierQuery request, CancellationToken cancellationToken) =>
        await queries.GetSupplierAsync(request.SupplierId, cancellationToken) is { } supplier ? supplier : PurchasingErrors.SupplierNotFound;
}

// ─────────────────────────────── Productos del proveedor ───────────────────────────────

public sealed record ListSupplierProductsQuery(Guid SupplierId) : IQuery<IReadOnlyList<SupplierProductDto>>;

internal sealed class ListSupplierProductsHandler(IPurchasingStore store, ICatalogReader catalog, IPermissionChecker permissions)
    : IQueryHandler<ListSupplierProductsQuery, IReadOnlyList<SupplierProductDto>>
{
    public async Task<Result<IReadOnlyList<SupplierProductDto>>> Handle(ListSupplierProductsQuery request, CancellationToken cancellationToken)
    {
        if (await store.GetSupplierAsync(request.SupplierId, cancellationToken) is null)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var items = await store.GetSupplierProductsAsync(request.SupplierId, cancellationToken);
        var products = await catalog.GetProductsAsync([.. items.Select(i => i.ProductId)], cancellationToken);
        var costs = await permissions.HasPermissionAsync(InventoryPermissions.CostView, cancellationToken: cancellationToken);
        return items.OrderBy(i => products.GetValueOrDefault(i.ProductId)?.Name, StringComparer.CurrentCulture)
            .Select(i => ToDto(i, products.GetValueOrDefault(i.ProductId), costs)).ToList();
    }

    internal static SupplierProductDto ToDto(SupplierProduct i, CatalogProductInfo? product, bool costs) => new(
        i.Id, i.SupplierId, i.ProductId, product?.Sku ?? string.Empty, product?.Name ?? string.Empty, i.PackagingId, i.SupplierCode,
        costs ? i.LastCost : null, i.LastPurchaseAt, i.LeadTimeDays, i.IsPreferred);
}

/// <summary>Agrega o modifica un producto del proveedor (código propio, presentación habitual, días de entrega).</summary>
public sealed record SetSupplierProductCommand(Guid SupplierId, Guid ProductId, Guid? PackagingId, string? SupplierCode, int? LeadTimeDays, bool IsPreferred)
    : ICommand<SupplierProductDto>;

internal sealed class SetSupplierProductHandler(
    IInstallationContext installation, IPurchasingStore store, ICatalogReader catalog, IPermissionChecker permissions, IIdGenerator ids)
    : ICommandHandler<SetSupplierProductCommand, SupplierProductDto>
{
    public async Task<Result<SupplierProductDto>> Handle(SetSupplierProductCommand request, CancellationToken cancellationToken)
    {
        var supplier = await store.GetSupplierAsync(request.SupplierId, cancellationToken);
        if (supplier is null)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var product = (await catalog.GetProductsAsync([request.ProductId], cancellationToken)).GetValueOrDefault(request.ProductId);
        if (product is null)
        {
            return PurchasingErrors.ProductNotPurchasable;
        }

        if (request.PackagingId is { } packagingId
            && (await catalog.GetPackagingsAsync([request.ProductId], cancellationToken)).All(p => p.Id != packagingId || !p.IsPurchasable))
        {
            return PurchasingErrors.ProductNotPurchasable;
        }

        var existing = (await store.GetSupplierProductsAsync(supplier.Id, cancellationToken)).SingleOrDefault(i => i.ProductId == request.ProductId);
        Result result;
        if (existing is null)
        {
            var created = SupplierProduct.Create(
                ids.NewId(), installation.CompanyId!.Value, supplier.Id, request.ProductId, request.PackagingId, request.SupplierCode, request.LeadTimeDays, request.IsPreferred);
            result = created;
            if (created.IsSuccess)
            {
                store.Add(created.Value);
                existing = created.Value;
            }
        }
        else
        {
            result = existing.Update(request.PackagingId, request.SupplierCode, request.LeadTimeDays, request.IsPreferred);
        }

        if (result.IsFailure)
        {
            return result.Error;
        }

        var costs = await permissions.HasPermissionAsync(InventoryPermissions.CostView, cancellationToken: cancellationToken);
        return ListSupplierProductsHandler.ToDto(existing!, product, costs);
    }
}

public sealed record RemoveSupplierProductCommand(Guid SupplierId, Guid ProductId) : ICommand;

internal sealed class RemoveSupplierProductHandler(IPurchasingStore store) : ICommandHandler<RemoveSupplierProductCommand>
{
    public async Task<Result> Handle(RemoveSupplierProductCommand request, CancellationToken cancellationToken)
    {
        var item = (await store.GetSupplierProductsAsync(request.SupplierId, cancellationToken)).SingleOrDefault(i => i.ProductId == request.ProductId);
        if (item is null)
        {
            return PurchasingErrors.SupplierProductNotFound;
        }

        store.Remove(item);
        return Result.Success();
    }
}
