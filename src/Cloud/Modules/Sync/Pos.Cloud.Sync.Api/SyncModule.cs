using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Sync.Application;
using Pos.Cloud.Sync.Infrastructure;
using Pos.Sync.Contracts;

namespace Pos.Cloud.Sync.Api;

/// <summary>
/// Módulo de sincronización de la nube (Fase 16): recibe los lotes de las tiendas por la API del POS (autenticados con el token de licencia y la
/// huella) y los paquetes .possync cargados en el portal; guarda los documentos para las consultas del portal del cliente.
/// </summary>
public sealed class SyncModule : IModule
{
    /// <summary>Política de límite de peticiones de la API del POS (la misma que usa el módulo de licencias).</summary>
    public const string PosApiRateLimit = "pos-api";

    public string Name => "sync";

    public void Register(IServiceCollection services, IConfiguration configuration) => SyncInfrastructureRegistration.Register(services);

    public void MapEndpoints(IEndpointRouteBuilder api) =>
        api.MapPost(SyncRoutes.Batches, async (SyncBatch batch, HttpContext http, IDispatcher d, CancellationToken ct) =>
            {
                var authorization = http.Request.Headers.Authorization.ToString();
                if (!AuthenticationHeaderValue.TryParse(authorization, out var header) || header.Scheme != SyncRoutes.AuthorizationScheme
                    || string.IsNullOrWhiteSpace(header.Parameter))
                {
                    return SyncErrors.Unauthorized.ToProblem();
                }

                var result = await d.Send(new ReceiveBatchCommand(batch, header.Parameter, http.Request.Headers[SyncRoutes.FingerprintHeader].ToString()), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem();
            })
            .WithTags("API del POS")
            .RequireRateLimiting(PosApiRateLimit)
            .AllowAnonymous()
            .WithSummary("Lote de sincronización de una tienda: ventas, cierres, existencias y productos (idempotente)");
}
