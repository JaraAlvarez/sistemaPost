using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

/// <summary>Primer inicio de la demo por equipo (docs/fases/fase-demo-por-equipo.md).</summary>
public interface IDemoDeviceRegistry
{
    /// <summary>
    /// Busca el equipo por su huella (2 de 3 componentes): si ya existe devuelve su primer inicio (el más antiguo entre los registros que
    /// coinciden, bajándolo si el POS conoce uno anterior); si no, lo registra con <paramref name="startedAt"/>. Serializado: dos
    /// consultas a la vez del mismo equipo no crean dos registros.
    /// </summary>
    Task<DateTimeOffset> RegisterAsync(DeviceFingerprint fingerprint, DateTimeOffset startedAt, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// El POS sin licencia informa su huella y el inicio local de la demo; la nube responde, firmado, el primer inicio que conoce para ese
/// equipo. Anónimo por diseño (lo usa una tienda que todavía no tiene clave), con el límite por IP de la API del POS (host).
/// </summary>
public sealed record RegisterDemoDeviceCommand(DemoDeviceRequest Request) : ICommand<DemoDeviceResponse>;

internal sealed class RegisterDemoDeviceHandler(IDemoDeviceRegistry registry, ILicenseTokenSigner signer, IClock clock)
    : ICommandHandler<RegisterDemoDeviceCommand, DemoDeviceResponse>
{
    /// <summary>Un inicio local más viejo que esto no es creíble (reloj absurdo): se registra desde hoy.</summary>
    private static readonly TimeSpan MaxClaimedAge = TimeSpan.FromDays(3650);

    public async Task<Result<DemoDeviceResponse>> Handle(RegisterDemoDeviceCommand command, CancellationToken cancellationToken)
    {
        if (!signer.IsAvailable)
        {
            return LicenseApiErrors.SigningUnavailable;
        }

        if (!DeviceFingerprint.TryParse(command.Request?.Fingerprint, out var fingerprint) || !fingerprint.IsUsable)
        {
            return LicenseApiErrors.FingerprintInvalid;
        }

        // El inicio que informa el POS solo puede ADELANTAR el de la nube (un inicio futuro no cuenta: alargaría la demo).
        var now = clock.UtcNow;
        var claimed = command.Request!.LocalStartedAt is { } local && local <= now && local >= now - MaxClaimedAge ? local.ToUniversalTime() : now;
        var startedAt = await registry.RegisterAsync(fingerprint, claimed, now, cancellationToken);
        return new DemoDeviceResponse(signer.SignDemo(new DemoClaims
        {
            DeviceFingerprint = fingerprint.ToString(),
            StartedAt = startedAt,
            IssuedAt = now,
        }));
    }
}
