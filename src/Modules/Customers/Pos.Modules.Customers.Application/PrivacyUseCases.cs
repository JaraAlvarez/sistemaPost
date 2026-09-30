using System.Text.Json;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Customers.Contracts;
using Pos.Modules.Customers.Domain;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Customers.Application;

// ─────────────────────────────── Grupos ───────────────────────────────

public sealed record ListCustomerGroupsQuery : IQuery<IReadOnlyList<CustomerGroupDto>>;

internal sealed class ListCustomerGroupsHandler(ICustomerStore store) : IQueryHandler<ListCustomerGroupsQuery, IReadOnlyList<CustomerGroupDto>>
{
    public async Task<Result<IReadOnlyList<CustomerGroupDto>>> Handle(ListCustomerGroupsQuery request, CancellationToken cancellationToken) =>
        (await store.GetGroupsAsync(cancellationToken)).OrderByDescending(g => g.IsDefault).ThenBy(g => g.Code, StringComparer.Ordinal).Select(g => g.ToDto()).ToList();
}

public sealed record CreateCustomerGroupCommand(string Code, string Name, Guid? PriceListId) : ICommand<CustomerGroupDto>;

internal sealed class CreateCustomerGroupHandler(IInstallationContext installation, ICustomerStore store, IIdGenerator ids)
    : ICommandHandler<CreateCustomerGroupCommand, CustomerGroupDto>
{
    public async Task<Result<CustomerGroupDto>> Handle(CreateCustomerGroupCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return CustomersMapping.SetupRequired;
        }

        if ((await store.GetGroupsAsync(cancellationToken)).Any(g => string.Equals(g.Code, request.Code?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Error.Conflict("CUSTOMERS.GROUP_CODE_DUPLICATED", "Ya existe un grupo con ese código.");
        }

        var group = CustomerGroup.Create(ids.NewId(), companyId, request.Code!, request.Name, request.PriceListId);
        if (group.IsFailure)
        {
            return group.Error;
        }

        store.Add(group.Value);
        return group.Value.ToDto();
    }
}

public sealed record UpdateCustomerGroupCommand(Guid GroupId, string Name, Guid? PriceListId, bool IsActive) : ICommand<CustomerGroupDto>;

internal sealed class UpdateCustomerGroupHandler(ICustomerStore store) : ICommandHandler<UpdateCustomerGroupCommand, CustomerGroupDto>
{
    public async Task<Result<CustomerGroupDto>> Handle(UpdateCustomerGroupCommand request, CancellationToken cancellationToken)
    {
        if ((await store.GetGroupsAsync(cancellationToken)).FirstOrDefault(g => g.Id == request.GroupId) is not { } group)
        {
            return CustomerErrors.GroupNotFound;
        }

        var updated = group.Update(request.Name, request.PriceListId, request.IsActive);
        return updated.IsSuccess ? group.ToDto() : updated.Error;
    }
}

// ─────────────────────────────── Política de datos ───────────────────────────────

public sealed record ListPrivacyPoliciesQuery : IQuery<IReadOnlyList<PrivacyPolicyDto>>;

internal sealed class ListPrivacyPoliciesHandler(ICustomerStore store) : IQueryHandler<ListPrivacyPoliciesQuery, IReadOnlyList<PrivacyPolicyDto>>
{
    public async Task<Result<IReadOnlyList<PrivacyPolicyDto>>> Handle(ListPrivacyPoliciesQuery request, CancellationToken cancellationToken) =>
        (await store.GetPoliciesAsync(cancellationToken)).OrderByDescending(p => p.Version).Select(p => p.ToDto()).ToList();
}

/// <summary>Nueva versión de la política (queda pendiente de revisión hasta activarla); las versiones nunca se editan.</summary>
public sealed record CreatePrivacyPolicyCommand(string Text, string ShortNotice) : ICommand<PrivacyPolicyDto>;

internal sealed class CreatePrivacyPolicyHandler(IInstallationContext installation, ICustomerStore store, IIdGenerator ids)
    : ICommandHandler<CreatePrivacyPolicyCommand, PrivacyPolicyDto>
{
    public async Task<Result<PrivacyPolicyDto>> Handle(CreatePrivacyPolicyCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return CustomersMapping.SetupRequired;
        }

        var version = (await store.GetPoliciesAsync(cancellationToken)).Select(p => p.Version).DefaultIfEmpty(0).Max() + 1;
        var policy = PrivacyPolicy.Create(ids.NewId(), companyId, version, request.Text, request.ShortNotice);
        if (policy.IsFailure)
        {
            return policy.Error;
        }

        store.Add(policy.Value);
        return policy.Value.ToDto();
    }
}

