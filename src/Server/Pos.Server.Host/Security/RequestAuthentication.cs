using System.Net;
using Microsoft.Extensions.Options;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Server.Host.Database;
using Pos.SharedKernel.Results;

namespace Pos.Server.Host.Security;

/// <summary>Identidad de la petición resuelta por <see cref="RequestAuthenticationMiddleware"/>.</summary>
internal sealed class RequestIdentity
{
    public bool IsLocal { get; init; }

    public IPAddress? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public AuthenticatedDevice? Device { get; init; }

    public AuthenticatedSession? Session { get; set; }
}

/// <summary>
/// Quién llama y desde dónde (D3-04):
/// 1. el propio servidor (localhost) o un equipo emparejado que presenta su credencial;
/// 2. desde la LAN, solo equipos emparejados y solo por HTTPS (Multicaja); la edición Caja Única no atiende la LAN;
/// 3. la sesión (token opaco <c>Authorization: Bearer</c>) debe ser del mismo equipo que la abrió.
/// </summary>
internal sealed class RequestAuthenticationMiddleware(RequestDelegate next)
{
    public const string DeviceIdHeader = "X-Device-Id";
    public const string DeviceSecretHeader = "X-Device-Secret";

    private static readonly string[] OpenToUnpairedDevices = ["/api/v1/devices/pair", "/api/v1/system"];

    public async Task InvokeAsync(
        HttpContext context, IDeviceAuthenticator devices, ISessionAuthenticator sessions, IOptions<DatabaseOptions> database)
    {
        var remote = context.Connection.RemoteIpAddress;
        var isLocal = remote is null || IPAddress.IsLoopback(remote);
        AuthenticatedDevice? device = null;

        if (Guid.TryParse(context.Request.Headers[DeviceIdHeader].ToString(), out var deviceId))
        {
            device = await devices.AuthenticateAsync(deviceId, context.Request.Headers[DeviceSecretHeader].ToString(), context.RequestAborted);
            if (device is null)
            {
                await Reject(context, Error.Unauthorized("DEVICE.INVALID_CREDENTIALS", "La credencial del equipo no es válida o fue revocada."));
                return;
            }
        }

        var path = context.Request.Path;
        if (!isLocal && path.StartsWithSegments("/api/v1"))
        {
            if (!string.Equals(database.Value.Edition, "MULTI", StringComparison.OrdinalIgnoreCase))
            {
                await Reject(context, Error.Forbidden("SECURITY.REMOTE_NOT_ALLOWED", "La edición Caja Única solo atiende al propio equipo."));
                return;
            }

            if (!context.Request.IsHttps)
            {
                await Reject(context, Error.Forbidden("SECURITY.HTTPS_REQUIRED", "Desde la red local solo se admite HTTPS."));
                return;
            }

            if (device is null && !OpenToUnpairedDevices.Any(p => path.StartsWithSegments(p)))
            {
                await Reject(context, Error.Unauthorized("DEVICE.NOT_PAIRED", "Este equipo no está emparejado con el servidor."));
                return;
            }
        }

        var identity = new RequestIdentity
        {
            IsLocal = isLocal,
            IpAddress = remote is { IsIPv4MappedToIPv6: true } ? remote.MapToIPv4() : remote,
            UserAgent = context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            Device = device,
        };
        context.Features.Set(identity);

        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            identity.Session = await sessions.AuthenticateAsync(authorization["Bearer ".Length..].Trim(), new HttpClientContext(context), context.RequestAborted);
        }

        // Seguridad declarada por el endpoint (sesión, permiso, supervisor), antes de leer el cuerpo de la petición.
        if (await EndpointSecurity.AuthorizeAsync(context) is { } denied)
        {
            await denied.ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static Task Reject(HttpContext context, Error error) => error.ToProblem().ExecuteAsync(context);
}

/// <summary>Cliente de la petición en curso.</summary>
internal sealed class HttpClientContext(HttpContext? context) : IClientContext
{
    public HttpClientContext(IHttpContextAccessor accessor)
        : this(accessor.HttpContext)
    {
    }

    private RequestIdentity? Identity => context?.Features.Get<RequestIdentity>();

    public bool IsLocal => Identity?.IsLocal ?? true;

    public Guid? DeviceId => Identity?.Device?.DeviceId;

    public DeviceKind? DeviceKind => Identity?.Device?.Kind;

    public Guid? PosTerminalId => Identity?.Device?.PosTerminalId;

    public IPAddress? IpAddress => Identity?.IpAddress;

    public string? UserAgent => Identity?.UserAgent;
}

/// <summary>Usuario de la sesión en curso (lo usan la auditoría, los permisos y los casos de uso).</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser, ICurrentSecurityVersion
{
    private AuthenticatedSession? Session => accessor.HttpContext?.Features.Get<RequestIdentity>()?.Session;

    public bool IsAuthenticated => Session is not null;

    public Guid? UserId => Session?.UserId;

    public string? DisplayName => Session?.DisplayName;

    public Guid? CompanyId => Session?.CompanyId;

    public Guid? BranchId => Session?.BranchId;

    public Guid? PosTerminalId => Session?.PosTerminalId;

    public Guid? SessionId => Session?.SessionId;

    public bool IsTerminalSession => Session?.IsTerminal ?? false;

    public bool MustChangePassword => Session?.MustChangePassword ?? false;

    public long? SecurityVersion => Session?.SecurityVersion;
}
