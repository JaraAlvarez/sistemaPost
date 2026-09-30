using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Purchasing.Application;

// Fase 8, bloque 8.4 (D8-14): ficha resumen, costos por producto, agenda y pedido mínimo, cuentas bancarias con
// verificación (RN-PUR-09), retenciones sugeridas (RN-PUR-10) y vencimientos próximos.

// ─────────────────────────────── Ficha resumen ───────────────────────────────

/// <summary>Ficha resumen del proveedor. Sin fechas: los últimos 90 días hasta hoy.</summary>
public sealed record GetSupplierSummaryQuery(Guid SupplierId, DateOnly? From, DateOnly? To) : IQuery<SupplierSummaryDto>;

internal sealed class GetSupplierSummaryHandler(IPurchasingStore store, IPurchasingQueries queries, IClock clock)
    : IQueryHandler<GetSupplierSummaryQuery, SupplierSummaryDto>
{
    public const int DefaultDays = 90;

    public async Task<Result<SupplierSummaryDto>> Handle(GetSupplierSummaryQuery request, CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var to = request.To ?? today;
        var from = request.From ?? to.AddDays(-(DefaultDays - 1));
        if (from > to || to.DayNumber - from.DayNumber > 3 * 366)
        {
            return PurchasingErrors.InvalidPeriod;
        }

        if (await store.GetSupplierAsync(request.SupplierId, cancellationToken) is not { } supplier
            || await queries.GetSupplierAsync(supplier.Id, cancellationToken) is not { } info)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var activity = await queries.GetSupplierActivityAsync(supplier.Id, from, to, cancellationToken);
        var open = await queries.ListPayablesAsync(supplier.Id, openOnly: true, today, withEntries: false, cancellationToken);
        var overdue = open.Where(p => p.DaysOverdue > 0).ToList();
        var primary = (await store.GetBankAccountsAsync(supplier.Id, cancellationToken)).FirstOrDefault(a => a.IsPrimary && a.IsActive);
        return new SupplierSummaryDto(
            supplier.Id, supplier.Code, info.Name, info.Status, from, to, activity.PurchasedTotal, activity.PurchaseCount, activity.LastPurchaseDate,
            activity.LastPurchaseTotal, activity.ReturnsTotal, activity.ReturnCount, info.OpenBalance, overdue.Sum(p => p.Balance), overdue.Count,
            activity.ActiveProducts, supplier.PaymentTermDays, supplier.MinimumOrderAmount, primary?.Status.Db());
    }
}

// ─────────────────────────────── Costos por producto ───────────────────────────────

/// <summary>"¿Quién me vende esto?": preferido primero, luego el menor último costo.</summary>
public sealed record ListProductSuppliersQuery(Guid ProductId) : IQuery<IReadOnlyList<ProductSupplierDto>>;

internal sealed class ListProductSuppliersHandler(IPurchasingQueries queries, ICatalogReader catalog, IPermissionChecker permissions)
    : IQueryHandler<ListProductSuppliersQuery, IReadOnlyList<ProductSupplierDto>>
{
    public async Task<Result<IReadOnlyList<ProductSupplierDto>>> Handle(ListProductSuppliersQuery request, CancellationToken cancellationToken)
    {
        if (!(await catalog.GetProductsAsync([request.ProductId], cancellationToken)).ContainsKey(request.ProductId))
        {
            return PurchasingErrors.ProductNotFound;
        }

        var costs = await permissions.HasPermissionAsync(InventoryPermissions.CostView, cancellationToken: cancellationToken);
        var rows = await queries.ListProductSuppliersAsync(request.ProductId, cancellationToken);
        return rows.OrderByDescending(r => r.IsPreferred).ThenBy(r => r.LastCost ?? decimal.MaxValue).ThenBy(r => r.SupplierName, StringComparer.CurrentCulture)
            .Select(r => costs ? r : r with { LastCost = null })
            .ToList();
    }
}

/// <summary>Historial de costo neto del producto por compra y proveedor (el endpoint exige inventory.cost.view).</summary>
public sealed record GetCostHistoryQuery(Guid ProductId, Guid? SupplierId, DateOnly? From, DateOnly? To, int? Limit) : IQuery<IReadOnlyList<CostHistoryEntryDto>>;

