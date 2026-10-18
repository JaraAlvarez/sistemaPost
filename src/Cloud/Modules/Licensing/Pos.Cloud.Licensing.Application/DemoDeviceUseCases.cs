using System.Net;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

/// <summary>Resultado del registro: primer inicio conocido y si soporte lo reinició.</summary>
public sealed record DemoRegistration(DateTimeOffset StartedAt, bool Reset);

/// <summary>Equipo de la demo visto desde el portal (sin la huella completa: solo el código corto para soporte).</summary>
public sealed record DemoDeviceDto(
    Guid Id, string SupportCode, DateTimeOffset FirstStartedAt, DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt, int Requests, string Status,
    DateTimeOffset? ResetAt, string? ResetReason);

/// <summary>Primer inicio de la demo por equipo (docs/fases/fase-demo-por-equipo.md).</summary>
public interface IDemoDeviceRegistry
{
    /// <summary>
    /// Busca el equipo por su huella (<see cref="DemoDeviceIdentity.SameDevice"/>): si ya existe devuelve su primer inicio (bajándolo
    /// si el POS conoce uno anterior, salvo que soporte lo haya reiniciado); si no, lo registra con <paramref name="startedAt"/>.
    /// <c>null</c> si la IP ya registró hoy el máximo de equipos nuevos. Serializado con un candado por componente de la huella.
    /// </summary>
    Task<DemoRegistration?> RegisterAsync(
        DeviceFingerprint fingerprint, DateTimeOffset startedAt, IPAddress? ipAddress, int newDevicesPerIpPerDay, DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Soporte reinicia la demo del equipo: status RESET con el inicio en <paramref name="now"/>. <c>false</c> si no existe.</summary>
    Task<bool> ResetAsync(Guid id, string reason, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Borra los equipos vistos una sola vez antes de <paramref name="seenBefore"/>. Devuelve cuántos.</summary>
    Task<int> PurgeStaleAsync(DateTimeOffset seenBefore, CancellationToken cancellationToken);

    /// <summary>Equipos más recientes, o los cuyo código de soporte empieza por <paramref name="supportCode"/>.</summary>
    Task<IReadOnlyList<DemoDeviceDto>> ListAsync(string? supportCode, CancellationToken cancellationToken);
}

/// <summary>Parámetros de la demo por equipo (⚙️ sección <c>Licensing:DemoDevices</c>).</summary>
public sealed class DemoDevicesOptions
{
    /// <summary>
    /// Salida del registro de la demo por equipo: ningún inicio anterior es creíble (reloj atrasado al instalar). La nube usa el mayor
    /// entre esta fecha y la de compilación que manda el POS.
    /// </summary>
    public DateTimeOffset Since { get; set; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Equipos NUEVOS que una misma IP puede registrar por día (los ya conocidos no cuentan).</summary>
    public int NewDevicesPerIpPerDay { get; set; } = 20;

    /// <summary>Días sin verse tras los que se borra un equipo visto una sola vez.</summary>
    public int StaleAfterDays { get; set; } = 90;
}

/// <summary>
/// El POS sin licencia informa su huella, el inicio local de la demo y su fecha de compilación; la nube responde, firmado, el primer inicio
/// que conoce para ese equipo y si soporte lo reinició. Anónimo por diseño (lo usa una tienda que todavía no tiene clave), con el límite
/// por IP de la API del POS (host) y un cupo diario de equipos nuevos por IP.
/// </summary>
public sealed record RegisterDemoDeviceCommand(DemoDeviceRequest Request, IPAddress? IpAddress = null) : ICommand<DemoDeviceResponse>;

internal sealed class RegisterDemoDeviceHandler(IDemoDeviceRegistry registry, ILicenseTokenSigner signer, LicensingOptions options, IClock clock)
    : ICommandHandler<RegisterDemoDeviceCommand, DemoDeviceResponse>
{
    public async Task<Result<DemoDeviceResponse>> Handle(RegisterDemoDeviceCommand command, CancellationToken cancellationToken)
    {
        if (!signer.IsAvailable)
        {
            return LicenseApiErrors.SigningUnavailable;
        }

        if (!DeviceFingerprint.TryParse(command.Request?.Fingerprint, out var fingerprint) || !DemoDeviceIdentity.IsUsable(fingerprint))
        {
            return LicenseApiErrors.FingerprintInvalid;
        }

        // El inicio que informa el POS solo puede ADELANTAR el de la nube (un inicio futuro no cuenta: alargaría la demo), y nunca puede ser
        // anterior a la salida del registro ni a la compilación del POS (reloj atrasado al instalar: la demo no quedaría quemada).
        var now = clock.UtcNow;
        var demo = options.DemoDevices;
        var floor = command.Request!.BuildDate is { } built && built <= now && built > demo.Since ? built.ToUniversalTime() : demo.Since;
        var claimed = command.Request.LocalStartedAt is { } local && local <= now ? local.ToUniversalTime() : now;
        if (claimed < floor)
        {
            claimed = floor < now ? floor : now;
        }

        var registered = await registry.RegisterAsync(
            DemoDeviceIdentity.Normalize(fingerprint), claimed, command.IpAddress, demo.NewDevicesPerIpPerDay, now, cancellationToken);
        if (registered is null)
        {
            return LicenseApiErrors.TooManyRequests;
        }

        return new DemoDeviceResponse(signer.SignDemo(new DemoClaims
        {
            DeviceFingerprint = fingerprint.ToString(),
            StartedAt = registered.StartedAt,
            IssuedAt = now,
            Reset = registered.Reset,
        }));
    }
}

/// <summary>Equipos de la demo para el portal (soporte busca por el código que muestra la tienda).</summary>
[RequiresPermission(CloudPermissions.LicensingView)]
public sealed record ListDemoDevicesQuery(string? SupportCode = null) : IQuery<IReadOnlyList<DemoDeviceDto>>;

internal sealed class ListDemoDevicesHandler(IDemoDeviceRegistry registry) : IQueryHandler<ListDemoDevicesQuery, IReadOnlyList<DemoDeviceDto>>
{
    public async Task<Result<IReadOnlyList<DemoDeviceDto>>> Handle(ListDemoDevicesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await registry.ListAsync(request.SupportCode, cancellationToken));
}

/// <summary>
/// "Reiniciar la demo de este equipo" (soporte, solo superadministrador, auditado): para un reloj atrasado al instalar o un equipo
/// clonado. El inicio pasa a ahora y el token de la demo lleva <c>reset</c>: la tienda anula sus marcas locales en el siguiente registro.
/// </summary>
[RequiresPermission(CloudPermissions.DemoReset)]
public sealed record ResetDemoDeviceCommand(Guid DemoDeviceId, string Reason) : ICommand;

internal sealed class ResetDemoDeviceHandler(IDemoDeviceRegistry registry, PortalChange portal, IAuditWriter audit, IClock clock)
    : ICommandHandler<ResetDemoDeviceCommand>
{
    public async Task<Result> Handle(ResetDemoDeviceCommand request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 300)
        {
            return Error.Validation("LICENSING.DEMO_RESET_REASON", "Escriba el motivo del reinicio (máximo 300 caracteres).");
        }

        var context = portal.Create();
        if (!await registry.ResetAsync(request.DemoDeviceId, reason, context.ActorId, clock.UtcNow, cancellationToken))
        {
            return Error.NotFound("LICENSING.DEMO_DEVICE_NOT_FOUND", "El equipo de la demo no existe.");
        }

        await audit.WriteAsync(
            new AuditEntry("licensing", "DEMO_DEVICE_RESET", nameof(DemoDevice), request.DemoDeviceId, "Demo por equipo",
                $"{portal.ActorName} reinició la demo del equipo {request.DemoDeviceId}: {reason}.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }
}

/// <summary>Limpieza de los equipos vistos una sola vez hace más de ⚙️ 90 días (la ejecuta el proceso de fondo del host).</summary>
public sealed record PurgeStaleDemoDevicesCommand : ICommand<int>;

internal sealed class PurgeStaleDemoDevicesHandler(IDemoDeviceRegistry registry, LicensingOptions options, IClock clock)
    : ICommandHandler<PurgeStaleDemoDevicesCommand, int>
{
    public async Task<Result<int>> Handle(PurgeStaleDemoDevicesCommand request, CancellationToken cancellationToken) =>
        Result.Success(await registry.PurgeStaleAsync(clock.UtcNow.AddDays(-Math.Max(30, options.DemoDevices.StaleAfterDays)), cancellationToken));
}
