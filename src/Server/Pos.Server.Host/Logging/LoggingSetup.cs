using Pos.Server.Host.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Pos.Server.Host.Logging;

internal static class LoggingSetup
{
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {CorrelationId}{NewLine}{Exception}";

    /// <summary>
    /// Consola legible + archivo JSON rotativo diario (30 días) en <c>{DataRoot}\logs</c>.
    /// Los niveles se pueden ajustar en la sección <c>Serilog</c> de la configuración.
    /// </summary>
    public static void Configure(LoggerConfiguration logger, IConfiguration configuration)
    {
        var paths = ProductPaths.From(configuration);

        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", ProductInfo.Name)
            .Enrich.WithProperty("Version", ProductInfo.Version)
            .Enrich.With<SensitiveDataMaskingEnricher>()
            .WriteTo.Console(outputTemplate: ConsoleTemplate, formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(paths.LogsDirectory, "server-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true);
    }
}