internal sealed class GetCostHistoryHandler(IPurchasingQueries queries, ICatalogReader catalog)
    : IQueryHandler<GetCostHistoryQuery, IReadOnlyList<CostHistoryEntryDto>>
{
    public async Task<Result<IReadOnlyList<CostHistoryEntryDto>>> Handle(GetCostHistoryQuery request, CancellationToken cancellationToken)
    {
        if (request.From > request.To)
        {
            return PurchasingErrors.InvalidPeriod;
        }

        if (!(await catalog.GetProductsAsync([request.ProductId], cancellationToken)).ContainsKey(request.ProductId))
        {
            return PurchasingErrors.ProductNotFound;
        }

        return Result.Success(await queries.ListCostHistoryAsync(
            request.ProductId, request.SupplierId, request.From, request.To, Math.Clamp(request.Limit ?? 100, 1, 500), cancellationToken));
    }
}

// ─────────────────────────────── Agenda y pedido mínimo ───────────────────────────────

public sealed record GetSupplierScheduleQuery(Guid SupplierId) : IQuery<SupplierScheduleDto>;

internal sealed class GetSupplierScheduleHandler(IPurchasingStore store, IClock clock) : IQueryHandler<GetSupplierScheduleQuery, SupplierScheduleDto>
{
    public async Task<Result<SupplierScheduleDto>> Handle(GetSupplierScheduleQuery request, CancellationToken cancellationToken) =>
        await store.GetSupplierAsync(request.SupplierId, cancellationToken) is { } supplier
            ? ScheduleMapping.ToDto(supplier, await store.GetSchedulesAsync(supplier.Id, cancellationToken), clock.Today)
            : PurchasingErrors.SupplierNotFound;
}

/// <summary>Reemplaza la agenda completa del proveedor y fija el pedido mínimo y la nota de hora de corte.</summary>
public sealed record SetSupplierScheduleCommand(Guid SupplierId, decimal? MinimumOrderAmount, string? OrderCutoffNote, IReadOnlyList<ScheduleEntryInput>? Entries)
    : ICommand<SupplierScheduleDto>;

internal sealed class SetSupplierScheduleHandler(IPurchasingStore store, IIdGenerator ids, IClock clock) : ICommandHandler<SetSupplierScheduleCommand, SupplierScheduleDto>
{
    public async Task<Result<SupplierScheduleDto>> Handle(SetSupplierScheduleCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetSupplierAsync(request.SupplierId, cancellationToken) is not { } supplier)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var entries = request.Entries ?? [];
        var valid = SupplierSchedule.Validate(entries);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var terms = supplier.SetOrderingTerms(request.MinimumOrderAmount, request.OrderCutoffNote);
        if (terms.IsFailure)
        {
            return terms.Error;
        }

        var current = (await store.GetSchedulesAsync(supplier.Id, cancellationToken)).ToList();
        foreach (var existing in current.Where(c => !entries.Any(c.Matches)).ToList())
        {
            store.Remove(existing);
            current.Remove(existing);
        }

        foreach (var entry in entries)
        {
            if (current.FirstOrDefault(c => c.Matches(entry)) is { } kept)
            {
                kept.SetNotes(entry.Notes);
            }
            else
            {
                var created = SupplierSchedule.Create(ids.NewId(), supplier.CompanyId, supplier.Id, entry);
                store.Add(created);
                current.Add(created);
            }
        }

        return ScheduleMapping.ToDto(supplier, current, clock.Today);
    }
}

internal static class ScheduleMapping
{
    public static SupplierScheduleDto ToDto(Supplier supplier, IEnumerable<SupplierSchedule> entries, DateOnly today) => new(
        supplier.Id, supplier.MinimumOrderAmount, supplier.OrderCutoffNote,
        [.. entries.OrderBy(e => e.Kind).ThenBy(e => e.DayOfWeek).ThenBy(e => e.BranchId)
            .Select(e => new ScheduleEntryDto(e.Id, e.DayOfWeek, SupplierSchedule.DayName(e.DayOfWeek), e.Kind.Db(), e.BranchId, e.Notes, e.NextOn(today)))]);
}

// ─────────────────────────────── Retenciones sugeridas ───────────────────────────────

public sealed record WithholdingDefaultRequest(WithholdingKind Kind, decimal Rate, string? Concept);

public sealed record GetSupplierWithholdingsQuery(Guid SupplierId) : IQuery<IReadOnlyList<SupplierWithholdingDefaultDto>>;

