using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Identity.Contracts;
using Pos.Server.Host.Database;
using Pos.Server.Host.Configuration;
using Pos.Server.Host.Diagnostics;
using Pos.Server.Host.ErrorHandling;
using Pos.Server.Host.Health;
using Pos.Server.Host.Logging;
using Pos.Server.Host.Middleware;
using Pos.Server.Host.Modules;
using Pos.Server.Host.Security;
using Pos.Server.Host.SystemInfo;
using Pos.Server.Host.Updates;
using Pos.SharedKernel.Time;
using Scalar.AspNetCore;
using Serilog;
using Pos.Infrastructure.Security;

namespace Pos.Server.Host;

/// <summary>Composición del servidor: servicios (AddPosServer) y pipeline HTTP (UsePosServer).</summary>
internal static class ServerSetup
{
    public static WebApplicationBuilder AddPosServer(this WebApplicationBuilder builder)
    {
        // Configuración: appsettings → {DataRoot}\config\server.json (instalación) → variables de entorno.
        var paths = ProductPaths.From(builder.Configuration);
        builder.Configuration.AddJsonFile(paths.ServerConfigFile, optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();

        var posOptions = builder.Configuration.GetSection(PosOptions.SectionName).Get<PosOptions>() ?? new PosOptions();
        builder.Services.Configure<PosOptions>(builder.Configuration.GetSection(PosOptions.SectionName));

        builder.Services.AddWindowsService(options => options.ServiceName = ProductInfo.ServerServiceName);

        // HTTP solo en localhost. Multicaja: además HTTPS en la LAN con el certificado de la instalación (D3-05),
        // solo para equipos emparejados (el middleware de autenticación lo exige).
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            var server = context.Configuration.GetSection(PosOptions.SectionName).Get<PosOptions>()?.Server ?? new ServerOptions();
            kestrel.ListenLocalhost(server.Port);
            var edition = context.Configuration[$"{DatabaseOptions.SectionName}:{nameof(DatabaseOptions.Edition)}"];
            if (string.Equals(edition, "MULTI", StringComparison.OrdinalIgnoreCase))
            {
                var certificate = kestrel.ApplicationServices.GetRequiredService<ServerCertificate>().Certificate;
                kestrel.ListenAnyIP(server.LanHttpsPort, listen => listen.UseHttps(certificate));
            }
        });

