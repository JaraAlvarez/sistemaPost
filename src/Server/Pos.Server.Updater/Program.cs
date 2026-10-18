using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Server.Updater;
using Pos.Updates.Contracts;

// Las llamadas a UpdaterSecurity (ACL y dueños, solo Windows) están todas detrás de OperatingSystem.IsWindows() (variable checkOwners o el if
// de producción en Windows): el analizador no lo ve a través de la variable ni dentro de las lambdas.
#pragma warning disable CA1416

// Uso:
//   Pos.Server.Updater                  Servicio de Windows (BusinessPost-Updater).
//   Pos.Server.Updater discover [--seconds 3]
//                                       Busca servidores Multicaja en la LAN (lo usa el instalador en modo Caja) y los imprime en JSON.
var product = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "PosProductName")?.Value ?? "BusinessPost";

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
// ALTA 1: antes de leer la configuración y de restringir la carpeta (eso cambia los dueños), se descartan los archivos de control que no
// son de SYSTEM ni de los Administradores: los pudo dejar un usuario normal mientras la carpeta no tenía la ACL.
// Integración 1.2.0 (8): como las claves de desarrollo (DevelopmentKeysCompiledIn), el entorno lo cambia cualquier administrador de
// Windows; en Release estas revisiones van SIEMPRE en Windows y solo una compilación Debug fuera de producción las omite.
#if DEBUG
var checkOwners = OperatingSystem.IsWindows() && builder.Environment.IsProduction();
#else
var checkOwners = OperatingSystem.IsWindows();
#endif
var startupWarnings = checkOwners ? UpdaterSecurity.PurgeUntrustedControlFiles(dataRoot) : [];
foreach (var name in new[] { "server.json", "updater.json" })
{
    var file = Path.Combine(dataRoot, "config", name);
    if (!checkOwners || !File.Exists(file) || UpdaterSecurity.IsOwnedByTrusted(file))
    {
        builder.Configuration.AddJsonFile(file, optional: true);
    }
    else
    {
        startupWarnings = [.. startupWarnings, $"Se ignoró {file}: no es de SYSTEM ni de los Administradores."];
    }
}

builder.Services.AddWindowsService(options => options.ServiceName = $"{product}-Updater");
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog();
}

// R5: la carpeta de la instalación sale de la ubicación de este programa ({app}\updater) y de la clave de Inno, no de la configuración.
var installRoot = UpdaterSecurity.ResolveInstallRoot(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? UpdaterSecurity.InnoInstallLocation() : null);
string? securityProblem = installRoot is null
    ? "La carpeta de este actualizador no coincide con la de la instalación registrada: no se instalan actualizaciones."
    : null;
installRoot ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
var settings = UpdaterSettings.From(builder.Configuration, product, dataRoot, installRoot, builder.Environment.IsProduction());
if (checkOwners)
{
    // B1: también la carpeta del actualizador y la de la versión activa (los programas que corren como servicios).
    securityProblem ??= new[] { installRoot, Path.Combine(installRoot, "app"), Path.Combine(installRoot, "updater"), Path.Combine(installRoot, "app", "current") }
        .Where(Directory.Exists)
        .Select(d => UpdaterSecurity.IsReparsePoint(d) && d.EndsWith("current", StringComparison.OrdinalIgnoreCase)
            ? UpdaterSecurity.WritableByUnprivileged(new DirectoryInfo(d).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? d)
            : UpdaterSecurity.WritableByUnprivileged(d))
        .FirstOrDefault(p => p is not null);
    if (settings.Mode == UpdaterMode.Terminal)
    {
        // R5: en la caja adicional la carpeta de datos queda solo para SYSTEM y Administradores (también en equipos instalados antes).
        startupWarnings = [.. startupWarnings, .. UpdaterSecurity.RestrictTerminalDataRoot(dataRoot)];
    }
}

foreach (var aviso in startupWarnings)
{
    Console.Error.WriteLine(aviso);
}

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(UpdateKeys.Load(settings.DevelopmentTrustedKeys));
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IUpdateHost, WindowsUpdateHost>();
    builder.Services.AddHostedService(provider => new UpdateWorker(
        settings, provider.GetRequiredService<IUpdateHost>(), provider.GetRequiredService<Pos.Licensing.Contracts.LicenseKeyRing>(),
        TimeProvider.System, provider.GetRequiredService<ILogger<UpdateWorker>>())
    {
        SecurityProblem = securityProblem,
        IsTrustedFile = checkOwners ? UpdaterSecurity.IsOwnedByTrusted : null,
    });
}

await builder.Build().RunAsync();
return 0;
