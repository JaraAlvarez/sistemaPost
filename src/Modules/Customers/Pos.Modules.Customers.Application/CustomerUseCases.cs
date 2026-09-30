using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Customers.Contracts;
using Pos.Modules.Customers.Domain;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Customers.Application;

/// <summary>Autorización pedida en la API (alta rápida o registro posterior). <c>Channel</c> por defecto: verbal en la caja.</summary>
public sealed record ConsentRequest(ConsentPurpose Purpose, bool Granted, ConsentChannel? Channel = null, IReadOnlyList<string>? MarketingChannels = null,
    string? Evidence = null);

/// <summary>Servicios comunes: ficha del cliente, rol, política vigente y registro de autorizaciones.</summary>
public sealed class CustomerService(
    ICustomerStore store, IPartyRegistry parties, ICurrentUser current, IActorContext actor, IInstallationContext installation, IIdGenerator ids, IClock clock)
{
    public async Task<Result<CustomerDto>> DtoAsync(Customer customer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customer);
        var profile = (await parties.GetProfilesAsync([customer.PartyId], cancellationToken)).GetValueOrDefault(customer.PartyId);
        if (profile is null)
        {
            return CustomerErrors.NotFound;
        }

        var groups = await store.GetGroupsAsync(cancellationToken);
        return customer.ToDto(profile, groups.FirstOrDefault(g => g.Id == customer.GroupId));
    }

    /// <summary>Rol cliente del tercero; lo crea si no existe (con el origen indicado y el grupo por defecto).</summary>
    public async Task<Result<Customer>> EnsureAsync(Guid partyId, CustomerOrigin origin, CancellationToken cancellationToken)
    {
        if (await store.GetCustomerAsync(partyId, cancellationToken) is { } existing)
        {
            return existing;
        }

        if (installation.CompanyId is not { } companyId)
        {
            return CustomersMapping.SetupRequired;
        }

        var group = (await store.GetGroupsAsync(cancellationToken)).Single(g => g.IsDefault);
        var customer = Customer.Create(partyId, companyId, group.Id, origin, current.BranchId ?? installation.BranchId);
        store.Add(customer);
        return customer;
    }

    /// <summary>Política con la que se registra una autorización: la activa o, si aún no hay, la plantilla pendiente de revisión.</summary>
    public async Task<PrivacyPolicy?> CurrentPolicyAsync(CancellationToken cancellationToken)
    {
        var policies = await store.GetPoliciesAsync(cancellationToken);
        return policies.FirstOrDefault(p => p.Status == PolicyStatus.Active)
            ?? policies.Where(p => p.Status == PolicyStatus.PendingReview).OrderByDescending(p => p.Version).FirstOrDefault();
    }

    /// <summary>Registra autorizaciones (libro de solo inserción, D8-06) y recalcula el estado vigente del cliente.</summary>
    public async Task<Result> RecordConsentsAsync(Customer customer, IReadOnlyList<ConsentRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return Result.Success();
        }

        if (customer.AnonymizedAt is not null)
        {
            return CustomerErrors.Anonymized;
        }

        if (await CurrentPolicyAsync(cancellationToken) is not { } policy)
        {
            return CustomerErrors.PolicyNotFound;
        }

        var context = new ConsentContext(actor.ActorId!.Value, current.BranchId ?? installation.BranchId, current.PosTerminalId, installation.NodeId, clock.UtcNow);
        var consents = (await store.GetConsentsAsync(customer.PartyId, cancellationToken)).ToList();
        foreach (var request in requests)
        {
            var consent = CustomerConsent.Create(
                ids.NewId(), customer.CompanyId, customer.PartyId,
                new ConsentInput(request.Purpose, request.Granted, request.Channel ?? ConsentChannel.PosVerbal, request.MarketingChannels, request.Evidence), policy,
                context);
            if (consent.IsFailure)
            {
                return consent.Error;
            }

            store.Add(consent.Value);
            consents.Add(consent.Value);
        }

        customer.ApplyConsents(consents);
        return Result.Success();
    }
}

// ─────────────────────────────── Búsqueda y ficha ───────────────────────────────