internal sealed class GetSupplierWithholdingsHandler(IPurchasingStore store) : IQueryHandler<GetSupplierWithholdingsQuery, IReadOnlyList<SupplierWithholdingDefaultDto>>
{
    public async Task<Result<IReadOnlyList<SupplierWithholdingDefaultDto>>> Handle(GetSupplierWithholdingsQuery request, CancellationToken cancellationToken) =>
        await store.GetSupplierAsync(request.SupplierId, cancellationToken) is { } supplier
            ? Result.Success(WithholdingMapping.ToDto(await store.GetWithholdingDefaultsAsync(supplier.Id, cancellationToken)))
            : PurchasingErrors.SupplierNotFound;
}

/// <summary>Reemplaza las retenciones sugeridas del proveedor (una por tipo). Pre-llenan las compras nuevas (RN-PUR-10).</summary>
public sealed record SetSupplierWithholdingsCommand(Guid SupplierId, IReadOnlyList<WithholdingDefaultRequest>? Withholdings)
    : ICommand<IReadOnlyList<SupplierWithholdingDefaultDto>>;

internal sealed class SetSupplierWithholdingsHandler(IPurchasingStore store, IIdGenerator ids)
    : ICommandHandler<SetSupplierWithholdingsCommand, IReadOnlyList<SupplierWithholdingDefaultDto>>
{
    public async Task<Result<IReadOnlyList<SupplierWithholdingDefaultDto>>> Handle(SetSupplierWithholdingsCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetSupplierAsync(request.SupplierId, cancellationToken) is not { } supplier)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var requested = request.Withholdings ?? [];
        if (requested.Any(w => w is null || !Enum.IsDefined(w.Kind)) || requested.Select(w => w.Kind).Distinct().Count() != requested.Count)
        {
            return PurchasingErrors.InvalidWithholdingDefault;
        }

        var current = (await store.GetWithholdingDefaultsAsync(supplier.Id, cancellationToken)).ToList();
        foreach (var removed in current.Where(c => requested.All(r => r.Kind != c.Kind)).ToList())
        {
            store.Remove(removed);
            current.Remove(removed);
        }

        foreach (var item in requested)
        {
            Result result;
            if (current.FirstOrDefault(c => c.Kind == item.Kind) is { } existing)
            {
                result = existing.Update(item.Rate, item.Concept);
            }
            else
            {
                var created = SupplierWithholdingDefault.Create(ids.NewId(), supplier.CompanyId, supplier.Id, item.Kind, item.Rate, item.Concept);
                result = created;
                if (created.IsSuccess)
                {
                    store.Add(created.Value);
                    current.Add(created.Value);
                }
            }

            if (result.IsFailure)
            {
                return result.Error;
            }
        }

        return Result.Success(WithholdingMapping.ToDto(current));
    }
}

internal static class WithholdingMapping
{
    public static IReadOnlyList<SupplierWithholdingDefaultDto> ToDto(IEnumerable<SupplierWithholdingDefault> items) =>
        [.. items.OrderBy(i => i.Kind).Select(i => new SupplierWithholdingDefaultDto(i.Kind.Db(), i.Rate, i.Concept))];
}

// ─────────────────────────────── Cuentas bancarias (RN-PUR-09) ───────────────────────────────

/// <summary>Cuenta bancaria. Sin <c>IsActive</c> la cuenta queda activa; <c>IsActive = false</c> la inactiva.</summary>
public sealed record BankAccountRequest(
    string BankCode,
    BankAccountType AccountType,
    string AccountNumber,
    string HolderName,
    string HolderIdentificationType,
    string HolderIdentificationNumber,
    bool IsPrimary,
    bool IsActive = true)
{
    public BankAccountData Data => new(BankCode, AccountType, AccountNumber, HolderName, HolderIdentificationType, HolderIdentificationNumber);
}

public sealed record ListBanksQuery(bool IncludeInactive) : IQuery<IReadOnlyList<BankDto>>;

internal sealed class ListBanksHandler(IPurchasingQueries queries) : IQueryHandler<ListBanksQuery, IReadOnlyList<BankDto>>
{
    public async Task<Result<IReadOnlyList<BankDto>>> Handle(ListBanksQuery request, CancellationToken cancellationToken) =>
        Result.Success(await queries.ListBanksAsync(request.IncludeInactive, cancellationToken));
}

