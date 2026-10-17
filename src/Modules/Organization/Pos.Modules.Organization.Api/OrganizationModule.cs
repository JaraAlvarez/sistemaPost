using System.Text.Json;
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
using Pos.Infrastructure.Persistence;
using Pos.Modules.Organization.Application;
using Pos.Modules.Organization.Application.Business;
using Pos.Modules.Organization.Application.Devices;
using Pos.Modules.Organization.Application.Settings;
using Pos.Modules.Organization.Application.Setup;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.Modules.Organization.Infrastructure;

namespace Pos.Modules.Organization.Api;

/// <summary>Módulo Organization: asistente inicial, empresa, sucursales, bodegas, cajas y configuración general.</summary>
public sealed class OrganizationModule : IModule
{
    public string Name => "organization";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IOrganizationStore).Assembly);
        OrganizationInfrastructureRegistration.Register(services);
        services.AddScoped<IOrganizationQueries, OrganizationQueries>();
        services.AddScoped<IBusinessFeatures, BusinessFeaturesReader>();
        services.AddSingleton<ISettingDefinitionProvider, GeneralSettingsProvider>();
        services.AddSingleton<IPermissionCatalogProvider, OrganizationPermissionCatalog>();
        services.AddScoped<ICompanyInitializer, DocumentSeriesInitializer>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        MapSetup(api.MapGroup("/setup").WithTags("Configuración inicial"));
        MapOrganization(api.MapGroup("/organization").WithTags("Organización"));
        MapSettings(api.MapGroup("/settings").WithTags("Configuración general"));
        MapDevices(api.MapGroup("/devices").WithTags("Equipos"));
    }

    private static void MapDevices(RouteGroupBuilder group)
    {
        group.MapPost("/pairing-codes", async (CreatePairingCodeCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(_ => "/api/v1/devices"))
            .RequirePermission(OrganizationPermissions.DeviceManage)
            .WithSummary("Código de 6 dígitos, de un solo uso, para emparejar una caja o un PC administrativo (vence en 10 min)");

        group.MapPost("/pair", async (PairDeviceCommand command, IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(command, ct);
                return result.IsFailure ? result.Error.ToProblem()
                    : result.Value.Error is { } error ? error.ToProblem()
                    : TypedResults.Created($"/api/v1/devices/{result.Value.Value!.DeviceId}", result.Value.Value);
            })
            .AllowAnonymousByDesign("El equipo nuevo se identifica con el código de un solo uso.")
            .RequireRateLimiting(DevicePairingRateLimit)
            .WithSummary("Empareja el equipo: devuelve su credencial (solo esta vez) y la huella del certificado del servidor");

        group.MapGet("/", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListDevicesQuery(), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.DeviceManage);

        group.MapPost("/{deviceId:guid}/revoke", async (Guid deviceId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RevokeDeviceCommand(deviceId), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.DeviceManage)
            .WithSummary("Revoca el equipo y cierra sus sesiones de inmediato");
    }

    /// <summary>Política de límite de peticiones del emparejamiento (la define el host).</summary>
    public const string DevicePairingRateLimit = "device-pairing";

    private static void MapSetup(RouteGroupBuilder group)
    {
        group.MapGet("/status", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetSetupStatusQuery(), ct)).ToHttpResult())
            .AllowAnonymousByDesign("El instalador consulta si la instalación ya está configurada.")
            .WithSummary("Estado del asistente inicial y edición instalada");

        group.MapPost("/", async (SetupCommand command, IDispatcher d, CancellationToken ct) => (await d.Send(command, ct)).ToHttpResult())
            .AllowAnonymousByDesign("Solo funciona mientras la instalación no está configurada (409 SETUP.ALREADY_COMPLETED después).")
            .WithSummary("Asistente inicial: empresa, sucursal, bodegas, caja, series, roles y usuario system en una transacción");
    }

    private static void MapOrganization(RouteGroupBuilder group)
    {
        group.MapGet("/company", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetCompanyQuery(), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.CompanyView);

        group.MapPut("/company", async (CompanyInput company, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateCompanyCommand(company), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.CompanyManage);

        // Fase 7-C parte A: tipo de negocio y funciones activables. Se leen con /auth/me (cualquier sesión); se cambian con el permiso
        // de los datos de la empresa.
        group.MapGet("/business-features", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetBusinessFeaturesQuery(), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .WithSummary("Tipo de negocio y funciones encendidas de la empresa");

        group.MapPut("/business-features", async (SetBusinessFeaturesRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetBusinessFeaturesCommand(r.EnabledFeatures), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.CompanyManage)
            .WithSummary("Enciende exactamente estas funciones; apagar una solo la oculta (los datos se conservan)");

        group.MapPut("/business-profile", async (ChangeBusinessProfileRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeBusinessProfileCommand(r.Profile), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.CompanyManage)
            .WithSummary("Cambia el tipo de negocio y deja los interruptores en los valores de ese tipo");

        group.MapGet("/branches", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListBranchesQuery(), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.BranchView);

        group.MapPost("/branches", async (BranchInput branch, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateBranchCommand(branch), ct)).ToCreatedResult(b => $"/api/v1/organization/branches/{b.Branch.Id}"))
            .RequirePermission(OrganizationPermissions.BranchManage);

        group.MapGet("/branches/{branchId:guid}", async (Guid branchId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetBranchQuery(branchId), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.BranchView);

        group.MapPut("/branches/{branchId:guid}", async (Guid branchId, UpdateBranchRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateBranchCommand(branchId, r.Name, r.MunicipalityCode, r.Address, r.Phone), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.BranchManage);

        group.MapPost("/branches/{branchId:guid}/deactivate", async (Guid branchId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivateBranchCommand(branchId), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.BranchManage);

        group.MapPost("/branches/{branchId:guid}/activate", async (Guid branchId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ActivateBranchCommand(branchId), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.BranchManage);

        group.MapPost("/branches/{branchId:guid}/warehouses", async (Guid branchId, CreateWarehouseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateWarehouseCommand(branchId, r.Code, r.Name, r.Kind, r.AllowsSales), ct))
                .ToCreatedResult(w => $"/api/v1/organization/branches/{branchId}"))
            .RequirePermission(OrganizationPermissions.WarehouseManage);

        group.MapPut("/warehouses/{warehouseId:guid}", async (Guid warehouseId, UpdateWarehouseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateWarehouseCommand(warehouseId, r.Name, r.AllowsSales), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.WarehouseManage);

        group.MapPost("/warehouses/{warehouseId:guid}/deactivate", async (Guid warehouseId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivateWarehouseCommand(warehouseId), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.WarehouseManage);

        group.MapGet("/terminals", async (Guid? branchId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListTerminalsQuery(branchId), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.BranchView);

        group.MapPost("/branches/{branchId:guid}/terminals", async (Guid branchId, CreateTerminalRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateTerminalCommand(branchId, r.Code, r.Name, r.WarehouseId), ct))
                .ToCreatedResult(t => $"/api/v1/organization/terminals?branchId={branchId}"))
            .RequirePermission(OrganizationPermissions.TerminalManage);

        group.MapPut("/terminals/{terminalId:guid}", async (Guid terminalId, UpdateTerminalRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateTerminalCommand(terminalId, r.Name, r.WarehouseId), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.TerminalManage);

        group.MapPost("/terminals/{terminalId:guid}/status", async (Guid terminalId, ChangeTerminalStatusRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeTerminalStatusCommand(terminalId, r.Status), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.TerminalManage);

        group.MapGet("/terminals/{terminalId:guid}/receipt-printer", async (Guid terminalId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetReceiptPrinterQuery(terminalId), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .WithSummary("Impresora de tiquetes de la caja (la interfaz de caja se la entrega al agente)");
        group.MapPut("/terminals/{terminalId:guid}/receipt-printer", async (Guid terminalId, ReceiptPrinterRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetReceiptPrinterCommand(terminalId, r.Connection, r.Address, r.PaperWidthMm, r.CodePage, r.AutoCut, r.DrawerConnected, r.DrawerPin), ct))
                .ToHttpResult())
            .RequirePermission(OrganizationPermissions.TerminalManage)
            .WithSummary("Configura la impresora de tiquetes: archivo, red (IP:9100), spooler de Windows o puerto serie; papel y cajón");

        group.MapGet("/terminals/{terminalId:guid}/scale", async (Guid terminalId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetTerminalScaleQuery(terminalId), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .WithSummary("Báscula conectada a la caja (la interfaz de caja se la entrega al agente)");
        group.MapPut("/terminals/{terminalId:guid}/scale", async (Guid terminalId, ScaleConfiguration r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetTerminalScaleCommand(terminalId, r), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.TerminalManage)
            .WithSummary("Configura la báscula de la caja: Toledo 8217, CAS o genérica; puerto serie y tiempos de espera de la caja");
        group.MapPut("/terminals/{terminalId:guid}/scale/reading-key", async (Guid terminalId, ScaleReadingKeyRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetTerminalScaleKeyCommand(terminalId, r.KeyId, r.PublicKey), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.TerminalManage)
            .WithSummary("Vincula la báscula: registra la clave pública con la que el agente de la caja firma las lecturas (rota la anterior)");
    }

    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid? branchId, Guid? terminalId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetSettingsQuery(branchId, terminalId), ct)).ToHttpResult())
            .RequirePermission(SettingsPermissions.SettingView)
            .WithSummary("Valores efectivos (con su origen) para la empresa, una sucursal o una caja");

        group.MapPut("/{key}", async (string key, SetSettingRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetSettingCommand(key, r.Scope, r.ScopeId, r.Value), ct)).ToHttpResult())
            .RequirePermission(SettingsPermissions.SettingManage)
            .WithSummary("Guarda una excepción en el nivel indicado");

        group.MapDelete("/{key}", async (string key, SettingScope scope, Guid? scopeId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RemoveSettingCommand(key, scope, scopeId), ct)).ToHttpResult())
            .RequirePermission(SettingsPermissions.SettingManage)
            .WithSummary("Elimina la excepción: ese nivel vuelve a heredar");
    }
}

public sealed record UpdateBranchRequest(string Name, string MunicipalityCode, string Address, string? Phone);

public sealed record CreateWarehouseRequest(string Code, string Name, WarehouseKind Kind, bool AllowsSales);

public sealed record UpdateWarehouseRequest(string Name, bool AllowsSales);

public sealed record CreateTerminalRequest(string Code, string Name, Guid? WarehouseId);

public sealed record UpdateTerminalRequest(string Name, Guid WarehouseId);

public sealed record ChangeTerminalStatusRequest(TerminalStatus Status);

public sealed record SetSettingRequest(SettingScope Scope, Guid? ScopeId, JsonElement Value);

public sealed record ReceiptPrinterRequest(
    PrinterConnection Connection, string? Address, int PaperWidthMm, PrinterCodePageSetting CodePage, bool AutoCut, bool DrawerConnected, DrawerPinSetting DrawerPin);
