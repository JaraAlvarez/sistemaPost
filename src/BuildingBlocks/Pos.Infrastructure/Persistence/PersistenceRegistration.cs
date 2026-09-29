using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Files;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Messaging.Behaviors;
using Pos.Infrastructure.Numbering;
using Pos.Infrastructure.Outbox;
using Pos.Infrastructure.Settings;

namespace Pos.Infrastructure.Persistence;

/// <summary>Opciones de persistencia.</summary>
public sealed class PersistenceOptions
{
    /// <summary>Cadena de conexión del rol <c>pos_app</c>.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Edición con la que se crea la fila de instalación si aún no existe.</summary>
    public NodeRole DefaultNodeRole { get; init; } = NodeRole.AllInOne;

    /// <summary>Procesos en segundo plano (sellado de auditoría, outbox). Las pruebas pueden desactivarlos.</summary>
    public bool RunBackgroundServices { get; init; } = true;
}

public static class PersistenceRegistration
{
    /// <summary>
    /// Persistencia de la Fase 2: contexto único, transacción por comando, auditoría con sellado, outbox/inbox,
    /// numeración interna y configuración jerárquica. Llamar DESPUÉS de <c>AddPosInfrastructure</c> (orden del pipeline).
    /// </summary>
    public static IServiceCollection AddPosPersistence(this IServiceCollection services, PersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddPosPersistence(_ => options, options.RunBackgroundServices);
    }

    /// <summary>
    /// Variante con opciones diferidas: se leen al resolver los servicios (con la configuración final del host,
    /// incluidos archivos de la instalación y valores de las pruebas).
    /// </summary>
    public static IServiceCollection AddPosPersistence(
        this IServiceCollection services, Func<IServiceProvider, PersistenceOptions> optionsFactory, bool runBackgroundServices = true)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.AddSingleton<PersistenceOptions>(sp => optionsFactory(sp));
        services.AddSingleton(sp =>
        {
            var builder = new NpgsqlDataSourceBuilder(sp.GetRequiredService<PersistenceOptions>().ConnectionString);
            builder.ConnectionStringBuilder.ApplicationName ??= "pos-server";
            return builder.Build();
        });

        services.AddSingleton<IModelContributor, SystemModelContributor>();
        services.AddScoped<PosSaveChangesInterceptor>();
        services.AddDbContext<PosDbContext>(
            (sp, db) => db
                .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
                .UseSnakeCaseNamingConvention()
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ContributorModelCacheKeyFactory>()
                .AddInterceptors(sp.GetRequiredService<PosSaveChangesInterceptor>()),
            contextLifetime: ServiceLifetime.Scoped,
            optionsLifetime: ServiceLifetime.Scoped);

        services.AddSingleton<IInstallationContext>(sp =>
            new InstallationContext(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<PersistenceOptions>().DefaultNodeRole));
        services.TryAddScoped<ICurrentUser, AnonymousCurrentUser>();
        services.TryAddScoped<IRequestContext, NoRequestContext>();
        services.AddScoped<IActorContext, ActorContext>();
        services.TryAddScoped<IPermissionChecker, PermissiveFase2PermissionChecker>();
        services.TryAddScoped<IClientContext, LocalClientContext>();
        services.AddScoped<IAuthorizationScope, AuthorizationScope>();
        services.TryAddSingleton<ISecretHasher, Security.Argon2idSecretHasher>();
        services.AddScoped<IInstallationSetup, InstallationSetup>();

        services.AddSingleton<DatabaseReadiness>();
        services.AddSingleton<ConstraintErrorTranslator>();
        services.AddScoped<CommitCallbacks>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IOutbox, Outbox.Outbox>();
        services.AddScoped<InboxStore>();
        services.AddScoped<IDocumentNumberAllocator, DocumentNumberAllocator>();
        services.AddScoped<IDocumentSeriesProvisioner, DocumentSeriesProvisioner>();
        services.AddSingleton<ITabularFileReader, Files.TabularFileReader>();
        services.AddSingleton<IDatabaseReadyHook, CompanyInitializersHook>();

        services.AddSingleton<ISettingsCatalog, SettingsCatalog>();
        services.AddSingleton<SettingsCache>();
        services.AddSingleton<ISettingsReader, SettingsReader>();
        services.AddScoped<ISettingsWriter, SettingsWriter>();

        services.AddSingleton<AuditVerifier>();
        services.AddOptions<AuditSealingOptions>();
        services.AddSingleton<AuditSealer>();
        services.AddSingleton<IAuditAnchor, AuditAnchor>();
        services.TryAddSingleton(TimeProvider.System);

        // Pipeline: Logging → Validación → Transacción (esta última envuelve al handler).
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        if (runBackgroundServices)
        {
            services.AddHostedService(sp => sp.GetRequiredService<AuditSealer>());
            services.AddHostedService<OutboxProcessor>();
        }

        return services;
    }
}
