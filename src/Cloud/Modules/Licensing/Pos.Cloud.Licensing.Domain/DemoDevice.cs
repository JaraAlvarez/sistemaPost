using System.Net;
using Pos.SharedKernel.Domain;

namespace Pos.Cloud.Licensing.Domain;

/// <summary>
/// Primer inicio de la demostración de un equipo (demo por equipo, docs/fases/fase-demo-por-equipo.md), identificado por los hashes de
/// su huella. Lo escribe <c>DemoDeviceRegistry</c> con SQL directo (serializado con un candado); aquí solo se mapea para el modelo y las
/// consultas del portal.
/// </summary>
public sealed class DemoDevice : Entity<Guid>
{
    private DemoDevice(Guid id)
        : base(id)
    {
    }

    public string? BoardHash { get; private set; }

    public string? DiskHash { get; private set; }

    public string? MachineHash { get; private set; }

    public DateTimeOffset FirstStartedAt { get; private set; }

    public DateTimeOffset RegisteredAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public int Requests { get; private set; }

    /// <summary>IP que registró el equipo (cupo diario de equipos nuevos por IP).</summary>
    public IPAddress? RegisteredIp { get; private set; }

    /// <summary><c>ACTIVE</c> o <c>RESET</c> (soporte reinició la demo de este equipo).</summary>
    public string Status { get; private set; } = "ACTIVE";

    public DateTimeOffset? ResetAt { get; private set; }

    public Guid? ResetBy { get; private set; }

    public string? ResetReason { get; private set; }
}