/// <summary>Búsqueda de la caja (D8-03): cédula/NIT (exacta o prefijo), celular o nombre; exactos primero; máximo 20.</summary>
public sealed record LookupCustomersQuery(string Query) : IQuery<IReadOnlyList<CustomerLookupDto>>;

internal sealed class LookupCustomersHandler(IPartyRegistry parties, ICustomerStore store) : IQueryHandler<LookupCustomersQuery, IReadOnlyList<CustomerLookupDto>>
{
    public async Task<Result<IReadOnlyList<CustomerLookupDto>>> Handle(LookupCustomersQuery request, CancellationToken cancellationToken)
    {
        var matches = await parties.LookupAsync(request.Query ?? string.Empty, 20, cancellationToken);
        var customers = await store.GetCustomersAsync([.. matches.Select(m => m.Id)], cancellationToken);
        var groups = (await store.GetGroupsAsync(cancellationToken)).ToDictionary(g => g.Id);
        return matches.Select(m =>
        {
            var customer = customers.GetValueOrDefault(m.Id);
            return new CustomerLookupDto(
                m.Id, m.DisplayName, m.IdentificationType, m.IdentificationNumber, m.CheckDigit, m.Phone, m.MatchedBy, m.Exact, customer is not null,
                customer is null ? null : groups.GetValueOrDefault(customer.GroupId)?.Code, customer?.Status.Db(), string.IsNullOrWhiteSpace(m.Email));
        }).ToList();
    }
}

public sealed record GetCustomerQuery(Guid PartyId) : IQuery<CustomerDto>;

internal sealed class GetCustomerHandler(ICustomerStore store, CustomerService service) : IQueryHandler<GetCustomerQuery, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(GetCustomerQuery request, CancellationToken cancellationToken) =>
        await store.GetCustomerAsync(request.PartyId, cancellationToken) is { } customer ? await service.DtoAsync(customer, cancellationToken) : CustomerErrors.NotFound;
}

public sealed record CustomerPageDto(IReadOnlyList<CustomerRow> Items, int Page, int PageSize, int TotalCount);

public sealed record ListCustomersQuery(string? Search, Guid? GroupId, string? Status, int Page = 1, int PageSize = 50) : IQuery<CustomerPageDto>;

internal sealed class ListCustomersHandler(ICustomerQueries queries) : IQueryHandler<ListCustomersQuery, CustomerPageDto>
{
    public async Task<Result<CustomerPageDto>> Handle(ListCustomersQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 200);
        var (items, total) = await queries.ListAsync(request.Search, request.GroupId, request.Status?.ToUpperInvariant(), page, size, cancellationToken);
        return new CustomerPageDto(items, page, size, total);
    }
}

// ─────────────────────────────── Alta rápida y datos ───────────────────────────────

/// <summary>
/// Alta rápida en la caja (D8-04): identificación (DV si es NIT), nombres o razón social; opcionales correo y celular; régimen 49
/// y R-99-PN por defecto. Si la identificación ya existe devuelve ese tercero SIN modificarlo (la cajera no edita terceros) y
/// le crea el rol si le falta. Registra las autorizaciones de datos del cliente (D8-06).
/// </summary>
public sealed record QuickCreateCustomerCommand(
    string PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? FirstNames,
    string? LastNames,
    string? LegalName,
    string? Email,
    string? Phone,
    IReadOnlyList<ConsentRequest>? Consents) : ICommand<QuickCreateResultDto>, IAllowedWhenRestricted;

