using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Pos.Server.IntegrationTests;

/// <summary>
/// Servidor en memoria (TestServer) con su propia carpeta de datos temporal y su propia base de datos PostgreSQL
/// real, creada y migrada con el migrador del producto.
/// </summary>
public class PosServerFactory : WebApplicationFactory<Program>
{
    private readonly Lazy<string> _connectionString = new(() => TestPostgres.CreateMigratedDatabaseAsync().GetAwaiter().GetResult());

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.CreateVersion7().ToString("N"));

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>Cadena de conexión (rol pos_app) de la BD de este servidor.</summary>
    public string ConnectionString => _connectionString.Value;

    /// <summary>Edición instalada: SINGLE (Caja Única) o MULTI (Multicaja).</summary>
    protected virtual string Edition => "MULTI";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pos:DataRoot"] = DataRoot,
            ["Pos:Database:ConnectionString"] = ConnectionString,
            ["Pos:Database:MigrateOnStartup"] = "false",
            ["Pos:Database:Edition"] = Edition,
        }));
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
}

/// <summary>Servidor con la edición Caja Única.</summary>
public sealed class SingleTerminalServerFactory : PosServerFactory
{
    protected override string Edition => "SINGLE";
}
