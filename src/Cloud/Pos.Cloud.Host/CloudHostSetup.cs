using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Host.Components;
using Pos.Cloud.Host.Hosting;
using Pos.Cloud.Host.Security;
using Pos.Cloud.Infrastructure;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Api;
using Pos.Cloud.PortalIdentity.Api;
using Pos.Infrastructure;
using Pos.SharedKernel.Time;
using Serilog;

namespace Pos.Cloud.Host;

/// <summary>Composición del servidor de la nube: servicios (AddCloudHost) y pipeline HTTP (UseCloudHost).</summary>
internal static class CloudHostSetup
{
    public static IReadOnlyList<IModule> Modules { get; } = [new PortalIdentityModule(), new LicensingModule(), new Sync.Api.SyncModule()];

    public static WebApplicationBuilder AddCloudHost(this WebApplicationBuilder builder)
    {
        var cloud = builder.Configuration.GetSection(CloudOptions.SectionName).Get<CloudOptions>() ?? new CloudOptions();
        builder.Services.Configure<CloudOptions>(builder.Configuration.GetSection(CloudOptions.SectionName));

        builder.Services.AddSerilog(
            (services, logger) => logger.ReadFrom.Configuration(services.GetRequiredService<IConfiguration>())
                .Enrich.FromLogContext()
                .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture),
            preserveStaticLogger: true);

        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Instance ??= $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
            context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
        });
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddOpenApi("v1");

        // Núcleo: reloj (zona de Colombia para mostrar fechas), IDs, despachador y persistencia de la nube.
        builder.Services.AddPosInfrastructure(BusinessTimeZones.Colombia);
        builder.Services.AddScoped<CloudRequestContext>();
        builder.Services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<CloudRequestContext>());
        builder.Services.AddCloudPersistence(
            sp => new CloudPersistenceOptions
            {
                ConnectionString = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CloudOptions>>().Value.Database.ConnectionString is { Length: > 0 } connection
                    ? connection
                    : throw new InvalidOperationException("Falta la cadena de conexión 'Cloud:Database:ConnectionString' (rol pos_app de la BD de la nube)."),
            },
            runBackgroundServices: cloud.AuditSealing);
        builder.Services.Configure<Pos.Infrastructure.Auditing.AuditSealingOptions>(builder.Configuration.GetSection("Cloud:Audit"));

        foreach (var module in Modules)
        {
            module.Register(builder.Services, builder.Configuration);
        }

        var dataProtection = builder.Services.AddDataProtection().SetApplicationName("pos-cloud");
        if (!string.IsNullOrWhiteSpace(cloud.DataProtectionKeysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(cloud.DataProtectionKeysPath));
        }

        builder.Services.AddPortalAuthentication();
        builder.Services.AddHttpContextAccessor();
        AddRateLimits(builder.Services);
        builder.Services.AddCloudHealthChecks();
        builder.Services.AddHostedService<CloudStartup>();
        builder.Services.AddHostedService<SubscriptionStatusRefresher>();

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            if (cloud.TrustForwardedHeaders)
            {
                // Solo Caddy llega a la aplicación (el puerto no se publica): se confía en su red interna de Docker.
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
        });
        builder.Services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = false;
        });

        // Portal: Blazor Web App con interactividad de servidor y MudBlazor (L-02).
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, PortalRevalidatingAuthenticationStateProvider>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, PortalCircuitHandler>());
        builder.Services.AddScoped<PortalOperations>();
        builder.Services.AddMudServices();
        return builder;
    }

    /// <summary>Límites por IP (§6): API del POS y accesos al portal. El límite por licencia lo aplica el caso de uso.</summary>
    private static void AddRateLimits(IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await TypedResults.Problem(
                        title: "Demasiadas solicitudes",
                        detail: "Demasiadas solicitudes desde esta dirección. Espere un momento e intente de nuevo.",
                        statusCode: StatusCodes.Status429TooManyRequests,
                        extensions: new Dictionary<string, object?> { [ResultHttpExtensions.ErrorCodeExtension] = "SECURITY.TOO_MANY_REQUESTS" })
                    .ExecuteAsync(context.HttpContext);
            };
            options.AddPolicy(LicensingModule.PosApiRateLimit, context =>
                PerIp(context, "pos", c => c.PosApiPermitsPerMinute));
            options.AddPolicy(PortalIdentityModule.LoginRateLimit, context =>
                PerIp(context, "login", c => c.LoginPermitsPerMinute));
        });

    private static RateLimitPartition<string> PerIp(HttpContext context, string name, Func<CloudSecurityOptions, int> permits)
    {
        var options = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CloudOptions>>().CurrentValue.Security;
        var limit = Math.Max(1, permits(options));
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "desconocida";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{name}:{limit}:{ip}", _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    public static WebApplication UseCloudHost(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging();
        app.UseExceptionHandler();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<DatabaseGateMiddleware>();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<RequestContextMiddleware>();
        app.UseMiddleware<PortalUserContextMiddleware>();
        app.UseMiddleware<MustChangePasswordMiddleware>();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets().AllowAnonymous();
        app.MapCloudHealthChecks();
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
        }

        foreach (var module in Modules)
        {
            module.MapEndpoints(app);
        }

        app.MapPortalAccountEndpoints();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }
}
