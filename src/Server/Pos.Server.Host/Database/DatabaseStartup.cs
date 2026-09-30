using Microsoft.Extensions.Options;
using Npgsql;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Persistence;
using Pos.Server.Migrations;
using Pos.Infrastructure.Security;

namespace Pos.Server.Host.Database;

/// <summary>
/// Arranque de la base de datos. En producción NO migra: comprueba que la versión del esquema sea la esperada; si no,
/// el servidor queda en pie pero no atiende negocio (503 y /health/ready Unhealthy). Si la BD no responde, reintenta
/// cada 10 s sin detener el servicio.
/// </summary>
internal sealed partial class DatabaseStartup(
    IOptions<DatabaseOptions> options,
    NpgsqlDataSource dataSource,
    IInstallationContext installation,
    DatabaseReadiness readiness,
    IEnumerable<IDatabaseReadyHook> hooks,
    IServiceScopeFactory scopes,
    ILoggerFactory loggerFactory,
    ILogger<DatabaseStartup> logger) : BackgroundService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        // El primer intento se espera: el servidor empieza a atender con el estado real de la BD.
        await _firstAttempt.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TryInitializeAsync(stoppingToken);
            _firstAttempt.TrySetResult();
            if (readiness.Status is DatabaseStatus.Ready or DatabaseStatus.SchemaOutdated)
            {
                return;
            }

            await Task.Delay(RetryInterval, stoppingToken);
        }
    }

    internal async Task TryInitializeAsync(CancellationToken cancellationToken)
    {
        var expected = ScriptCatalog.Default.LatestVersion;
        try
        {
            if (options.Value.MigrateOnStartup && !string.IsNullOrWhiteSpace(options.Value.MigratorConnectionString))
            {
                await new DatabaseMigrator(ScriptCatalog.Default, loggerFactory.CreateLogger<DatabaseMigrator>())
                    .MigrateAsync(ProtectedSecret.Reveal(options.Value.MigratorConnectionString)!, ProductInfo.Version, cancellationToken);
            }

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var version = await DatabaseMigrator.GetDatabaseVersionAsync(connection, cancellationToken);
            if (version != expected)
            {
                readiness.Set(
                    DatabaseStatus.SchemaOutdated,
                    $"La base de datos está en la versión {version ?? "(sin migrar)"} y la aplicación espera {expected}. Ejecute el migrador.",
                    version,
                    expected);
                LogSchemaOutdated(logger, version, expected);
                return;
            }

            await installation.RefreshAsync(cancellationToken);
            if (installation.IsSetupCompleted)
            {
                foreach (var hook in hooks)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await hook.RunAsync(scope.ServiceProvider, cancellationToken);
                }
            }

            readiness.Set(DatabaseStatus.Ready, "Base de datos lista.", version, expected);
            LogReady(logger, version, installation.NodeId, installation.IsSetupCompleted);
        }
        catch (Exception ex) when (ex is NpgsqlException or MigrationException or TimeoutException)
        {
            readiness.Set(DatabaseStatus.Unavailable, $"No se pudo usar la base de datos: {ex.Message}", null, expected);
            LogUnavailable(logger, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un inicializador que falla no debe dejar el arranque esperando para siempre: se registra y se reintenta.
            readiness.Set(DatabaseStatus.Unavailable, $"Falló la inicialización de la base de datos: {ex.Message}", null, expected);
            LogInitializationFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Base de datos lista: esquema {Version}, nodo {NodeId}, configurada: {SetupCompleted}")]
    private static partial void LogReady(ILogger logger, string? version, Guid nodeId, bool setupCompleted);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Esquema desactualizado: BD {Version}, esperado {Expected}. El servidor no atenderá operaciones de negocio.")]
    private static partial void LogSchemaOutdated(ILogger logger, string? version, string? expected);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Falló la inicialización de la base de datos (inicializadores de los módulos); se reintentará")]
    private static partial void LogInitializationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Base de datos no disponible; se reintentará")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}
