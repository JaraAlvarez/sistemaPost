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
        var options = configuration.GetSection(LicensingOptions.SectionName).Get<LicensingOptions>() ?? new LicensingOptions();
        LicensingInfrastructureRegistration.Register(services, options);
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        MapPosApi(api);
        MapAdminApi(api);
    }

    private static void MapPosApi(IEndpointRouteBuilder app)
    {
        var pos = app.MapGroup("/v1").WithTags("API del POS").RequireRateLimiting(PosApiRateLimit).AllowAnonymous();

        pos.MapPost("/activations", async (ActivationRequest request, HttpContext http, IDispatcher d, CancellationToken ct) =>
                ToHttp(await d.Send(new ActivateLicenseCommand(request, http.Connection.RemoteIpAddress), ct)))
            .WithSummary("Activa una instalación con la clave de licencia y devuelve el token firmado");
        pos.MapPost("/checkins", async (CheckinRequest request, HttpContext http, IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new CheckinCommand(request, http.Connection.RemoteIpAddress), ct);
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
