using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Pos.Server.IntegrationTests;

/// <summary>Servidor en memoria (TestServer) con su propia carpeta de datos temporal.</summary>
public sealed class PosServerFactory : WebApplicationFactory<Program>
{
    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.CreateVersion7().ToString("N"));

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pos:DataRoot"] = DataRoot,
        }));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
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
