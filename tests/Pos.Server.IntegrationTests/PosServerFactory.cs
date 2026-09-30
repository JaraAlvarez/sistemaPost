using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pos.Server.IntegrationTests;

/// <summary>
/// Servidor en memoria (TestServer) con su propia carpeta de datos temporal y su propia base de datos PostgreSQL
/// real, creada y migrada con el migrador del producto.
/// </summary>
public class PosServerFactory : WebApplicationFactory<Program>
{
    /// <summary>Cabecera de prueba: simula que la petición llega desde esta IP de la LAN.</summary>
    public const string SimulatedRemoteIpHeader = "X-Test-Remote-Ip";

    /// <summary>Cabecera de prueba: simula que la petición llegó por HTTPS.</summary>
    public const string SimulatedHttpsHeader = "X-Test-Https";

    private readonly Lazy<string> _connectionString = new(() => TestPostgres.CreateMigratedDatabaseAsync().GetAwaiter().GetResult());

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.CreateVersion7().ToString("N"));

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>Cadena de conexión (rol pos_app) de la BD de este servidor.</summary>
    public string ConnectionString => _connectionString.Value;

    /// <summary>Edición instalada: SINGLE (Caja Única) o MULTI (Multicaja).</summary>
    protected virtual string Edition => "MULTI";

    /// <summary>Configuración adicional de una fábrica derivada.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pos:DataRoot"] = DataRoot,
            ["Pos:Database:ConnectionString"] = ConnectionString,
            ["Pos:Database:MigrateOnStartup"] = "false",
            ["Pos:Database:Edition"] = Edition,
            ["Pos:Security:LoginPermitsPerMinute"] = "10000",
            ["Pos:Security:PairingPermitsPer15Minutes"] = "10000",
            // Sin descubrimiento UDP en las pruebas: evita el aviso del firewall de Windows y choques de puerto entre servidores de prueba.
            ["Pos:Server:LanDiscovery"] = "false",
        }).AddInMemoryCollection(ExtraSettings));
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, SimulatedNetworkStartupFilter>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(DataRoot, recursive: true);
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    /// <summary>TestServer no tiene red: estas cabeceras simulan un equipo de la LAN y una conexión HTTPS.</summary>
    private sealed class SimulatedNetworkStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[SimulatedRemoteIpHeader].ToString(), out var ip))
                {
                    context.Connection.RemoteIpAddress = ip;
                }

                if (context.Request.Headers.ContainsKey(SimulatedHttpsHeader))
                {
                    context.Request.Scheme = "https";
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>Servidor con la edición Caja Única.</summary>
public sealed class SingleTerminalServerFactory : PosServerFactory
{
    protected override string Edition => "SINGLE";
}

/// <summary>Caja Única con el sellado de la auditoría acelerado: el reporte Z lleva un sello real en segundos.</summary>
public sealed class CashServerFactory : PosServerFactory
{
    protected override string Edition => "SINGLE";

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Pos:Audit:Interval", "00:00:00.500"),
        new("Pos:Audit:SafetyHorizon", "00:00:02"),
    ];
}
