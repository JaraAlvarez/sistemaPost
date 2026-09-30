using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>Registro del cliente de Factus. Lo conecta el módulo Billing (no se registra solo).</summary>
public static class FactusServiceCollectionExtensions
{
    /// <summary>Nombre del cliente HTTP (API y autenticación comparten configuración y manejador).</summary>
    public const string HttpClientName = "Factus";

    /// <summary>
    /// Registra <see cref="IFactusApi"/> como cliente tipado de IHttpClientFactory, el token en memoria (singleton, uno por juego de
    /// credenciales y ambiente) y <see cref="IFactusApiFactory"/> (un cliente por petición con las credenciales de la empresa).
    /// Quien lo conecte debe registrar su <see cref="IFactusOptionsProvider"/> (credenciales descifradas); si no hay ninguno,
    /// queda uno que devuelve "no configurado". Devuelve el <see cref="IHttpClientBuilder"/> para ajustar el manejador
    /// (p. ej. <c>ConfigurePrimaryHttpMessageHandler</c> con el Factus simulado en las pruebas).
    /// </summary>
    public static IHttpClientBuilder AddFactusApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFactusOptionsProvider>(new StaticFactusOptionsProvider(null));
        services.TryAddSingleton<FactusTokenManager>();
        services.TryAddSingleton<FactusConnectionOptions>();
        services.TryAddSingleton<IFactusApiFactory, FactusApiFactory>();

        // El tiempo de espera lo controla cada petición (FactusOptions.RequestTimeout, configurable en ejecución).
        return services.AddHttpClient<IFactusApi, FactusApiClient>(HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
    }
}
