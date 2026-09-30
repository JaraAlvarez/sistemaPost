using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure;
using Pos.Modules.Customers.Application;
using Pos.Modules.Customers.Contracts;
using Pos.Modules.Customers.Domain;
using Pos.Modules.Customers.Infrastructure;
using Pos.Modules.Parties.Contracts;

namespace Pos.Modules.Customers.Api;

/// <summary>Módulo Customers (Fase 8): clientes en la caja, grupos y listas, privacidad (Ley 1581) e historial.</summary>
public sealed class CustomersModule : IModule
{
    public string Name => "customers";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(ICustomerStore).Assembly);
        CustomersInfrastructureRegistration.Register(services);
        services.AddScoped<ICompanyInitializer, CustomersInitializer>();
        services.AddSingleton<IPermissionCatalogProvider, CustomersPermissionCatalog>();
        services.AddSingleton<ISettingDefinitionProvider, CustomersSettingsProvider>();
        services.AddScoped<CustomerService>();
        services.AddScoped<ConsentGuard>();
        services.AddScoped<ICustomerDirectory, CustomerDirectory>();
        services.AddSingleton<ICustomerCreditGate, NullCustomerCreditGate>();
        services.AddSingleton<ILoyaltyProgram, NullLoyaltyProgram>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/customers").WithTags("Clientes");

        group.MapGet("/lookup", async (string q, IDispatcher d, CancellationToken ct) => (await d.Send(new LookupCustomersQuery(q), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerView)
            .WithSummary("Búsqueda de la caja: cédula/NIT (exacta o prefijo, sin DV), celular o nombre; exactos primero");
        group.MapGet("/", async (string? search, Guid? groupId, string? status, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListCustomersQuery(search, groupId, status, page ?? 1, pageSize ?? 50), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerView);
        group.MapPost("/quick", async (QuickCreateCustomerCommand command, IDispatcher d, CancellationToken ct) => (await d.Send(command, ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerQuickCreate)
            .WithSummary("Alta rápida en la caja con la autorización de datos; si la identificación ya existe devuelve ese cliente sin modificarlo");
        group.MapPost("/", async (CreateCustomerCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(c => $"/api/v1/customers/{c.PartyId}"))
            .RequirePermission(CustomersPermissions.CustomerManage);
        group.MapGet("/{partyId:guid}", async (Guid partyId, IDispatcher d, CancellationToken ct) => (await d.Send(new GetCustomerQuery(partyId), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerView);
        group.MapPut("/{partyId:guid}", async (Guid partyId, UpdateCustomerRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateCustomerCommand(partyId, r.Party, r.AlwaysRequestsInvoice), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerManage)
            .WithSummary("Corrige los datos del cliente (supervisor o administración)");
        group.MapPost("/{partyId:guid}/complete", async (Guid partyId, CompleteCustomerRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CompleteCustomerCommand(partyId, r.Email, r.Phone, r.Address, r.MunicipalityCode), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerQuickCreate)
            .WithSummary("Completa solo los datos vacíos (la cajera no modifica lo que ya tiene valor)");
        group.MapPost("/{partyId:guid}/status", async (Guid partyId, StatusRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeCustomerStatusCommand(partyId, r.Status, r.Reason), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerManage)
            .WithSummary("Activa, inactiva o bloquea (con motivo) al cliente");
        group.MapPut("/{partyId:guid}/pricing", async (Guid partyId, PricingRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AssignCustomerPricingCommand(partyId, r.GroupId, r.PriceListId), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.PricingAssign)
            .WithSummary("Grupo y lista de precio del cliente (solo propietario o administrador)");
        group.MapGet("/{partyId:guid}/consents", async (Guid partyId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListConsentsQuery(partyId), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerView);
        group.MapPost("/{partyId:guid}/consents", async (Guid partyId, ConsentsRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordConsentsCommand(partyId, r.Consents), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerQuickCreate)
            .WithSummary("Registra autorizaciones o revocaciones (libro de solo inserción con la versión de la política)");
        group.MapGet("/{partyId:guid}/history", async (Guid partyId, DateOnly? from, DateOnly? to, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CustomerHistoryQuery(partyId, from, to, page ?? 1, pageSize ?? 50), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.HistoryView)
            .WithSummary("Ventas, anulaciones, cambios y garantías del cliente (requiere su autorización de datos)");
        group.MapGet("/{partyId:guid}/summary", async (Guid partyId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CustomerSummaryQuery(partyId), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.HistoryView)
            .WithSummary("Compras, total comprado, ticket promedio, primera y última compra, productos frecuentes");
        group.MapPost("/{partyId:guid}/export", async (Guid partyId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ExportCustomerDataCommand(partyId), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.PrivacyManage)
            .WithSummary("Datos del titular (consulta de la Ley 1581), auditado");
        group.MapPost("/{partyId:guid}/anonymize", async (Guid partyId, AnonymizeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AnonymizeCustomerCommand(partyId, r.Reason), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.PrivacyManage)
            .WithSummary("Supresión por anonimización: se conservan la identificación y los documentos");

        group.MapGet("/groups", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCustomerGroupsQuery(), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerView);
        group.MapPost("/groups", async (CreateCustomerGroupCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(g => $"/api/v1/customers/groups/{g.Id}"))
            .RequirePermission(CustomersPermissions.GroupManage);
        group.MapPut("/groups/{groupId:guid}", async (Guid groupId, GroupRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateCustomerGroupCommand(groupId, r.Name, r.PriceListId, r.IsActive), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.GroupManage);

        group.MapGet("/privacy-policies", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPrivacyPoliciesQuery(), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.CustomerView)
            .WithSummary("Versiones de la política de datos (el aviso corto se lee al cliente en la caja)");
        group.MapPost("/privacy-policies", async (CreatePrivacyPolicyCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(p => $"/api/v1/customers/privacy-policies/{p.Id}"))
            .RequirePermission(CustomersPermissions.PrivacyManage);
        group.MapPost("/privacy-policies/{policyId:guid}/activate", async (Guid policyId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ActivatePrivacyPolicyCommand(policyId), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.PrivacyManage)
            .WithSummary("El propietario activa la versión revisada (la anterior se retira)");
        group.MapGet("/privacy-status", async (IDispatcher d, CancellationToken ct) => (await d.Send(new PrivacyStatusQuery(), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.PrivacyManage)
            .WithSummary("Tablero: política pendiente, clientes sin autorización y solicitudes por vencer o vencidas");

        group.MapGet("/data-requests", async (DataRequestStatus? status, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListDataRequestsQuery(status), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.PrivacyManage);
        group.MapPost("/data-requests", async (CreateDataRequestCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(r => $"/api/v1/customers/data-requests/{r.Id}"))
            .RequirePermission(CustomersPermissions.PrivacyManage)
            .WithSummary("Solicitud del titular: vence en 10 (consulta) o 15 (reclamo) días hábiles con los festivos de Colombia");
        group.MapPost("/data-requests/{requestId:guid}/close", async (Guid requestId, CloseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CloseDataRequestCommand(requestId, r.Resolved, r.Response), ct)).ToHttpResult())
            .RequirePermission(CustomersPermissions.PrivacyManage);
    }
}

public sealed record UpdateCustomerRequest(PartyRegistration Party, bool AlwaysRequestsInvoice);

public sealed record CompleteCustomerRequest(string? Email, string? Phone, string? Address, string? MunicipalityCode);

public sealed record StatusRequest(CustomerStatus Status, string? Reason);

public sealed record PricingRequest(Guid GroupId, Guid? PriceListId);

public sealed record ConsentsRequest(IReadOnlyList<ConsentRequest> Consents);

public sealed record AnonymizeRequest(string Reason);

public sealed record GroupRequest(string Name, Guid? PriceListId, bool IsActive);

public sealed record CloseRequest(bool Resolved, string Response);
