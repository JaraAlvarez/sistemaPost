using System.Net;
using System.Text.Json;

namespace Pos.Infrastructure.Auditing;

/// <summary><c>audit.audit_log</c>: fila de la bitácora. Solo se inserta; nunca se modifica.</summary>
public sealed class AuditLogRecord
{
    public Guid Id { get; set; }

    /// <summary>Instante UTC truncado a microsegundos (precisión de PostgreSQL) antes de calcular el hash.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public Guid NodeId { get; set; }

    /// <summary>Consecutivo del nodo. 0 = aún no asignado (se reserva al guardar, dentro de la transacción).</summary>
    public long Seq { get; set; }

    public short HashVersion { get; set; } = AuditHasher.CurrentVersion;

    public Guid? CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public Guid? PosTerminalId { get; set; }

    public Guid? UserId { get; set; }

    public string? UserDisplayName { get; set; }

    public Guid? SessionId { get; set; }

    public Guid? DeviceId { get; set; }

    public IPAddress? IpAddress { get; set; }

    public string? CorrelationId { get; set; }

    public string Module { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public string? EntityLabel { get; set; }

    public JsonDocument? OldValues { get; set; }

    public JsonDocument? NewValues { get; set; }

    public string? Summary { get; set; }

    public Guid? AuthorizedBy { get; set; }

    public string Severity { get; set; } = "INFO";

    public string RowHash { get; set; } = string.Empty;
}

/// <summary><c>audit.audit_seals</c>: sello de un rango de filas de un nodo.</summary>
public sealed record AuditSealRecord(
    Guid NodeId,
    long SealNo,
    short FormatVersion,
    long SeqFrom,
    long SeqTo,
    int RowsCount,
    string RowsDigest,
    string PrevSealHash,
    string SealHash,
    DateTimeOffset SealedAt);