public sealed record ListSupplierBankAccountsQuery(Guid SupplierId) : IQuery<IReadOnlyList<SupplierBankAccountDto>>;

internal sealed class ListSupplierBankAccountsHandler(IPurchasingStore store, IPurchasingQueries queries)
    : IQueryHandler<ListSupplierBankAccountsQuery, IReadOnlyList<SupplierBankAccountDto>>
{
    public async Task<Result<IReadOnlyList<SupplierBankAccountDto>>> Handle(ListSupplierBankAccountsQuery request, CancellationToken cancellationToken)
    {
        if (await store.GetSupplierAsync(request.SupplierId, cancellationToken) is not { } supplier)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var accounts = await store.GetBankAccountsAsync(supplier.Id, cancellationToken);
        var banks = await BankAccountMapping.BankNamesAsync(queries, cancellationToken);
        return accounts.OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Status).ThenBy(a => a.BankCode, StringComparer.Ordinal)
            .Select(a => BankAccountMapping.ToDto(a, banks)).ToList();
    }
}

public sealed record CreateSupplierBankAccountCommand(Guid SupplierId, BankAccountRequest Account) : ICommand<SupplierBankAccountDto>;

internal sealed class CreateSupplierBankAccountHandler(
    IPurchasingStore store, IPurchasingQueries queries, IAuthorizationScope authorization, IActorContext actor, IAuditWriter audit, IIdGenerator ids, IClock clock)
    : ICommandHandler<CreateSupplierBankAccountCommand, SupplierBankAccountDto>
{
    public async Task<Result<SupplierBankAccountDto>> Handle(CreateSupplierBankAccountCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Account);
        if (await store.GetSupplierAsync(request.SupplierId, cancellationToken) is not { } supplier)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var banks = await BankAccountMapping.BankNamesAsync(queries, cancellationToken);
        var existing = await store.GetBankAccountsAsync(supplier.Id, cancellationToken);
        var responsible = authorization.Current?.AuthorizedBy ?? actor.ActorId!.Value;
        var isPrimary = request.Account.IsPrimary || !existing.Any(a => a.IsPrimary && a.IsActive);
        var created = SupplierBankAccount.Create(ids.NewId(), supplier.CompanyId, supplier.Id, request.Account.Data, isPrimary, responsible, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var account = created.Value;
        var checkedAccount = BankAccountMapping.Check(account, existing, banks);
        if (checkedAccount.IsFailure)
        {
            return checkedAccount.Error;
        }

        if (!request.Account.IsActive)
        {
            account.Update(request.Account.Data, isActive: false, isPrimary: false, responsible, clock.UtcNow);
        }

        BankAccountMapping.ClearOtherPrimaries(account, existing);
        store.Add(account);
        await audit.WriteAsync(
            new AuditEntry("purchasing", "SUPPLIER_BANK_ACCOUNT_CREATED", nameof(SupplierBankAccount), account.Id, account.AuditLabel,
                $"Cuenta {BankAccountMapping.Describe(account, banks)} registrada para el proveedor {supplier.Code}: queda POR VERIFICAR"
                + (account.IsPrimary ? " y es la principal." : "."),
                NewValues: BankAccountMapping.AuditValues(account), AuthorizedBy: authorization.Current?.AuthorizedBy, Severity: AuditSeverity.Critical),
            cancellationToken);
        return BankAccountMapping.ToDto(account, banks);
    }
}

public sealed record UpdateSupplierBankAccountCommand(Guid SupplierId, Guid AccountId, BankAccountRequest Account) : ICommand<SupplierBankAccountDto>;

