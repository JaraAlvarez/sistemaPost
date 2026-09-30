using Microsoft.Extensions.Options;
using Pos.Terminal.Agent.Api;
using Pos.Terminal.Agent.Printing;
using Pos.Terminal.Agent.Transports;

namespace Pos.Terminal.Agent;

/// <summary>Composición del agente: servicios (<see cref="AddTerminalAgent"/>) y pipeline HTTP (<see cref="UseTerminalAgent"/>).</summary>
public static class AgentSetup
{
    public static WebApplicationBuilder AddTerminalAgent(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Configuración de la instalación (Fase 15): el instalador de la caja agrega aquí el origen de la interfaz servida por el servidor
        // Multicaja (https://servidor:5443) para que pueda pedir la impresión. Fuera de la carpeta versionada: sobrevive a las actualizaciones.
        builder.Configuration.AddJsonFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AgentInfo.ProductName, "config", "agent.json"), optional: true);
        builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
        builder.Services.AddWindowsService(options => options.ServiceName = AgentInfo.ServiceName);

        // SOLO localhost (127.0.0.1 y ::1), sin importar ASPNETCORE_URLS; el tamaño del cuerpo también se limita en Kestrel.
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            var options = context.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
            kestrel.ListenLocalhost(options.Port);
            kestrel.Limits.MaxRequestBodySize = options.MaxRequestBytes;
            kestrel.AddServerHeader = false;
        });

        builder.Services.ConfigureHttpJsonOptions(json => AgentEndpoints.Configure(json.SerializerOptions));
        builder.Services.AddProblemDetails();
        builder.Services.AddCors();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IPrinterTransportFactory, PrinterTransportFactory>();
        builder.Services.AddSingleton<PrintService>();
        return builder;
    }

    public static WebApplication UseTerminalAgent(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseMiddleware<LocalRequestGuard>();

        // CORS solo para los orígenes configurados (la interfaz de caja del servidor local); el guardián ya rechazó los demás.
        var allowed = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value.AllowedOrigins.Select(o => o.TrimEnd('/')).ToArray();
        app.UseCors(policy => policy.WithOrigins(allowed).WithMethods("GET", "POST").WithHeaders("Content-Type"));
        app.MapAgentEndpoints();
        return app;
    }
}
