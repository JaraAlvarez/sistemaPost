using System.Text.Json.Serialization;
using Pos.Infrastructure;
using Pos.Server.Host.Configuration;
using Pos.Server.Host.Diagnostics;
using Pos.Server.Host.ErrorHandling;
using Pos.Server.Host.Health;
using Pos.Server.Host.Logging;
using Pos.Server.Host.Middleware;
using Pos.Server.Host.Modules;
using Pos.Server.Host.SystemInfo;
using Pos.SharedKernel.Time;
using Scalar.AspNetCore;
using Serilog;

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

        // Solo localhost hasta la Fase 13 (HTTPS en LAN con emparejamiento de cajas).
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(posOptions.Server.Port));

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

        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddRequestHandlersFrom(typeof(DevDiagnosticsEndpoints).Assembly);
        }

        foreach (var module in ModuleCatalog.All)
        {
            module.Register(builder.Services, builder.Configuration);
        }

        return builder;
    }

    public static WebApplication UsePosServer(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging();
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.MapPosHealthChecks();

        var api = app.MapGroup("/api/v1");
        api.MapSystemEndpoints();

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

        // Se crea al arrancar para que el uptime cuente desde el inicio real.
        _ = app.Services.GetRequiredService<ServerRuntime>();
        return app;
    }
}
