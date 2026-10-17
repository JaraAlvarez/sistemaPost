using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.Licensing.Infrastructure;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Licensing.Api;

/// <summary>Módulo de licencias de la nube: API del POS (/v1) y API interna (/admin).</summary>
public sealed class LicensingModule : IModule
{
    /// <summary>Política de límite de peticiones por IP de la API del POS (la registra el host).</summary>
    public const string PosApiRateLimit = "pos-api";

    public string Name => "licensing";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Se lee al resolver (no al registrar): así vale la configuración FINAL del host (variables, secretos, pruebas).
        var section = configuration.GetSection(LicensingOptions.SectionName);
        var payments = configuration.GetSection(PaymentsOptions.SectionName);
        LicensingInfrastructureRegistration.Register(
            services, _ => section.Get<LicensingOptions>() ?? new LicensingOptions(), _ => payments.Get<PaymentsOptions>() ?? new PaymentsOptions());
    }

    /// <summary>Política de límite de peticiones por IP de los eventos de la pasarela de pagos (la registra el host).</summary>
    public const string PaymentEventsRateLimit = "payment-events";

    /// <summary>Tamaño máximo del cuerpo de un evento de la pasarela.</summary>
    public const int MaxEventBytes = 64 * 1024;

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        MapPosApi(api);
        MapPaymentsApi(api);
        MapAdminApi(api);
    }

    /// <summary>Dirección pública de la petición (esquema, host y ruta base), para los enlaces de pago.</summary>
    public static string PublicBaseUrl(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"{request.Scheme}://{request.Host}{request.PathBase}";
    }

    /// <summary>
    /// Pago en línea (Fase 12-C). Anónimo por diseño: el evento lo autentica su firma (D12C-13) y el enlace su código de 128 bits
    /// (D12C-07). Siempre con límite de peticiones por IP.
    /// </summary>
    private static void MapPaymentsApi(IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/v1/payments").WithTags("Pago en línea").AllowAnonymous();

        payments.MapPost("/wompi/events", async (HttpRequest request, IDispatcher d, CancellationToken ct) =>
            {
                if (request.ContentLength > MaxEventBytes)
                {
                    return (IResult)TypedResults.Problem(title: "Solicitud demasiado grande", statusCode: StatusCodes.Status413PayloadTooLarge);
                }

                var buffer = new char[MaxEventBytes + 1];
                using var reader = new StreamReader(request.Body);
                var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
                if (read > MaxEventBytes)
                {
                    return TypedResults.Problem(title: "Solicitud demasiado grande", statusCode: StatusCodes.Status413PayloadTooLarge);
                }

                var result = await d.Send(new HandlePaymentEventCommand(new string(buffer, 0, read), request.Headers[WompiProtocol.ChecksumHeader].ToString()), ct);
                return result.IsFailure ? LicenseProblem(result.Error)
                    : result.Value.Error is { } error ? LicenseProblem(error)
                    : TypedResults.Ok(result.Value.Value);
            })
            .RequireRateLimiting(PaymentEventsRateLimit)
            .WithSummary("Evento firmado de Wompi (transaction.updated): verifica la firma, confirma la transacción y renueva una sola vez");

        payments.MapGet("/links/{code}", async (string code, IDispatcher d, CancellationToken ct) => ToHttp(await d.Send(new GetPaymentPageQuery(code), ct)))
            .RequireRateLimiting(PosApiRateLimit)
            .WithSummary("Resumen público del enlace de pago: empresa, edición, vencimiento y valores (sin NIT ni datos sensibles)");
        payments.MapPost("/links/{code}/orders", async (string code, CreateOrderRequest r, HttpContext http, IDispatcher d, CancellationToken ct) =>
                ToHttp(await d.Send(new CreatePaymentOrderCommand(code, r.Periods, PublicBaseUrl(http.Request)), ct)))
            .RequireRateLimiting(PosApiRateLimit)
            .WithSummary("Crea la orden de pago (valor congelado) y devuelve la redirección firmada al checkout de Wompi");
    }

    private static void MapPosApi(IEndpointRouteBuilder app)
    {
        var pos = app.MapGroup("/v1").WithTags("API del POS").RequireRateLimiting(PosApiRateLimit).AllowAnonymous();

        pos.MapPost("/activations", async (ActivationRequest request, HttpContext http, IDispatcher d, CancellationToken ct) =>
                ToHttp(await d.Send(new ActivateLicenseCommand(request, http.Connection.RemoteIpAddress), ct)))
            .WithSummary("Activa una instalación con la clave de licencia y devuelve el token firmado");
        pos.MapPost("/checkins", async (CheckinRequest request, HttpContext http, IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new CheckinCommand(request, http.Connection.RemoteIpAddress, PublicBaseUrl(http.Request)), ct);
                return result.IsFailure ? LicenseProblem(result.Error)
                    : result.Value.Error is { } error ? LicenseProblem(error)
                    : TypedResults.Ok(result.Value.Value);
            })
            .WithSummary("Check-in diario: token renovado con el estado actual de la suscripción y mensajes");
        pos.MapPost("/deactivations", async (DeactivationRequest request, HttpContext http, IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new DeactivateCommand(request, http.Connection.RemoteIpAddress), ct);
                return result.IsSuccess ? TypedResults.NoContent() : LicenseProblem(result.Error);
            })
            .WithSummary("Libera el equipo desde el propio POS");
        pos.MapGet("/public-keys", async (IDispatcher d, CancellationToken ct) => ToHttp(await d.Send(new GetPublicKeysQuery(), ct)))
            .WithSummary("Claves públicas de confianza por kid (JWK OKP Ed25519)");

        // Instalador premium (Fase 13-B, D13B-08 y D13B-09).
        pos.MapPost("/license-previews", async (LicensePreviewRequest request, IDispatcher d, CancellationToken ct) =>
                ToHttp(await d.Send(new PreviewLicenseCommand(request), ct)))
            .WithSummary("Datos públicos de la empresa de una clave vigente (razón social, NIT, edición, vencimiento) para el instalador");
        pos.MapPost("/owner-links", async (OwnerLinkRequest request, HttpContext http, IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new CreateOwnerLinkCommand(request, http.Connection.RemoteIpAddress), ct);
                return result.IsFailure ? LicenseProblem(result.Error)
                    : result.Value.Error is { } error ? LicenseProblem(error)
                    : TypedResults.Ok(result.Value.Value);
            })
            .WithSummary("Valida la cuenta del portal del dueño de la licencia y devuelve el enlace firmado para crear al Propietario del POS");
    }

    private static void MapAdminApi(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").WithTags("API interna del portal");

        admin.MapGet("/dashboard", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetDashboardQuery(), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.DashboardView);

        admin.MapGet("/accounts", async (string? search, IDispatcher d, CancellationToken ct) => (await d.Send(new ListAccountsQuery(search), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.LicensingView);
        admin.MapGet("/accounts/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetAccountQuery(id), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.LicensingView);
        admin.MapPost("/accounts", async (AccountInput input, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateAccountCommand(input), ct)).ToCreatedResult(id => $"/admin/accounts/{id}"))
            .RequireAuthorization(CloudPermissions.AccountManage);
        admin.MapPut("/accounts/{id:guid}", async (Guid id, UpdateAccountRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateAccountCommand(id, r.Account, r.IsActive), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.AccountManage);

        admin.MapGet("/organizations", async (Guid? accountId, string? search, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListOrganizationsQuery(accountId, search), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.LicensingView);
        admin.MapGet("/organizations/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetOrganizationQuery(id), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.LicensingView);
        admin.MapPost("/organizations", async (CreateOrganizationCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(id => $"/admin/organizations/{id}"))
            .RequireAuthorization(CloudPermissions.AccountManage);
        admin.MapPut("/organizations/{id:guid}", async (Guid id, UpdateOrganizationRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateOrganizationCommand(id, r.LegalName, r.City, r.IsActive), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.AccountManage);

        admin.MapGet("/subscriptions", async (string? status, IDispatcher d, CancellationToken ct) => (await d.Send(new ListSubscriptionsQuery(status), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.LicensingView);
        admin.MapPost("/subscriptions", async (CreateSubscriptionCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(id => $"/admin/subscriptions/{id}"))
            .RequireAuthorization(CloudPermissions.SubscriptionManage);
        admin.MapPost("/subscriptions/{id:guid}/renew", async (Guid id, RenewRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RenewSubscriptionCommand(id, r.PaymentReference, r.Periods), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.SubscriptionManage);
        admin.MapPost("/subscriptions/{id:guid}/suspend", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SuspendSubscriptionCommand(id, r.Reason), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.SubscriptionManage);
        admin.MapPost("/subscriptions/{id:guid}/reactivate", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReactivateSubscriptionCommand(id, r.Reason), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.SubscriptionSupport);
        admin.MapPost("/subscriptions/{id:guid}/cancel", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelSubscriptionCommand(id, r.Reason), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.SubscriptionManage);
        admin.MapPost("/subscriptions/{id:guid}/extend-grace", async (Guid id, ExtendGraceRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ExtendGraceCommand(id, r.Days, r.Reason), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.SubscriptionSupport);
        admin.MapPost("/subscriptions/{id:guid}/change-edition", async (Guid id, ChangeEditionRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeEditionCommand(id, r.Edition, r.Reason), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.SubscriptionManage);

        admin.MapPost("/licenses", async (GenerateLicenseCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(l => $"/admin/organizations/{l.OrganizationId}"))
            .RequireAuthorization(CloudPermissions.LicenseManage)
            .WithSummary("Genera la clave de la empresa (se muestra una sola vez)");
        admin.MapPost("/licenses/{id:guid}/regenerate", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegenerateLicenseCommand(id, r.Reason), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.LicenseManage);
        admin.MapPost("/licenses/{id:guid}/revoke", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RevokeLicenseCommand(id, r.Reason), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.LicenseManage);
        admin.MapPut("/licenses/{id:guid}/max-installations", async (Guid id, MaxInstallationsRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetMaxInstallationsCommand(id, r.MaxInstallations), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.LicenseManage);

        admin.MapGet("/installations", async (Guid? organizationId, string? search, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListInstallationsQuery(organizationId, search), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.LicensingView);
        admin.MapPost("/activations/{id:guid}/release", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReleaseActivationCommand(id, r.Reason), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.DeviceRelease)
            .WithSummary("Libera el equipo (cambio de PC): la sucursal activa el nuevo con su clave");

        admin.MapGet("/signing-keys", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListSigningKeysQuery(), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.SigningKeyView);

        // Pago en línea (Fase 12-C).
        admin.MapGet("/payments/settings", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetPaymentSettingsQuery(), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.PaymentView);
        admin.MapGet("/payments/prices", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPricesQuery(), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.PaymentView);
        admin.MapPost("/payments/prices", async (SetPriceCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(_ => "/admin/payments/prices"))
            .RequireAuthorization(CloudPermissions.PriceManage);
        admin.MapGet("/payments/orders", async (string? status, DateTimeOffset? from, DateTimeOffset? to, string? search, Guid? organizationId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListPaymentOrdersQuery(new PaymentOrderFilter(status, from, to, search, organizationId)), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.PaymentView);
        admin.MapGet("/payments/orders/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetPaymentOrderQuery(id), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.PaymentView);
        admin.MapPost("/payments/orders/{id:guid}/check", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CheckPaymentOrderCommand(id), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.PaymentView)
            .WithSummary("Consultar ahora: concilia la orden con la pasarela");
        admin.MapPost("/payments/orders/{id:guid}/review", async (Guid id, NoteRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new MarkPaymentReviewedCommand(id, r.Note), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.PaymentManage);
        admin.MapGet("/payments/alerts", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetPaymentAlertsQuery(), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.DashboardView);
        admin.MapPost("/subscriptions/{id:guid}/payment-link", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new EnsurePaymentLinkCommand(id), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.PaymentView)
            .WithSummary("Enlace de pago vigente de la suscripción (lo crea si no tiene)");
        admin.MapPost("/subscriptions/{id:guid}/payment-link/regenerate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegeneratePaymentLinkCommand(id), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.PaymentManage);
        admin.MapGet("/my/subscriptions", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetMySubscriptionsQuery(), ct)).ToHttpResult())
            .RequireAuthorization(CloudPermissions.SubscriptionSelfView);
    }

    /// <summary>Errores de la API del POS: 429 por límite de la licencia y 503 si el servidor no puede firmar.</summary>
    public static IResult LicenseProblem(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = error.Code switch
        {
            LicenseErrorCodes.TooManyRequests => StatusCodes.Status429TooManyRequests,
            LicenseErrorCodes.SigningUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => ResultHttpExtensions.StatusCodeFor(error.Type),
        };
        var extensions = new Dictionary<string, object?> { [ResultHttpExtensions.ErrorCodeExtension] = error.Code };
        if (error.FieldErrors.Count > 0)
        {
            extensions[ResultHttpExtensions.FieldErrorsExtension] = error.FieldErrors;
        }

        return TypedResults.Problem(title: ResultHttpExtensions.TitleFor(error.Type), detail: error.Message, statusCode: status, extensions: extensions);
    }

    private static IResult ToHttp<T>(Result<T> result) => result.IsSuccess ? TypedResults.Ok(result.Value) : LicenseProblem(result.Error);
}

public sealed record UpdateAccountRequest(AccountInput Account, bool IsActive);

public sealed record UpdateOrganizationRequest(string LegalName, string? City, bool IsActive);

public sealed record RenewRequest(string PaymentReference, int Periods = 1);

public sealed record ReasonRequest(string Reason);

public sealed record ExtendGraceRequest(int Days, string Reason);

public sealed record ChangeEditionRequest(string Edition, string Reason);

public sealed record MaxInstallationsRequest(int? MaxInstallations);

public sealed record CreateOrderRequest(int Periods = 1);

public sealed record NoteRequest(string Note);