/// <summary>El propietario activa una versión revisada: la anterior queda retirada.</summary>
public sealed record ActivatePrivacyPolicyCommand(Guid PolicyId) : ICommand<PrivacyPolicyDto>;

internal sealed class ActivatePrivacyPolicyHandler(ICustomerStore store, IActorContext actor, IClock clock)
    : ICommandHandler<ActivatePrivacyPolicyCommand, PrivacyPolicyDto>
{
    public async Task<Result<PrivacyPolicyDto>> Handle(ActivatePrivacyPolicyCommand request, CancellationToken cancellationToken)
    {
        var policies = await store.GetPoliciesAsync(cancellationToken);
        if (policies.FirstOrDefault(p => p.Id == request.PolicyId) is not { } policy)
        {
            return CustomerErrors.PolicyNotFound;
        }

        var activated = policy.Activate(actor.ActorId!.Value, clock.UtcNow);
        if (activated.IsFailure)
        {
            return activated.Error;
        }

        foreach (var other in policies.Where(p => p.Id != policy.Id && p.Status != PolicyStatus.Retired))
        {
            other.Retire();
        }

        return policy.ToDto();
    }
}

// ─────────────────────────────── Solicitudes de titulares ───────────────────────────────

public sealed record CreateDataRequestCommand(Guid PartyId, DataRequestType Type, ConsentChannel Channel, string Detail) : ICommand<DataRequestDto>, IAllowedWhenRestricted;

internal sealed class CreateDataRequestHandler(
    IInstallationContext installation, ICustomerStore store, IPartyRegistry parties, IAuditWriter audit, IIdGenerator ids, IClock clock)
    : ICommandHandler<CreateDataRequestCommand, DataRequestDto>
{
    public async Task<Result<DataRequestDto>> Handle(CreateDataRequestCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return CustomersMapping.SetupRequired;
        }

        if ((await parties.GetProfilesAsync([request.PartyId], cancellationToken)).GetValueOrDefault(request.PartyId) is not { IsSystem: false } party)
        {
            return CustomerErrors.NotFound;
        }

        var created = DataRequest.Create(ids.NewId(), companyId, party.Id, request.Type, request.Channel, request.Detail, clock.Today);
        if (created.IsFailure)
        {
            return created.Error;
        }

        store.Add(created.Value);
        await audit.WriteAsync(
            new AuditEntry("customers", "DATA_REQUEST_RECEIVED", nameof(DataRequest), created.Value.Id, created.Value.AuditLabel,
                $"Solicitud {request.Type} de {party.DisplayName}; vence el {created.Value.DueOn:yyyy-MM-dd}.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return DataRequests.ToDto(created.Value, party.DisplayName, clock.Today);
    }
}

public sealed record ListDataRequestsQuery(DataRequestStatus? Status) : IQuery<IReadOnlyList<DataRequestDto>>;

internal sealed class ListDataRequestsHandler(ICustomerStore store, IPartyRegistry parties, IClock clock) : IQueryHandler<ListDataRequestsQuery, IReadOnlyList<DataRequestDto>>
{
    public async Task<Result<IReadOnlyList<DataRequestDto>>> Handle(ListDataRequestsQuery request, CancellationToken cancellationToken)
    {
        var requests = await store.ListRequestsAsync(request.Status, cancellationToken);
        var names = await parties.GetProfilesAsync([.. requests.Select(r => r.PartyId).Distinct()], cancellationToken);
        return requests.OrderBy(r => r.DueOn)
            .Select(r => DataRequests.ToDto(r, names.GetValueOrDefault(r.PartyId)?.DisplayName ?? string.Empty, clock.Today)).ToList();
    }
}

/// <summary>Responde (resuelta) o rechaza una solicitud; supresión y exportación tienen sus propios casos de uso.</summary>
public sealed record CloseDataRequestCommand(Guid RequestId, bool Resolved, string Response) : ICommand<DataRequestDto>, IAllowedWhenRestricted;

internal sealed class CloseDataRequestHandler(ICustomerStore store, IPartyRegistry parties, IActorContext actor, IClock clock)
    : ICommandHandler<CloseDataRequestCommand, DataRequestDto>
{
    public async Task<Result<DataRequestDto>> Handle(CloseDataRequestCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetRequestAsync(request.RequestId, cancellationToken) is not { } dataRequest)
        {
            return Error.NotFound("CUSTOMERS.REQUEST_NOT_FOUND", "La solicitud no existe.");
        }

        var closed = dataRequest.Close(request.Resolved, request.Response, actor.ActorId!.Value, clock.UtcNow);
        if (closed.IsFailure)
        {
            return closed.Error;
        }

        var name = (await parties.GetProfilesAsync([dataRequest.PartyId], cancellationToken)).GetValueOrDefault(dataRequest.PartyId)?.DisplayName ?? string.Empty;
        return DataRequests.ToDto(dataRequest, name, clock.Today);
    }
}

