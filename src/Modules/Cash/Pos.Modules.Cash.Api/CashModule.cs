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
using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Infrastructure;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Cash.Api;

/// <summary>Módulo Cash: medios de pago (Fase 5); jornadas, movimientos, arqueos y cierres (Fase 6).</summary>
public sealed class CashModule : IModule
{
    public string Name => "cash";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(ICashStore).Assembly);
        CashInfrastructureRegistration.Register(services);
        services.AddScoped<ICompanyInitializer, CashInitializer>();
        services.AddSingleton<IPermissionCatalogProvider, CashPermissionCatalog>();
        services.AddSingleton<ISettingDefinitionProvider, CashSettingsProvider>();
        services.AddScoped<SessionAccess>();
        services.AddScoped<SessionLoader>();
        services.AddScoped<SessionViews>();
        services.AddScoped<OpenSalesGuard>();
        services.AddScoped<CashRegisterService>();
        services.AddScoped<ICashRegister>(sp => sp.GetRequiredService<CashRegisterService>());
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/cash").WithTags("Caja");

        group.MapGet("/payment-methods", async (bool? includeInactive, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListPaymentMethodsQuery(includeInactive ?? false), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .WithSummary("Medios de pago de la empresa (efectivo, tarjetas, transferencias, billeteras, bonos)");

        group.MapPost("/payment-methods", async (CreatePaymentMethodCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(m => $"/api/v1/cash/payment-methods/{m.Id}"))
            .RequirePermission(CashPermissions.PaymentMethodManage);

        group.MapPut("/payment-methods/{paymentMethodId:guid}", async (Guid paymentMethodId, UpdatePaymentMethodRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdatePaymentMethodCommand(paymentMethodId, r.Name, r.DianCode, r.RequiresReference, r.SortOrder, r.IsActive), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.PaymentMethodManage);

        group.MapGet("/denominations", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListDenominationsQuery(), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .WithSummary("Billetes y monedas para el arqueo por denominación");

        MapSessions(group.MapGroup("/sessions"));
    }

    private static void MapSessions(RouteGroupBuilder group)
    {
        group.MapPost("/", async (OpenSessionCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(s => $"/api/v1/cash/sessions/{s.Id}"))
            .RequirePermission(CashPermissions.SessionOperate)
            .WithSummary("Abre la jornada del cajero en su caja (sesión de caja con código y PIN) con el fondo inicial");
        group.MapGet("/current", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetCurrentSessionQuery(), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.SessionOperate)
            .WithSummary("Jornada sin cerrar de esta caja (o del cajero)");
        group.MapGet("/", async (string? status, DateOnly? from, DateOnly? to, bool? pendingReview, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListSessionsQuery(status, from, to, pendingReview ?? false), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.ReportView)
            .WithSummary("Jornadas de la sucursal (pendingReview=true: cierres con diferencia sin revisar)");
        group.MapGet("/{sessionId:guid}", async (Guid sessionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetSessionQuery(sessionId), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.SessionOperate)
            .WithSummary("Jornada con movimientos y totales (con arqueo ciego, el cajero no ve lo esperado)");

        group.MapPost("/{sessionId:guid}/cash-in", async (Guid sessionId, MovementRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterMovementCommand(sessionId, ManualMovementKind.CashIn, r.Amount, r.Reason), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.SessionOperate)
            .WithSummary("Ingreso de efectivo con motivo");
        group.MapPost("/{sessionId:guid}/withdrawals", async (Guid sessionId, MovementRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterMovementCommand(sessionId, ManualMovementKind.Withdrawal, r.Amount, r.Reason), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.MovementWithdraw, allowSupervisor: true)
            .WithSummary("Retiro de efectivo (sangría): permiso o autorización de supervisor; no deja el efectivo negativo");
        group.MapPost("/{sessionId:guid}/drawer-openings", async (Guid sessionId, ReasonOnlyRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterMovementCommand(sessionId, ManualMovementKind.NoSaleDrawerOpen, 0m, r.Reason), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.DrawerOpen, allowSupervisor: true)
            .WithSummary("Apertura del cajón sin venta con motivo (permiso o autorización de supervisor)");
        group.MapPost("/{sessionId:guid}/corrections", async (Guid sessionId, CorrectionRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterMovementCommand(sessionId, ManualMovementKind.Correction, r.Amount, r.Reason, r.PaymentMethodId, r.Direction), ct))
                .ToHttpResult())
            .RequirePermission(CashPermissions.SessionReview)
            .WithSummary("Corrección (entra o sale) con motivo: el cierre es definitivo y los errores se corrigen en la jornada siguiente");

        group.MapPost("/{sessionId:guid}/start-closing", async (Guid sessionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new StartClosingCommand(sessionId), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.SessionOperate)
            .WithSummary("Inicia el cierre: no se admiten más ventas");
        group.MapPost("/{sessionId:guid}/cancel-closing", async (Guid sessionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelClosingCommand(sessionId), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.SessionOperate);
        group.MapPost("/{sessionId:guid}/close", async (Guid sessionId, CloseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CloseSessionCommand(sessionId, r.Count, r.DifferenceNote), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.SessionOperate)
            .WithSummary("Confirma el arqueo (efectivo por denominación, demás medios por total) y devuelve el reporte Z con el sello");
        group.MapPost("/{sessionId:guid}/supervisor-close", async (Guid sessionId, SupervisorCloseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CloseSessionCommand(sessionId, r.Count, null, BySupervisor: true, r.Reason), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.SessionCloseAny)
            .WithSummary("Cierre por supervisor (cajero ausente): con motivo y conteo; queda para revisión y auditado como crítico");
        group.MapPost("/{sessionId:guid}/review", async (Guid sessionId, ReviewRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReviewSessionCommand(sessionId, r.Note), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.SessionReview)
            .WithSummary("Revisión del supervisor de un cierre con diferencia");

        group.MapGet("/{sessionId:guid}/report-x", async (Guid sessionId, string? format, IDispatcher d, CancellationToken ct) =>
                Report(await d.Send(new GetXReportQuery(sessionId), ct), format))
            .RequirePermission(CashPermissions.SessionOperate)
            .WithSummary("Reporte X (parcial); format=text para la impresión de 80 mm");
        group.MapGet("/{sessionId:guid}/report-z", async (Guid sessionId, string? format, IDispatcher d, CancellationToken ct) =>
                Report(await d.Send(new GetZReportQuery(sessionId), ct), format))
            .RequirePermission(CashPermissions.SessionOperate)
            .WithSummary("Reporte Z (cierre) con el sello de la auditoría; format=text para la impresión de 80 mm");
    }

    private static IResult Report(Result<CashReportDto> result, string? format) =>
        result.IsSuccess && string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
            ? TypedResults.Text(result.Value.Text, "text/plain; charset=utf-8")
            : result.ToHttpResult();
}

public sealed record UpdatePaymentMethodRequest(string Name, string? DianCode, bool RequiresReference, int SortOrder, bool IsActive);

public sealed record MovementRequest(decimal Amount, string Reason);

public sealed record ReasonOnlyRequest(string Reason);

public sealed record CorrectionRequest(decimal Amount, int Direction, string Reason, Guid? PaymentMethodId);

public sealed record CloseRequest(IReadOnlyList<CountLineRequest> Count, string? DifferenceNote);

public sealed record SupervisorCloseRequest(IReadOnlyList<CountLineRequest> Count, string Reason);

public sealed record ReviewRequest(string Note);
