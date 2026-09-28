using System.Globalization;
using Microsoft.Extensions.Hosting.WindowsServices;
using Pos.Server.Host;
using Serilog;

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        // Como servicio, el directorio de trabajo es System32: el contenido se busca junto al ejecutable.
        ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
    });

    builder.AddPosServer();

    var app = builder.Build();
    app.UsePosServer();

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // Falla de arranque: el logger normal puede no existir aún; se escribe a consola y a un archivo de emergencia.
    await using var fatalLogger = new LoggerConfiguration()
        .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
        .WriteTo.File(
            Path.Combine(Path.GetTempPath(), $"{ProductInfo.Name}-startup-error.log"),
            formatProvider: CultureInfo.InvariantCulture)
        .CreateLogger();
    fatalLogger.Fatal(ex, "El servidor no pudo iniciar");
    return 1;
}

/// <summary>Punto de entrada; parcial y público para las pruebas de integración (WebApplicationFactory).</summary>
public partial class Program;