        builder.Services.AddSerilog(
            (services, logger) => LoggingSetup.Configure(logger, services.GetRequiredService<IConfiguration>()),
            preserveStaticLogger: true);

        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Instance ??= $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
            context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
        });
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

        builder.Services.AddPosHealthChecks();
        builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = $"{ProductInfo.Name} — API del servidor de tienda";
            document.Info.Version = ProductInfo.Version;
            return Task.CompletedTask;
        }));

        builder.Services.AddPosInfrastructure(BusinessTimeZones.Find(posOptions.BusinessTimeZone));
        builder.Services.AddSingleton<ServerRuntime>();

        // Base de datos (Fase 2). DatabaseStartup se registra ANTES que los procesos de fondo de la persistencia:
        // los servicios alojados arrancan en orden y los demás esperan a que la BD esté lista.
        builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
        builder.Services.AddSingleton(sp => new ServerCertificate(
            ProductPaths.From(sp.GetRequiredService<IConfiguration>()), sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Edition));
        builder.Services.AddSingleton<IServerIdentity>(sp => sp.GetRequiredService<ServerCertificate>());
        AddRateLimits(builder.Services);
        builder.Services.AddHostedService<DatabaseStartup>();

        // Instalador y actualizaciones (Fase 13): estado del actualizador, historial auditado y descubrimiento en la LAN (Multicaja).
        builder.Services.AddSingleton(sp => ProductPaths.From(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<IPermissionCatalogProvider, Updates.UpdatePermissionCatalog>();
        builder.Services.AddSingleton<IDatabaseReadyHook, Updates.UpdateHistoryAuditor>();
        builder.Services.AddHostedService<Updates.LanDiscoveryResponder>();
        builder.Services.AddSingleton(sp => new Infrastructure.Backup.BackupEnvironment(
            paths.DataRoot,
            paths.ServerConfigFile,
            ProductInfo.Version,
            ProtectedSecret.Reveal(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.BackupConnectionString)));
        builder.Services.AddSingleton<Infrastructure.Backup.IPgTools>(sp => new Infrastructure.Backup.PgClientTools(
            sp.GetRequiredService<IConfiguration>()[$"{Infrastructure.Backup.BackupToolsOptions.SectionName}:{nameof(Infrastructure.Backup.BackupToolsOptions.PgBinPath)}"]));
        builder.Services.Configure<Infrastructure.Auditing.AuditSealingOptions>(
            builder.Configuration.GetSection(Infrastructure.Auditing.AuditSealingOptions.SectionName));
        builder.Services.AddPosPersistence(sp =>
        {
            var database = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            return new PersistenceOptions
            {
                ConnectionString = ProtectedSecret.Reveal(database.ConnectionString) is { Length: > 0 } connectionString
                    ? connectionString
                    : throw new InvalidOperationException($"Falta la cadena de conexión '{DatabaseOptions.SectionName}:ConnectionString'."),
                DefaultNodeRole = string.Equals(database.Edition, "MULTI", StringComparison.OrdinalIgnoreCase)
                    ? NodeRole.StoreServer
                    : NodeRole.AllInOne,
            };
        });

        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddRequestHandlersFrom(typeof(DevDiagnosticsEndpoints).Assembly);
        }

        foreach (var module in ModuleCatalog.All)
        {
            module.Register(builder.Services, builder.Configuration);
        }

        // Identidad de la petición (Fase 3): reemplazan a los valores por defecto de la infraestructura.
        builder.Services.Replace(ServiceDescriptor.Scoped<HttpCurrentUser, HttpCurrentUser>());
        builder.Services.Replace(ServiceDescriptor.Scoped<ICurrentUser>(sp => sp.GetRequiredService<HttpCurrentUser>()));
        builder.Services.Replace(ServiceDescriptor.Scoped<ICurrentSecurityVersion>(sp => sp.GetRequiredService<HttpCurrentUser>()));
        builder.Services.Replace(ServiceDescriptor.Scoped<IClientContext>(sp => new HttpClientContext(sp.GetRequiredService<IHttpContextAccessor>())));

        return builder;
    }

    /// <summary>
    /// Límites de peticiones por IP para entrar y emparejar (fuerza bruta). Los valores se leen de la configuración
    /// (<c>Pos:Security</c>) en cada petición.
    /// </summary>
    private static void AddRateLimits(IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
                await Pos.SharedKernel.Results.Error.Conflict("SECURITY.TOO_MANY_REQUESTS", "Demasiados intentos. Espere un momento e intente de nuevo.")
                    .ToProblem().ExecuteAsync(context.HttpContext);
            options.AddPolicy(global::Pos.Modules.Identity.Api.AuthRateLimits.Login, context => Partition(context, "LoginPermitsPerMinute", 30, TimeSpan.FromMinutes(1)));
            options.AddPolicy(global::Pos.Modules.Organization.Api.OrganizationModule.DevicePairingRateLimit, context =>
                Partition(context, "PairingPermitsPer15Minutes", 5, TimeSpan.FromMinutes(15)));
        });

    private static RateLimitPartition<string> Partition(HttpContext context, string setting, int defaultPermits, TimeSpan window)
    {
        var permits = context.RequestServices.GetRequiredService<IConfiguration>().GetValue($"Pos:Security:{setting}", defaultPermits);
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "local";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{setting}:{key}", _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });
    }

    public static WebApplication UsePosServer(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();

        // Interfaz de caja y backoffice (Fase 15, D15-02): Blazor WebAssembly servida por el propio servidor, con su política de contenido.
        app.UseMiddleware<ContentSecurityPolicyMiddleware>();
        app.UseBlazorFrameworkFiles();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // El enrutamiento va DESPUÉS de los archivos estáticos: si no, la ruta de reserva de la interfaz se queda con /css, /_framework…
        app.UseRouting();

        app.UseSerilogRequestLogging();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<DatabaseGateMiddleware>();
        app.UseMiddleware<RequestAuthenticationMiddleware>();
        app.UseRateLimiter();

        app.MapPosHealthChecks();

        // Asistente inicial mínimo (Fase 13, D13-05): lo abre el instalador; la Fase 15 lo reemplaza con el diseño definitivo.
        app.MapGet("/instalacion", (IWebHostEnvironment environment) =>
                Results.File(Path.Combine(environment.WebRootPath, "instalacion", "index.html"), "text/html; charset=utf-8"))
            .ExcludeFromDescription();

        var api = app.MapGroup("/api/v1");
        api.MapSystemEndpoints();
        api.MapUpdateEndpoints();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference(options => options.WithTitle($"{ProductInfo.Name} API"));
            api.MapDevDiagnostics();
        }

        foreach (var module in ModuleCatalog.All)
        {
            module.MapEndpoints(api);
        }

        // Rutas de la interfaz (/caja, /admin/…): el enrutador de Blazor decide. La API y la salud nunca caen aquí.
        app.MapFallbackToFile("{*path:regex(^(?!api/|health).*$)}", "index.html");

        // Se crea al arrancar para que el uptime cuente desde el inicio real.
        _ = app.Services.GetRequiredService<ServerRuntime>();
        return app;
    }
}
