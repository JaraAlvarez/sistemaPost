using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Infrastructure.Identifiers;
using Pos.Infrastructure.Licensing;
using Pos.Infrastructure.Messaging;
using Pos.Infrastructure.Messaging.Behaviors;
using Pos.Infrastructure.Time;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Servicios transversales: reloj, IDs, despachador con su pipeline y feature gate provisional.</summary>
    public static IServiceCollection AddPosInfrastructure(this IServiceCollection services, TimeZoneInfo businessTimeZone)
    {
        ArgumentNullException.ThrowIfNull(businessTimeZone);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock>(sp => new SystemClock(sp.GetRequiredService<TimeProvider>(), businessTimeZone));
        services.TryAddSingleton<IIdGenerator, UuidV7IdGenerator>();
        services.TryAddSingleton<IFeatureGate, AllowAllFeatureGate>();

        services.TryAddScoped<IDispatcher, Dispatcher>();

        // Orden del pipeline (el primero es el más externo). Fase 2 agrega transacción y auditoría.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        return services;
    }

    /// <summary>
    /// Registra los handlers y validadores de un ensamblado (cada módulo llama a este método con su capa Application).
    /// Una petición debe tener exactamente un handler.
    /// </summary>
    public static IServiceCollection AddRequestHandlersFrom(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var registrations = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                .Select(i => (Service: i, Implementation: t)));

        foreach (var (service, implementation) in registrations)
        {
            if (services.Any(d => d.ServiceType == service))
            {
                throw new InvalidOperationException(
                    $"La petición '{service.GetGenericArguments()[0].FullName}' tiene más de un handler.");
            }

            services.AddScoped(service, implementation);
        }

        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped, includeInternalTypes: true);
        return services;
    }
}