internal sealed class QuickCreateCustomerHandler(IPartyRegistry parties, CustomerService service, ICustomerStore store, IAuditWriter audit)
    : ICommandHandler<QuickCreateCustomerCommand, QuickCreateResultDto>
{
    public async Task<Result<QuickCreateResultDto>> Handle(QuickCreateCustomerCommand request, CancellationToken cancellationToken)
    {
        var registered = await parties.RegisterAsync(
            new PartyRegistration(
                request.PersonType ?? "NATURAL", request.IdentificationType, request.IdentificationNumber, request.CheckDigit, request.LegalName, request.FirstNames,
                request.LastNames, null, null, null, request.Email, request.Phone, null, null),
            cancellationToken);
        if (registered.IsFailure)
        {
            return registered.Error;
        }

        var party = registered.Value.Party;
        var customer = await service.EnsureAsync(party.Id, registered.Value.Created ? CustomerOrigin.PosQuick : CustomerOrigin.AutoOnSale, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error;
        }

        var consents = await service.RecordConsentsAsync(customer.Value, request.Consents ?? [], cancellationToken);
        if (consents.IsFailure)
        {
            return consents.Error;
        }

        if (registered.Value.Created)
        {
            await audit.WriteAsync(
                new AuditEntry("customers", "CUSTOMER_QUICK_CREATED", nameof(Customer), party.Id, $"Cliente {party.DisplayName}",
                    $"Cliente {party.DisplayName} ({party.IdentificationType} {party.IdentificationNumber}) creado en la caja."),
                cancellationToken);
        }

        var groups = await store.GetGroupsAsync(cancellationToken);
        var dto = customer.Value.ToDto(party, groups.FirstOrDefault(g => g.Id == customer.Value.GroupId));
        return new QuickCreateResultDto(
            dto, registered.Value.Created,
            [.. registered.Value.PossibleDuplicates.Select(p => new CustomerLookupDto(
                p.Id, p.DisplayName, p.IdentificationType, p.IdentificationNumber, p.CheckDigit, p.Phone, "IDENTIFICATION", false, false, null, null,
                string.IsNullOrWhiteSpace(p.Email)))]);
    }
}

/// <summary>Completa SOLO los datos vacíos (la cajera no modifica lo que ya tiene valor, D8-04).</summary>
public sealed record CompleteCustomerCommand(Guid PartyId, string? Email, string? Phone, string? Address, string? MunicipalityCode) : ICommand<CustomerDto>, IAllowedWhenRestricted;

internal sealed class CompleteCustomerHandler(IPartyRegistry parties, ICustomerStore store, CustomerService service)
    : ICommandHandler<CompleteCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(CompleteCustomerCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetCustomerAsync(request.PartyId, cancellationToken) is not { } customer)
        {
            return CustomerErrors.NotFound;
        }

        if (customer.AnonymizedAt is not null)
        {
            return CustomerErrors.Anonymized;
        }

        var completed = await parties.CompleteAsync(request.PartyId, request.Email, request.Phone, request.Address, request.MunicipalityCode, cancellationToken);
        return completed.IsSuccess ? await service.DtoAsync(customer, cancellationToken) : completed.Error;
    }
}

/// <summary>Cliente creado desde la administración con todos sus datos.</summary>
public sealed record CreateCustomerCommand(PartyRegistration Party, bool AlwaysRequestsInvoice, IReadOnlyList<ConsentRequest>? Consents) : ICommand<CustomerDto>, IAllowedWhenRestricted;

internal sealed class CreateCustomerHandler(IPartyRegistry parties, CustomerService service) : ICommandHandler<CreateCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(CreateCustomerCommand request, CancellationToken cancellationToken)
    {
        var registered = await parties.RegisterAsync(request.Party, cancellationToken);
        if (registered.IsFailure)
        {
            return registered.Error;
        }

        if (!registered.Value.Created)
        {
            return Error.Conflict("PARTIES.IDENTIFICATION_DUPLICATED", "Ya existe un tercero con esa identificación: búsquelo y asígnele el rol de cliente.");
        }

        var customer = await service.EnsureAsync(registered.Value.Party.Id, CustomerOrigin.Backoffice, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error;
        }

        customer.Value.SetAlwaysRequestsInvoice(request.AlwaysRequestsInvoice);
        var consents = await service.RecordConsentsAsync(customer.Value, request.Consents ?? [], cancellationToken);
        return consents.IsSuccess ? await service.DtoAsync(customer.Value, cancellationToken) : consents.Error;
    }
}

/// <summary>Corrección de los datos del cliente (supervisor o administración, D8-04) y "siempre pide factura".</summary>
public sealed record UpdateCustomerCommand(Guid PartyId, PartyRegistration Party, bool AlwaysRequestsInvoice) : ICommand<CustomerDto>;