internal static class DataRequests
{
    public static DataRequestDto ToDto(DataRequest r, string partyName, DateOnly today) => new(
        r.Id, r.PartyId, partyName, r.Type.Db(), r.Channel.Db(), r.Detail, r.ReceivedOn, r.DueOn, r.Status.Db(), r.IsOverdue(today), r.Response, r.ResolvedAt);
}

// ─────────────────────────────── Exportación y supresión ───────────────────────────────

/// <summary>Datos del titular para entregarle (consulta, Ley 1581 art. 14): perfil, autorizaciones, solicitudes y compras.</summary>
public sealed record CustomerExportDto(
    CustomerDto Customer, IReadOnlyList<ConsentDto> Consents, IReadOnlyList<DataRequestDto> Requests, CustomerSalesSummaryDto Purchases,
    IReadOnlyList<CustomerHistoryEntryDto> History, DateTimeOffset GeneratedAt);

/// <summary>Exporta los datos del titular (JSON), auditado.</summary>
public sealed record ExportCustomerDataCommand(Guid PartyId) : ICommand<CustomerExportDto>, IAllowedWhenRestricted;

internal sealed class ExportCustomerDataHandler(
    ICustomerStore store, CustomerService service, IDispatcher dispatcher, ICustomerSalesHistory history, IAuditWriter audit, IClock clock)
    : ICommandHandler<ExportCustomerDataCommand, CustomerExportDto>
{
    public async Task<Result<CustomerExportDto>> Handle(ExportCustomerDataCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetCustomerAsync(request.PartyId, cancellationToken) is not { } customer)
        {
            return CustomerErrors.NotFound;
        }

        var dto = await service.DtoAsync(customer, cancellationToken);
        if (dto.IsFailure)
        {
            return dto.Error;
        }

        var consents = await dispatcher.Send(new ListConsentsQuery(request.PartyId), cancellationToken);
        var requests = (await store.ListRequestsAsync(null, cancellationToken)).Where(r => r.PartyId == request.PartyId)
            .Select(r => DataRequests.ToDto(r, dto.Value.DisplayName, clock.Today)).ToList();
        var export = new CustomerExportDto(
            dto.Value, consents.IsSuccess ? consents.Value : [], requests, await history.GetSummaryAsync(request.PartyId, cancellationToken),
            await history.GetHistoryAsync(request.PartyId, null, null, 1, 1_000, cancellationToken), clock.UtcNow);
        await audit.WriteAsync(
            new AuditEntry("customers", "CUSTOMER_DATA_EXPORTED", nameof(Customer), customer.PartyId, $"Cliente {dto.Value.DisplayName}",
                $"Datos del titular {dto.Value.DisplayName} exportados ({JsonSerializer.Serialize(export).Length} bytes).", Severity: AuditSeverity.Warning),
            cancellationToken);
        return export;
    }
}

