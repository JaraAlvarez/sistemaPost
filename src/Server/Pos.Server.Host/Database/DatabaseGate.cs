using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Results;

namespace Pos.Server.Host.Database;

/// <summary>
/// Mientras la BD no está lista, los endpoints de negocio responden 503 con un código estable. <c>/health</c> y
/// <c>/api/v1/system</c> siguen respondiendo para el diagnóstico.
/// </summary>
internal sealed class DatabaseGateMiddleware(RequestDelegate next)
{
    public const string SchemaOutdatedCode = "SYSTEM.SCHEMA_OUTDATED";
    public const string DatabaseUnavailableCode = "SYSTEM.DATABASE_UNAVAILABLE";

    public async Task InvokeAsync(HttpContext context, DatabaseReadiness readiness)
    {
        var path = context.Request.Path;
        if (readiness.IsReady
            || !path.StartsWithSegments("/api/v1")
            || path.StartsWithSegments("/api/v1/system")
            || path.StartsWithSegments("/api/v1/dev"))
        {
            await next(context);
            return;
        }

        var error = readiness.Status == DatabaseStatus.SchemaOutdated
            ? Error.Unexpected(SchemaOutdatedCode, readiness.Detail)
            : Error.Unexpected(DatabaseUnavailableCode, readiness.Detail);
        var problem = TypedResults.Problem(
            title: "Servicio no disponible",
            detail: error.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?> { [ResultHttpExtensions.ErrorCodeExtension] = error.Code });
        await problem.ExecuteAsync(context);
    }
}

/// <summary>/health/ready: estado de la BD y versión del esquema.</summary>
internal sealed class DatabaseHealthCheck(DatabaseReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["status"] = readiness.Status.ToString(),
            ["schemaVersion"] = readiness.SchemaVersion ?? "(desconocida)",
            ["expectedVersion"] = readiness.ExpectedVersion ?? "(desconocida)",
        };
        return Task.FromResult(readiness.IsReady
            ? HealthCheckResult.Healthy(readiness.Detail, data)
            : HealthCheckResult.Unhealthy(readiness.Detail, data: data));
    }
}

/// <summary>Datos de la petición HTTP que se guardan en la auditoría.</summary>
internal sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public const string DeviceHeader = "X-Device-Id";

    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;

    public IPAddress? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress is { } ip && ip.IsIPv4MappedToIPv6
        ? ip.MapToIPv4()
        : accessor.HttpContext?.Connection.RemoteIpAddress;

    public Guid? DeviceId =>
        Guid.TryParse(accessor.HttpContext?.Request.Headers[DeviceHeader].ToString(), out var id) ? id : null;
}
