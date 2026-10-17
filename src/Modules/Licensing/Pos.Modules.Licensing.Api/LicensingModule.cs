using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;
using Pos.Modules.Licensing.Infrastructure;
using Pos.SharedKernel.Results;
using QRCoder;

namespace Pos.Modules.Licensing.Api;

/// <summary>
/// Módulo Licensing (Fase 12-B): licencia dentro del POS. Activación con la clave, verificación diaria con el servidor de licencias,
/// estados locales calculados (DEMO, VALID, GRACE, RESTRICTED…) y restricciones en el backend. Nunca detiene una jornada abierta ni
/// bloquea consultas, reportes, exportación o backups (RN-LIC-01/04).
/// </summary>
public sealed class LicensingModule : IModule
{
    public string Name => "licensing";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(LicenseService).Assembly);
        LicensingInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, LicensePermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/license").WithTags("Licencia");

        group.MapGet("/", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetLicenseStatusQuery(), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.View)
            .WithSummary("Estado de la licencia de este equipo, días restantes, avisos y última verificación");

        // Fase 12-C: el QR solo dibuja el enlace GUARDADO (el que envió la nube); nunca una dirección que llegue en la petición.
        group.MapGet("/payment-qr", async (IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new GetLicenseStatusQuery(), ct);
                if (result.IsFailure)
                {
                    return result.ToHttpResult();
                }

                return result.Value.PaymentUrl is { } url
                    ? TypedResults.Text(LicensePaymentQr.Svg(url), "image/svg+xml; charset=utf-8")
                    : Result.Failure(LicenseErrors.NoPaymentLink).ToHttpResult();
            })
            .RequirePermission(LicensePermissions.View)
            .WithSummary("Código QR (SVG) del enlace para pagar la mensualidad en línea, para pagar desde el celular");

        group.MapGet("/checkins", async (int? limit, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListLicenseCheckinsQuery(limit ?? 50), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.View)
            .WithSummary("Historial de activaciones, verificaciones y liberaciones con el servidor de licencias");

        group.MapPost("/activate", async (ActivateLicenseRequest request, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ActivateLicenseCommand(request.LicenseKey), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.Manage)
            .WithSummary("Activa la licencia con la clave POS-XXXXX-XXXXX-XXXXX-XXXXX (requiere Internet)");

        group.MapPost("/check", async (IDispatcher d, CancellationToken ct) => (await d.Send(new CheckLicenseNowCommand(), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.Check)
            .WithSummary("Verifica ahora con el servidor de licencias (renovaciones, suspensiones, hora confiable)");

        group.MapPost("/deactivate", async (DeactivateLicenseRequest request, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivateLicenseCommand(request.Reason), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.Manage)
            .WithSummary("Libera este equipo de la licencia (antes de cambiar de computador); queda restringido hasta activar de nuevo");
    }
}

public sealed record ActivateLicenseRequest(string LicenseKey);

public sealed record DeactivateLicenseRequest(string? Reason);

/// <summary>Código QR del enlace de pago como imagen SVG (la interfaz lo muestra como imagen <c>data:</c>; la CSP lo permite).</summary>
internal static class LicensePaymentQr
{
    public static string Svg(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(6);
    }
}
