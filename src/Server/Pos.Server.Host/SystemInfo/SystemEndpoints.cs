using Microsoft.Extensions.Hosting.WindowsServices;
using Pos.Api.Abstractions;
using Pos.SharedKernel.Time;

namespace Pos.Server.Host.SystemInfo;

/// <summary>Información del servidor: versión, entorno, hora y tiempo en ejecución.</summary>
public sealed record SystemInfoResponse(
    string Product,
    string Version,
    string Environment,
    bool RunningAsWindowsService,
    DateTimeOffset ServerTimeUtc,
    DateTimeOffset BusinessTime,
    DateOnly BusinessDate,
    string BusinessTimeZone,
    DateTimeOffset StartedAtUtc,
    double UptimeSeconds);

/// <summary>Momento de arranque del servidor (para calcular el uptime).</summary>
internal sealed class ServerRuntime(IClock clock)
{
    public DateTimeOffset StartedAtUtc { get; } = clock.UtcNow;
}

internal static class SystemEndpoints
{
    public static RouteGroupBuilder MapSystemEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/system").WithTags("Sistema");

        group.MapGet("/info", GetInfo)
            .WithName("GetSystemInfo")
            .AllowAnonymousByDesign("Diagnóstico técnico: versión y hora, sin datos del negocio.")
            .WithSummary("Versión del servidor, entorno y hora del negocio.");

        return api;
    }

    private static SystemInfoResponse GetInfo(IClock clock, ServerRuntime runtime, IHostEnvironment environment)
    {
        var now = clock.UtcNow;
        return new SystemInfoResponse(
            ProductInfo.Name,
            ProductInfo.Version,
            environment.EnvironmentName,
            WindowsServiceHelpers.IsWindowsService(),
            now,
            clock.ToBusinessTime(now),
            clock.BusinessDateOf(now),
            clock.BusinessTimeZone.Id,
            runtime.StartedAtUtc,
            Math.Round((now - runtime.StartedAtUtc).TotalSeconds, 0));
    }
}