internal sealed class UpdateCustomerHandler(IPartyRegistry parties, ICustomerStore store, CustomerService service, IAuditWriter audit)
    : ICommandHandler<UpdateCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(UpdateCustomerCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetCustomerAsync(request.PartyId, cancellationToken) is not { } customer)
        {
            return CustomerErrors.NotFound;
        }

        if (customer.AnonymizedAt is not null)
        {
            return CustomerErrors.Anonymized;
        }

        var corrected = await parties.CorrectAsync(request.PartyId, request.Party, cancellationToken);
        if (corrected.IsFailure)
        {
            return corrected.Error;
        }

        customer.SetAlwaysRequestsInvoice(request.AlwaysRequestsInvoice);
        await audit.WriteAsync(
            new AuditEntry("customers", "CUSTOMER_DATA_CORRECTED", nameof(Customer), customer.PartyId, $"Cliente {corrected.Value.DisplayName}",
                $"Datos del cliente {corrected.Value.DisplayName} corregidos.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return await service.DtoAsync(customer, cancellationToken);
    }
}

/// <summary>Activa, inactiva o bloquea (con motivo, RN-CUS-03) un cliente.</summary>
public sealed record ChangeCustomerStatusCommand(Guid PartyId, CustomerStatus Status, string? Reason) : ICommand<CustomerDto>;

internal sealed class ChangeCustomerStatusHandler(ICustomerStore store, CustomerService service) : ICommandHandler<ChangeCustomerStatusCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(ChangeCustomerStatusCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetCustomerAsync(request.PartyId, cancellationToken) is not { } customer)
        {
            return CustomerErrors.NotFound;
        }

        var changed = customer.ChangeStatus(request.Status, request.Reason);
        return changed.IsSuccess ? await service.DtoAsync(customer, cancellationToken) : changed.Error;
    }
}

/// <summary>Grupo y lista de precio del cliente (RN-PRL-04: solo propietario o administrador; auditado).</summary>
public sealed record AssignCustomerPricingCommand(Guid PartyId, Guid GroupId, Guid? PriceListId) : ICommand<CustomerDto>;

internal sealed class AssignCustomerPricingHandler(ICustomerStore store, CustomerService service, ICatalogSaleItems catalog, IAuditWriter audit)
    : ICommandHandler<AssignCustomerPricingCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(AssignCustomerPricingCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetCustomerAsync(request.PartyId, cancellationToken) is not { } customer)
        {
            return CustomerErrors.NotFound;
        }

        if ((await store.GetGroupsAsync(cancellationToken)).FirstOrDefault(g => g.Id == request.GroupId && g.Status == MasterStatus.Active) is not { } group)
        {
            return CustomerErrors.GroupNotFound;
        }

        if (request.PriceListId is { } listId && await catalog.GetPriceListAsync(listId, cancellationToken) is not { IsActive: true })
        {
            return Error.NotFound("CATALOG.PRICE_LIST_NOT_FOUND", "La lista de precios no existe o está inactiva.");
        }

        var assigned = customer.AssignPricing(group.Id, request.PriceListId);
        if (assigned.IsFailure)
        {
            return assigned.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("customers", "CUSTOMER_PRICING_ASSIGNED", nameof(Customer), customer.PartyId, customer.AuditLabel,
                $"Cliente al grupo {group.Code} con lista {(request.PriceListId is null ? "del grupo" : request.PriceListId.ToString())}.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return await service.DtoAsync(customer, cancellationToken);
    }
}

// ─────────────────────────────── Autorizaciones ───────────────────────────────

/// <summary>Registra autorizaciones o revocaciones del cliente (libro de solo inserción, D8-06).</summary>
public sealed record RecordConsentsCommand(Guid PartyId, IReadOnlyList<ConsentRequest> Consents) : ICommand<CustomerDto>, IAllowedWhenRestricted;

internal sealed class RecordConsentsHandler(ICustomerStore store, CustomerService service) : ICommandHandler<RecordConsentsCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(RecordConsentsCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetCustomerAsync(request.PartyId, cancellationToken) is not { } customer)
        {
            return CustomerErrors.NotFound;
        }

        if (request.Consents is not { Count: > 0 })
        {
            return CustomerErrors.InvalidConsent;
        }

        var recorded = await service.RecordConsentsAsync(customer, request.Consents, cancellationToken);
        return recorded.IsSuccess ? await service.DtoAsync(customer, cancellationToken) : recorded.Error;
    }
}

