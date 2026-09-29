using System.Globalization;
using Pos.Cloud.Host;
using Pos.Cloud.Host.Cli;
using Serilog;

// Consola de operación (mismo ejecutable que el servidor): `Pos.Cloud.Host <comando> [--opción valor]…`.
if (args.Length > 0 && CloudCommands.IsCommand(args[0]))
{
    return await CloudCommands.RunAsync(args, Console.Out, Console.Error);
}

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddCloudHost();

    var app = builder.Build();
    app.UseCloudHost();

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    await using var fatalLogger = new LoggerConfiguration().WriteTo.Console(formatProvider: CultureInfo.InvariantCulture).CreateLogger();
    fatalLogger.Fatal(ex, "El servidor de licencias no pudo iniciar");
    return 1;
}

/// <summary>Punto de entrada; parcial y público para las pruebas de integración (WebApplicationFactory).</summary>
public partial class Program;
