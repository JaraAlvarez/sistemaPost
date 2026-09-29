using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Infrastructure.Auditing;
using Pos.Cloud.Infrastructure.Messaging;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Migrations;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Persistence;
using Pos.Server.Migrations;

namespace Pos.Cloud.Infrastructure;

/// <summary>Opciones de la persistencia de la nube.</summary>
public sealed class CloudPersistenceOptions
{
    /// <summary>Cadena de conexión del rol <c>pos_app</c> de la BD de la nube.</summary>
    public required string ConnectionString { get; init; }
}

public static class CloudInfrastructure
{
    /// <summary>
    /// Persistencia de la nube: contexto, transacción por comando, permisos del portal en el pipeline, auditoría encadenada con
    /// sellado. Llamar DESPUÉS de <c>AddPosInfrastructure</c> (orden del pipeline: registro → validación → permiso → transacción).
    /// <paramref name="runBackgroundServices"/> = sellado de la auditoría en segundo plano (la consola lo desactiva).
    /// </summary>
    public static IServiceCollection AddCloudPersistence(
        this IServiceCollection services, Func<IServiceProvider, CloudPersistenceOptions> optionsFactory, bool runBackgroundServices = true)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.AddSingleton<CloudPersistenceOptions>(sp => optionsFactory(sp));
        services.AddSingleton(sp =>
        {
            var builder = new NpgsqlDataSourceBuilder(sp.GetRequiredService<CloudPersistenceOptions>().ConnectionString);
            builder.ConnectionStringBuilder.ApplicationName ??= "pos-cloud";
            return builder.Build();
        });

        services.AddScoped<CloudSaveChangesInterceptor>();
        services.AddDbContext<CloudDbContext>(
            (sp, db) => db
                .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
                .UseSnakeCaseNamingConvention()
                .ReplaceService<IModelCacheKeyFactory, CloudModelCacheKeyFactory>()
                .AddInterceptors(sp.GetRequiredService<CloudSaveChangesInterceptor>()),
            contextLifetime: ServiceLifetime.Scoped,
            optionsLifetime: ServiceLifetime.Scoped);

        services.AddSingleton<CloudNodeContext>();
        services.AddSingleton<IInstallationContext>(sp => sp.GetRequiredService<CloudNodeContext>());
        services.TryAddScoped<PortalUserContext>();
        services.TryAddScoped<IPortalUserContext>(sp => sp.GetRequiredService<PortalUserContext>());
        services.TryAddScoped<IRequestContext, NoCloudRequestContext>();
        services.TryAddSingleton<ISecretHasher, Pos.Infrastructure.Security.Argon2idSecretHasher>();

        services.AddSingleton<DatabaseReadiness>();
        services.AddSingleton<ConstraintErrorTranslator>();
        services.AddScoped<IUnitOfWork, CloudUnitOfWork>();
        services.AddScoped<CloudAuditWriter>();
        services.AddScoped<IAuditWriter>(sp => sp.GetRequiredService<CloudAuditWriter>());
        services.AddScoped<IAttributedAuditWriter>(sp => sp.GetRequiredService<CloudAuditWriter>());
        services.AddSingleton<AuditVerifier>();
        services.AddSingleton<ICloudAuditLog, CloudAuditLog>();
        services.AddOptions<AuditSealingOptions>();
        services.AddSingleton<AuditSealer>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PortalPermissionBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(CloudTransactionBehavior<,>));

        if (runBackgroundServices)
        {
            services.AddHostedService(sp => sp.GetRequiredService<AuditSealer>());
        }

        return services;
    }
}

internal sealed class CloudUnitOfWork(CloudDbContext context) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
}

/// <summary>Estado del esquema de la BD de la nube frente a los scripts de esta versión.</summary>
public sealed record CloudSchemaState(bool Reachable, string? DatabaseVersion, string? ExpectedVersion, string Detail)
{
    public bool IsCurrent => Reachable && DatabaseVersion is not null && DatabaseVersion == ExpectedVersion;
}

/// <summary>Creación, migración y verificación de la BD de la nube con el migrador SQL-first del producto.</summary>
public static class CloudDatabase
{
    public static DatabaseMigrator CreateMigrator(Microsoft.Extensions.Logging.ILogger<DatabaseMigrator>? logger = null) =>
        new(CloudScripts.Catalog, logger);

    public static async Task<CloudSchemaState> CheckAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var expected = CloudScripts.Catalog.LatestVersion;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var version = await DatabaseMigrator.GetDatabaseVersionAsync(connection, cancellationToken);
            return version == expected
                ? new CloudSchemaState(true, version, expected, "Base de datos lista.")
                : new CloudSchemaState(true, version, expected, $"La BD está en la versión {version ?? "(sin migrar)"} y esta versión espera {expected}: ejecute la migración.");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return new CloudSchemaState(false, null, expected, $"No se pudo conectar a la base de datos: {ex.Message}");
        }
    }
}
