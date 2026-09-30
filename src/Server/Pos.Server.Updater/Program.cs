using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Server.Updater;
using Pos.Updates.Contracts;

// Uso:
//   Pos.Server.Updater                  Servicio de Windows (PosSupermercado-Updater).
//   Pos.Server.Updater discover [--seconds 3]
//                                       Busca servidores Multicaja en la LAN (lo usa el instalador en modo Caja) y los imprime en JSON.
var product = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "PosProductName")?.Value ?? "PosSupermercado";

if (args.Length > 0 && args[0] == "discover")
{
    var seconds = args.Length > 2 && args[1] == "--seconds" && int.TryParse(args[2], out var s) ? s : 3;
    var servers = await LanDiscovery.FindAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(servers, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return servers.Count == 0 ? 1 : 0;
}

var builder = Host.CreateApplicationBuilder(args);
var dataRoot = builder.Configuration["Pos:DataRoot"] is { Length: > 0 } root
    ? root
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), product);
builder.Configuration.AddJsonFile(Path.Combine(dataRoot, "config", "server.json"), optional: true);
builder.Configuration.AddJsonFile(Path.Combine(dataRoot, "config", "updater.json"), optional: true);
builder.Services.AddWindowsService(options => options.ServiceName = $"{product}-Updater");
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog();
}

var settings = UpdaterSettings.From(builder.Configuration, product, dataRoot, builder.Environment.IsProduction());
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(UpdateKeys.Load(settings.DevelopmentTrustedKeys));
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IUpdateHost, WindowsUpdateHost>();
    builder.Services.AddHostedService<UpdateWorker>();
}

await builder.Build().RunAsync();
return 0;
