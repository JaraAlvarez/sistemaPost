using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Infrastructure;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Application;
using Pos.Infrastructure.Persistence;
using Pos.Server.Migrations;

namespace Pos.Cloud.Host.Hosting;

/// <summary>
/// Arranque de la BD: (opcional) migra con el rol pos_migrator, verifica la versión de esquema, lee el nodo de la nube y
/// registra la clave de firma configurada. Reintenta cada 5 s mientras la BD no esté disponible o no esté migrada.
/// </summary>
internal sealed partial class CloudStartup(
    IServiceScopeFactory scopes,
    NpgsqlDataSource dataSource,
    DatabaseReadiness readiness,
    CloudNodeContext node,
    IOptions<CloudOptions> options,
    ILogger<CloudStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var migrated = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var database = options.Value.Database;
                if (!migrated && database.MigrateOnStartup && !string.IsNullOrWhiteSpace(database.MigratorConnectionString))
                {
                    var report = await CloudDatabase.CreateMigrator().MigrateAsync(database.MigratorConnectionString, AppVersion, stoppingToken);
                    LogMigrated(logger, report.AppliedScripts.Count, report.SchemaVersion);
                    migrated = true;
                }

                var state = await CloudDatabase.CheckAsync(dataSource, stoppingToken);
                if (state.IsCurrent)
                {
                    await node.RefreshAsync(stoppingToken);
                    readiness.Set(DatabaseStatus.Ready, state.Detail, state.DatabaseVersion, state.ExpectedVersion);
                    await using var scope = scopes.CreateAsyncScope();
                    var signing = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new EnsureActiveSigningKeyCommand(), stoppingToken);
                    if (signing.IsFailure || !signing.Value)
                    {
                        LogNoSigning(logger);
                    }

                    LogReady(logger, state.DatabaseVersion);
                    return;
                }

                readiness.Set(state.Reachable ? DatabaseStatus.SchemaOutdated : DatabaseStatus.Unavailable, state.Detail, state.DatabaseVersion, state.ExpectedVersion);
                LogNotReady(logger, state.Detail);
            }
            catch (Exception ex) when (ex is NpgsqlException or MigrationException or TimeoutException or InvalidOperationException)
            {
                readiness.Set(DatabaseStatus.Unavailable, ex.Message);
                LogFailed(logger, ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public static string AppVersion => typeof(CloudStartup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    [LoggerMessage(Level = LogLevel.Information, Message = "Migración de la BD de la nube: {Count} scripts aplicados; versión {Version}")]
    private static partial void LogMigrated(ILogger logger, int count, string? version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Servidor de licencias listo (esquema {Version})")]
    private static partial void LogReady(ILogger logger, string? version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La BD de la nube no está lista: {Detail}")]
    private static partial void LogNotReady(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Critical, Message = "No hay clave de firma utilizable: el portal funciona, pero no se emitirán tokens de licencia")]
    private static partial void LogNoSigning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el arranque de la BD; se reintentará")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>Actualiza cada cierto tiempo los estados de las suscripciones por fecha (vigente → en gracia → vencida).</summary>
internal sealed partial class SubscriptionStatusRefresher(
    IServiceScopeFactory scopes, DatabaseReadiness readiness, IOptions<CloudOptions> options, ILogger<SubscriptionStatusRefresher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.SubscriptionRefreshMinutes)));
        do
        {
            if (!readiness.IsReady)
            {
                continue;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RefreshSubscriptionStatusesCommand(), stoppingToken);
                if (result.IsSuccess && result.Value > 0)
                {
                    LogRefreshed(logger, result.Value);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} suscripciones cambiaron de estado por fecha")]
    private static partial void LogRefreshed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la actualización de estados de las suscripciones")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary><c>/health</c> (proceso + BD + firma), <c>/health/live</c> (solo el proceso) y <c>/health/ready</c>.</summary>
internal static class CloudHealth
{
    public const string LiveTag = "live";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddCloudHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("El servidor está en ejecución."), tags: [LiveTag])
            .AddCheck<DatabaseHealthCheck>("database")
            .AddCheck<SigningHealthCheck>("signing");
        return services;
    }

    public static IEndpointRouteBuilder MapCloudHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteAsync }).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = WriteAsync }).AllowAnonymous();
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains(LiveTag), ResponseWriter = WriteAsync }).AllowAnonymous();
        return endpoints;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            status = report.Status.ToString(),
            version = CloudStartup.AppVersion,
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, Json));
    }

    private sealed class DatabaseHealthCheck(DatabaseReadiness readiness) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(readiness.IsReady ? HealthCheckResult.Healthy(readiness.Detail) : HealthCheckResult.Unhealthy(readiness.Detail));
    }

    private sealed class SigningHealthCheck(ILicenseTokenSigner signer, DatabaseReadiness readiness) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(signer.IsAvailable
                ? HealthCheckResult.Healthy($"Firmando con {signer.PublicKey!.Kid}.")
                : readiness.IsReady
                    ? HealthCheckResult.Degraded("No hay clave de firma utilizable: no se emiten tokens (ver docs/despliegue-nube.md).")
                    : HealthCheckResult.Degraded("Esperando la base de datos."));
    }
}
