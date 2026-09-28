using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pos.Api.Abstractions;

/// <summary>
/// Punto de entrada de un módulo de negocio. El host solo conoce esta interfaz:
/// agregar un módulo = implementarla y añadirla al catálogo de módulos del host.
/// </summary>
public interface IModule
{
    /// <summary>Nombre corto y estable del módulo (p. ej. <c>catalog</c>); se usa en rutas, logs y auditoría.</summary>
    string Name { get; }

    void Register(IServiceCollection services, IConfiguration configuration);

    /// <summary>Mapea los endpoints del módulo sobre el grupo <c>/api/v1</c>.</summary>
    void MapEndpoints(IEndpointRouteBuilder api);
}