internal sealed class UpdateSupplierBankAccountHandler(
    IPurchasingStore store, IPurchasingQueries queries, IAuthorizationScope authorization, IActorContext actor, IAuditWriter audit, IClock clock)
    : ICommandHandler<UpdateSupplierBankAccountCommand, SupplierBankAccountDto>
{
    public async Task<Result<SupplierBankAccountDto>> Handle(UpdateSupplierBankAccountCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Account);
        if (await store.GetSupplierAsync(request.SupplierId, cancellationToken) is not { } supplier)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var accounts = await store.GetBankAccountsAsync(supplier.Id, cancellationToken);
        if (accounts.FirstOrDefault(a => a.Id == request.AccountId) is not { } account)
        {
            return PurchasingErrors.BankAccountNotFound;
        }

        var banks = await BankAccountMapping.BankNamesAsync(queries, cancellationToken);
        var before = BankAccountMapping.AuditValues(account);
        var responsible = authorization.Current?.AuthorizedBy ?? actor.ActorId!.Value;
        var changed = account.Update(request.Account.Data, request.Account.IsActive, request.Account.IsPrimary, responsible, clock.UtcNow);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        var checkedAccount = BankAccountMapping.Check(account, accounts, banks);
        if (checkedAccount.IsFailure)
        {
            return checkedAccount.Error;
        }

        BankAccountMapping.ClearOtherPrimaries(account, accounts);
        if (changed.Value.Any)
        {
            var what = changed.Value.Details ? "datos modificados: queda POR VERIFICAR" : $"estado {account.Status.Db()}";
            await audit.WriteAsync(
                new AuditEntry("purchasing", "SUPPLIER_BANK_ACCOUNT_CHANGED", nameof(SupplierBankAccount), account.Id, account.AuditLabel,
                    $"Cuenta {BankAccountMapping.Describe(account, banks)} del proveedor {supplier.Code}: {what}"
                    + (changed.Value.Primary ? (account.IsPrimary ? "; ahora es la principal." : "; deja de ser la principal.") : "."),
                    OldValues: before, NewValues: BankAccountMapping.AuditValues(account), AuthorizedBy: authorization.Current?.AuthorizedBy,
                    Severity: AuditSeverity.Critical),
                cancellationToken);
        }

        return BankAccountMapping.ToDto(account, banks);
    }
}

/// <summary>RN-PUR-09: otro usuario con el permiso confirma la cuenta (p. ej. por teléfono con el contacto de cartera del proveedor).</summary>
public sealed record VerifySupplierBankAccountCommand(Guid SupplierId, Guid AccountId) : ICommand<SupplierBankAccountDto>;

internal sealed class VerifySupplierBankAccountHandler(
    IPurchasingStore store, IPurchasingQueries queries, IAuthorizationScope authorization, IActorContext actor, IAuditWriter audit, IClock clock)
    : ICommandHandler<VerifySupplierBankAccountCommand, SupplierBankAccountDto>
{
    public async Task<Result<SupplierBankAccountDto>> Handle(VerifySupplierBankAccountCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetSupplierAsync(request.SupplierId, cancellationToken) is not { } supplier)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        if ((await store.GetBankAccountsAsync(supplier.Id, cancellationToken)).FirstOrDefault(a => a.Id == request.AccountId) is not { } account)
        {
            return PurchasingErrors.BankAccountNotFound;
        }

        // Quien pide la verificación tampoco puede ser quien hizo el cambio (aunque la autorice otro supervisor).
        var verifier = authorization.Current?.AuthorizedBy ?? actor.ActorId!.Value;
        if (actor.ActorId == account.ChangedBy)
        {
            return PurchasingErrors.BankAccountSameUser;
        }

        var verified = account.Verify(verifier, clock.UtcNow);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        var banks = await BankAccountMapping.BankNamesAsync(queries, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("purchasing", "SUPPLIER_BANK_ACCOUNT_VERIFIED", nameof(SupplierBankAccount), account.Id, account.AuditLabel,
                $"Cuenta {BankAccountMapping.Describe(account, banks)} del proveedor {supplier.Code} VERIFICADA.",
                NewValues: BankAccountMapping.AuditValues(account), AuthorizedBy: authorization.Current?.AuthorizedBy, Severity: AuditSeverity.Critical),
            cancellationToken);
        return BankAccountMapping.ToDto(account, banks);
    }
}

internal static class BankAccountMapping
{
    public static async Task<IReadOnlyDictionary<string, BankDto>> BankNamesAsync(IPurchasingQueries queries, CancellationToken cancellationToken) =>
        (await queries.ListBanksAsync(includeInactive: true, cancellationToken)).ToDictionary(b => b.Code, StringComparer.Ordinal);

    /// <summary>El banco existe y (si la cuenta cambió de banco o es nueva) está activo; banco + número no se repiten en el proveedor.</summary>
    public static Result Check(SupplierBankAccount account, IEnumerable<SupplierBankAccount> others, IReadOnlyDictionary<string, BankDto> banks)
    {
        if (!banks.ContainsKey(account.BankCode))
        {
            return PurchasingErrors.BankNotFound;
        }

        return others.Any(o => o.Id != account.Id && o.BankCode == account.BankCode && o.AccountNumber == account.AccountNumber)
            ? PurchasingErrors.BankAccountDuplicated
            : Result.Success();
    }

