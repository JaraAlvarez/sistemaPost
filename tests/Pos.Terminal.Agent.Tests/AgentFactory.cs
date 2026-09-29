using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Terminal.Agent.Api;

namespace Pos.Terminal.Agent.Tests;

/// <summary>Agente en memoria (TestServer) con la carpeta del transporte de archivo en una carpeta temporal propia.</summary>
public sealed class AgentFactory : WebApplicationFactory<Program>
{
    /// <summary>Cabecera de prueba: simula que la petición llega desde esta IP.</summary>
    public const string SimulatedRemoteIpHeader = "X-Test-Remote-Ip";

    public const string UiOrigin = "http://localhost:5480";

    private readonly Dictionary<string, string?> _settings;

    public AgentFactory(IDictionary<string, string?>? settings = null)
    {
        Root = TempDirectory();
        OutputDirectory = Path.Combine(Root, "salida");
        _settings = new Dictionary<string, string?>
        {
            ["Agent:FileOutputDirectory"] = OutputDirectory,
            ["Agent:AllowedOrigins:0"] = UiOrigin,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            _settings[key] = value;
        }
    }

    public static JsonSerializerOptions Json => AgentEndpoints.Json;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string Root { get; }

    public string OutputDirectory { get; }

    public static string TempDirectory() => Path.Combine(Path.GetTempPath(), "pos-agent-tests", Guid.CreateVersion7().ToString("N"));

    public static JsonContent Body(object value) => JsonContent.Create(value, value.GetType(), options: Json);

    /// <summary>Único archivo producido por el transporte de archivo.</summary>
    public byte[] SingleOutput() => File.ReadAllBytes(Directory.GetFiles(OutputDirectory).ShouldHaveSingleItem());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(_settings));
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, SimulatedRemoteStartupFilter>());
    }

    private sealed class SimulatedRemoteStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[SimulatedRemoteIpHeader].ToString(), out var ip))
                {
                    context.Connection.RemoteIpAddress = ip;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