public sealed record ListConsentsQuery(Guid PartyId) : IQuery<IReadOnlyList<ConsentDto>>;

internal sealed class ListConsentsHandler(ICustomerStore store, ICustomerQueries queries) : IQueryHandler<ListConsentsQuery, IReadOnlyList<ConsentDto>>
{
    public async Task<Result<IReadOnlyList<ConsentDto>>> Handle(ListConsentsQuery request, CancellationToken cancellationToken)
    {
        var consents = await store.GetConsentsAsync(request.PartyId, cancellationToken);
        var users = await queries.GetUserNamesAsync([.. consents.Select(c => c.UserId).Distinct()], cancellationToken);
        return consents.OrderByDescending(c => c.OccurredAt).Select(c => new ConsentDto(
            c.Id, c.Purpose.Db(), c.Granted, c.Channel.Db(), c.MarketingChannels?.Split(';') ?? [], c.PolicyVersion, c.Evidence, c.UserId,
            users.GetValueOrDefault(c.UserId), c.OccurredAt)).ToList();
    }
}

// ─────────────────────────────── Historial ───────────────────────────────

/// <summary>Historial del cliente (D8-13); sin autorización SERVICE vigente no se muestra (D8-07) ⚙️.</summary>
public sealed record CustomerHistoryQuery(Guid PartyId, DateOnly? From, DateOnly? To, int Page = 1, int PageSize = 50) : IQuery<IReadOnlyList<CustomerHistoryEntryDto>>;

internal sealed class CustomerHistoryHandler(ICustomerStore store, ICustomerSalesHistory history, ConsentGuard guard)
    : IQueryHandler<CustomerHistoryQuery, IReadOnlyList<CustomerHistoryEntryDto>>
{
    public async Task<Result<IReadOnlyList<CustomerHistoryEntryDto>>> Handle(CustomerHistoryQuery request, CancellationToken cancellationToken)
    {
        var allowed = await guard.EnsureAsync(await store.GetCustomerAsync(request.PartyId, cancellationToken), cancellationToken);
        return allowed.IsSuccess
            ? Result.Success(await history.GetHistoryAsync(
                request.PartyId, request.From, request.To, Math.Max(1, request.Page), Math.Clamp(request.PageSize, 1, 200), cancellationToken))
            : allowed.Error;
    }
}

public sealed record CustomerSummaryQuery(Guid PartyId) : IQuery<CustomerSalesSummaryDto>;

internal sealed class CustomerSummaryHandler(ICustomerStore store, ICustomerSalesHistory history, ConsentGuard guard)
    : IQueryHandler<CustomerSummaryQuery, CustomerSalesSummaryDto>
{
    public async Task<Result<CustomerSalesSummaryDto>> Handle(CustomerSummaryQuery request, CancellationToken cancellationToken)
    {
        var allowed = await guard.EnsureAsync(await store.GetCustomerAsync(request.PartyId, cancellationToken), cancellationToken);
        return allowed.IsSuccess ? await history.GetSummaryAsync(request.PartyId, cancellationToken) : allowed.Error;
    }
}

/// <summary>D8-07: el historial requiere la autorización SERVICE vigente (configurable).</summary>
public sealed class ConsentGuard(ISettingsReader settings, IInstallationContext installation)
{
    public async Task<Result> EnsureAsync(Customer? customer, CancellationToken cancellationToken)
    {
        if (customer is null)
        {
            return CustomerErrors.NotFound;
        }

        var required = await settings.GetAsync(CustomersSettings.HistoryRequiresConsent, new SettingContext(installation.CompanyId!.Value), cancellationToken);
        return !required || customer.ServiceConsent
            ? Result.Success()
            : Error.BusinessRule("CUSTOMERS.CONSENT_REQUIRED", "El cliente no ha autorizado el tratamiento de sus datos para historial y atención (D8-07).");
    }
}