    public static void ClearOtherPrimaries(SupplierBankAccount account, IEnumerable<SupplierBankAccount> others)
    {
        if (!account.IsPrimary)
        {
            return;
        }

        foreach (var other in others.Where(o => o.Id != account.Id && o.IsPrimary))
        {
            other.ClearPrimary();
        }
    }

    public static string Describe(SupplierBankAccount a, IReadOnlyDictionary<string, BankDto> banks) =>
        $"{banks.GetValueOrDefault(a.BankCode)?.Name ?? a.BankCode} {a.AccountType.Db()} {a.MaskedNumber} a nombre de {a.HolderName} "
        + $"({a.HolderIdentificationType} {a.HolderIdentificationNumber})";

    /// <summary>Valores para la auditoría con el número enmascarado.</summary>
    public static IReadOnlyDictionary<string, object?> AuditValues(SupplierBankAccount a) => new Dictionary<string, object?>
    {
        ["BankCode"] = a.BankCode,
        ["AccountType"] = a.AccountType.Db(),
        ["AccountNumber"] = a.MaskedNumber,
        ["HolderName"] = a.HolderName,
        ["HolderIdentification"] = $"{a.HolderIdentificationType} {a.HolderIdentificationNumber}",
        ["Status"] = a.Status.Db(),
        ["IsPrimary"] = a.IsPrimary,
    };

    public static SupplierBankAccountDto ToDto(SupplierBankAccount a, IReadOnlyDictionary<string, BankDto> banks) => new(
        a.Id, a.SupplierId, a.BankCode, banks.GetValueOrDefault(a.BankCode)?.Name ?? a.BankCode, a.AccountType.Db(), a.AccountNumber, a.MaskedNumber, a.HolderName,
        a.HolderIdentificationType, a.HolderIdentificationNumber, a.Status.Db(), a.IsPrimary, a.ChangedAt, a.ChangedBy, a.VerifiedAt, a.VerifiedBy);

    /// <summary>
    /// Advertencia del pago (no bloquea): la cuenta principal del proveedor está por verificar. Solo para medios distintos
    /// del efectivo (el dinero va a una cuenta).
    /// </summary>
    public static async Task<IReadOnlyList<OperationWarningDto>> PaymentWarningsAsync(
        IPurchasingStore store, Guid supplierId, string methodKind, CancellationToken cancellationToken)
    {
        if (string.Equals(methodKind, "CASH", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var primary = (await store.GetBankAccountsAsync(supplierId, cancellationToken)).FirstOrDefault(a => a.IsPrimary && a.IsActive);
        return primary is { Status: BankAccountStatus.PendingVerification }
            ? [new OperationWarningDto(
                "PURCHASING.BANK_ACCOUNT_UNVERIFIED",
                $"La cuenta principal del proveedor ({primary.BankCode} {primary.MaskedNumber}) está POR VERIFICAR: confírmela con el proveedor antes de transferir.")]
            : [];
    }
}

// ─────────────────────────────── Vencimientos próximos ───────────────────────────────

/// <summary>Cuentas vencidas y las que vencen en los próximos <c>Days</c> días (por defecto 7).</summary>
public sealed record GetDuePayablesQuery(int? Days) : IQuery<DuePayablesDto>;

internal sealed class GetDuePayablesHandler(IPurchasingQueries queries, IClock clock) : IQueryHandler<GetDuePayablesQuery, DuePayablesDto>
{
    public async Task<Result<DuePayablesDto>> Handle(GetDuePayablesQuery request, CancellationToken cancellationToken)
    {
        var days = request.Days ?? 7;
        if (days is < 0 or > 90)
        {
            return PurchasingErrors.InvalidPeriod;
        }

        var today = clock.Today;
        var open = await queries.ListPayablesAsync(null, openOnly: true, today, withEntries: false, cancellationToken);
        var overdue = open.Where(p => p.DueDate < today).OrderBy(p => p.DueDate).ToList();
        var soon = open.Where(p => p.DueDate >= today && p.DueDate <= today.AddDays(days)).OrderBy(p => p.DueDate).ToList();
        return new DuePayablesDto(today, days, overdue.Sum(p => p.Balance), soon.Sum(p => p.Balance), overdue, soon);
    }
}
