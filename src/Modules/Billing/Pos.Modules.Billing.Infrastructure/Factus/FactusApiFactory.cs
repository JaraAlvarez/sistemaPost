using Microsoft.Extensions.Logging;

namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>
/// Ajustes técnicos (no secretos) de la conexión con Factus: URL base explícita — solo fuera de producción, para el Factus simulado —
/// y tiempo máximo por petición. Lo lee el módulo Billing de <c>Pos:Billing:Factus</c>; las pruebas pueden cambiarlo en caliente.
/// </summary>
public sealed class FactusConnectionOptions
{
    /// <summary>URL base que reemplaza la del ambiente (nula: sandbox o producción según la empresa).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Tiempo máximo por petición (Factus valida una factura en ≈2,5 s).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Crea un cliente de Factus con las opciones de UNA empresa y ambiente (un <see cref="IFactusOptionsProvider"/> por petición): las
/// credenciales llegan descifradas en la llamada y no quedan guardadas en el cliente; el token vive en memoria en el
/// <see cref="FactusTokenManager"/>, separado por credenciales y ambiente.
/// </summary>
public interface IFactusApiFactory
{
    IFactusApi Create(FactusOptions options);
}

internal sealed class FactusApiFactory(
    IHttpClientFactory httpClientFactory, FactusTokenManager tokens, TimeProvider time, ILogger<FactusApiClient> logger) : IFactusApiFactory
{
    public IFactusApi Create(FactusOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new FactusApiClient(
            httpClientFactory.CreateClient(FactusServiceCollectionExtensions.HttpClientName), new StaticFactusOptionsProvider(options), tokens, time, logger);
    }
}