/// <summary>
/// Supresión (D8-08, RN-DAT-04): anonimiza el perfil del tercero, revoca las finalidades y deja el rol inactivo. La identificación,
/// los documentos y los snapshots de las ventas se conservan durante la retención legal.
/// </summary>
public sealed record AnonymizeCustomerCommand(Guid PartyId, string Reason) : ICommand<CustomerDto>, IAllowedWhenRestricted;

internal sealed class AnonymizeCustomerHandler(ICustomerStore store, IPartyRegistry parties, CustomerService service, IAuditWriter audit, IClock clock)
    : ICommandHandler<AnonymizeCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(AnonymizeCustomerCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetCustomerAsync(request.PartyId, cancellationToken) is not { } customer)
        {
            return CustomerErrors.NotFound;
        }

        if ((request.Reason ?? string.Empty).Trim().Length is < 5 or > 300)
        {
            return CustomerErrors.ReasonRequired;
        }

        if (customer.AnonymizedAt is not null)
        {
            return CustomerErrors.Anonymized;
        }

        var granted = (await store.GetConsentsAsync(customer.PartyId, cancellationToken))
            .GroupBy(c => c.Purpose).Select(g => g.OrderBy(c => c.OccurredAt).Last()).Where(c => c.Granted).Select(c => c.Purpose).ToList();
        var revoked = await service.RecordConsentsAsync(
            customer, [.. granted.Select(p => new ConsentRequest(p, false, ConsentChannel.PaperForm, null, "Supresión del titular"))], cancellationToken);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        var anonymized = await parties.AnonymizeAsync(customer.PartyId, cancellationToken);
        if (anonymized.IsFailure)
        {
            return anonymized.Error;
        }

        customer.Anonymize(clock.UtcNow);
        await audit.WriteAsync(
            new AuditEntry("customers", "CUSTOMER_ANONYMIZED", nameof(Customer), customer.PartyId, customer.AuditLabel,
                $"Datos del titular suprimidos: {request.Reason!.Trim()}", Severity: AuditSeverity.Warning),
            cancellationToken);
        return await service.DtoAsync(customer, cancellationToken);
    }
}

/// <summary>Estado de la privacidad para el tablero del propietario.</summary>
public sealed record PrivacyStatusQuery : IQuery<PrivacyStatusDto>;

internal sealed class PrivacyStatusHandler(ICustomerStore store, ICustomerQueries queries, ISettingsReader settings, IInstallationContext installation, IClock clock)
    : IQueryHandler<PrivacyStatusQuery, PrivacyStatusDto>
{
    public async Task<Result<PrivacyStatusDto>> Handle(PrivacyStatusQuery request, CancellationToken cancellationToken)
    {
        var policies = await store.GetPoliciesAsync(cancellationToken);
        var active = policies.FirstOrDefault(p => p.Status == PolicyStatus.Active);
        var open = await store.ListRequestsAsync(DataRequestStatus.Open, cancellationToken);
        var alertDays = await settings.GetAsync(CustomersSettings.RequestAlertDays, new SettingContext(installation.CompanyId!.Value), cancellationToken);
        var today = clock.Today;
        var soon = ColombianCalendar.AddBusinessDays(today, alertDays);
        return new PrivacyStatusDto(
            active is null, active?.Version, await queries.CountWithoutConsentAsync(cancellationToken), open.Count,
            open.Count(r => !r.IsOverdue(today) && r.DueOn <= soon), open.Count(r => r.IsOverdue(today)));
    }
}
