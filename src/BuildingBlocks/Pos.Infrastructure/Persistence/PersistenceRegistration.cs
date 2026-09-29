using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
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
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
        builder.ConnectionStringBuilder.ApplicationName ??= "pos-server";
        services.AddSingleton(builder.Build());

        services.AddSingleton<IModelContributor, SystemModelContributor>();
        services.AddScoped<PosSaveChangesInterceptor>();
        services.AddDbContext<PosDbContext>(
            (sp, db) => db
                .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<PosSaveChangesInterceptor>()),
            contextLifetime: ServiceLifetime.Scoped,
            optionsLifetime: ServiceLifetime.Scoped);

        services.AddSingleton<IInstallationContext>(sp =>
            new InstallationContext(sp.GetRequiredService<NpgsqlDataSource>(), options.DefaultNodeRole));
        services.TryAddScoped<ICurrentUser, AnonymousCurrentUser>();
        services.TryAddScoped<IRequestContext, NoRequestContext>();
        services.AddScoped<IActorContext, ActorContext>();

        services.AddSingleton<ConstraintErrorTranslator>();
        services.AddScoped<CommitCallbacks>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IOutbox, Outbox.Outbox>();
        services.AddScoped<InboxStore>();
        services.AddScoped<IDocumentNumberAllocator, DocumentNumberAllocator>();
        services.AddScoped<IDocumentSeriesProvisioner, DocumentSeriesProvisioner>();

        services.AddSingleton<ISettingsCatalog, SettingsCatalog>();
        services.AddSingleton<SettingsCache>();
        services.AddSingleton<ISettingsReader, SettingsReader>();
        services.AddScoped<ISettingsWriter, SettingsWriter>();

        services.AddSingleton<AuditVerifier>();
        services.AddOptions<AuditSealingOptions>();
        services.AddSingleton<AuditSealer>();
        services.TryAddSingleton(TimeProvider.System);

        // Pipeline: Logging → Validación → Transacción (esta última envuelve al handler).
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        if (options.RunBackgroundServices)
        {
            services.AddHostedService(sp => sp.GetRequiredService<AuditSealer>());
            services.AddHostedService<OutboxProcessor>();
        }

        return services;
    }
}
