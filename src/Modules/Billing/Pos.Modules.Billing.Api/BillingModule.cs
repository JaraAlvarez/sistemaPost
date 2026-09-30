using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure;
using Pos.Modules.Billing.Application;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.Infrastructure;

namespace Pos.Modules.Billing.Api;

/// <summary>Cuerpo de la corrección de los datos del adquirente.</summary>
public sealed record CorrectBuyerRequest(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? Email,
    string PersonType,
    string TaxRegime,
    IReadOnlyList<string>? Responsibilities,
    string? Address,
    string? MunicipalityCode,
    string? Phone);

/// <summary>Asignación de un rango: sucursal (sin sucursal = desasignar) y opcionalmente caja.</summary>
public sealed record AssignRangeRequest(Guid? BranchId, Guid? PosTerminalId);

/// <summary>
/// Módulo Billing (Fase 7): comprobantes de venta; Fase 11-B: facturación electrónica independiente del proveedor (modo, credenciales
/// cifradas, rangos, cola de envío con contingencia, notas crédito, documento soporte, conciliación y alertas).
/// </summary>
public sealed class BillingModule : IModule
{
    public string Name => "billing";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IFiscalDocumentStore).Assembly);
        BillingInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, BillingPermissionCatalog>();
        services.AddSingleton<ISettingDefinitionProvider, BillingSettingsProvider>();
        services.AddScoped<IBillingService, BillingService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/billing/documents").WithTags("Facturación");

        group.MapGet("/", async (DateOnly? from, DateOnly? to, string? status, string? type, string? source, string? search, int? limit, IDispatcher d,
                    CancellationToken ct) =>
                (await d.Send(new ListFiscalDocumentsQuery(from, to, status, limit, type, source, search), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentView)
            .WithSummary("Comprobantes y documentos fiscales de la sucursal con sus eventos (filtros: fechas, estado, tipo, origen y texto)");
        group.MapGet("/{documentId:guid}", async (Guid documentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetFiscalDocumentQuery(documentId), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentView);
        group.MapPost("/{documentId:guid}/retry", async (Guid documentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RetryFiscalDocumentCommand(documentId), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentManage)
            .WithSummary("Reintenta el envío de un documento electrónico pendiente, con error, en contingencia o rechazado");
        group.MapPut("/{documentId:guid}/buyer", async (Guid documentId, CorrectBuyerRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(
                    new CorrectFiscalBuyerCommand(
                        documentId, r.Name, r.IdentificationType, r.IdentificationNumber, r.CheckDigit, r.Email, r.PersonType, r.TaxRegime, r.Responsibilities,
                        r.Address, r.MunicipalityCode, r.Phone),
                    ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentManage)
            .WithSummary("Corrige los datos del adquirente de un documento aún no aceptado y lo deja listo para reenviar");

        var billing = api.MapGroup("/billing").WithTags("Facturación");

        billing.MapGet("/settings", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetBillingSettingsQuery(), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.SettingsManage)
            .WithSummary("Modo de emisión, ambiente y estado del proveedor (las credenciales nunca se devuelven)");
        billing.MapPut("/settings", async (UpdateBillingSettingsCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.SettingsManage)
            .WithSummary("Cambia el modo (OFF, ON_REQUEST, EVERY_SALE) y el ambiente (SANDBOX, PRODUCTION); encender exige credenciales");
        billing.MapPut("/settings/credentials", async (SetProviderCredentialsCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.SettingsManage)
            .WithSummary("Guarda las credenciales del proveedor cifradas con DPAPI (no se devuelven ni se registran)");

        billing.MapGet("/ranges", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListFiscalRangesQuery(), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentView)
            .WithSummary("Rangos de numeración con su uso, vigencia y asignación");
        billing.MapPost("/ranges/sync", async (FiscalRangeService ranges, CancellationToken ct) => (await ranges.SyncAsync(ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.SettingsManage)
            .WithSummary("Sincroniza los rangos de numeración desde el proveedor");
        billing.MapPut("/ranges/{rangeId:guid}/assignment", async (Guid rangeId, AssignRangeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AssignFiscalRangeCommand(rangeId, r.BranchId, r.PosTerminalId), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.SettingsManage)
            .WithSummary("Asigna el rango a una sucursal (y opcionalmente a una caja) o lo desasigna");

        billing.MapGet("/reconciliation", async (DateOnly? from, DateOnly? to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetFiscalReconciliationQuery(from, to), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentView)
            .WithSummary("Conciliación por día: ventas vs. documentos aceptados, pendientes, rechazados y en contingencia");
        billing.MapGet("/alerts", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetFiscalAlertsQuery(), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentView)
            .WithSummary("Pendientes antiguos, rechazos sin atender, documentos sin rango y rangos por agotarse o vencer");
        billing.MapPost("/queue/process", async (FiscalQueueRunner runner, CancellationToken ct) =>
                Results.Ok(await runner.ProcessDueAsync(maxDocuments: 500, ct)))
            .RequirePermission(BillingPermissions.DocumentManage)
            .WithSummary("Envía ahora la cola de documentos electrónicos (lo mismo que hace el proceso en segundo plano)");
    }
}
