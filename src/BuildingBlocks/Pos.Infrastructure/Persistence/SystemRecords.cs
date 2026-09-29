using System.Text.Json;

namespace Pos.Infrastructure.Persistence;

// Registros de las tablas técnicas del esquema system. No son entidades de dominio: solo los usa la infraestructura.

/// <summary><c>system.installation</c> (una sola fila).</summary>
public sealed class InstallationRecord
{
    public bool Id { get; set; } = true;

    public Guid InstallationId { get; set; }

    public string NodeRole { get; set; } = "ALL_IN_ONE";

    public int NodeEpoch { get; set; } = 1;

    public Guid? HomeCompanyId { get; set; }

    public Guid? HomeBranchId { get; set; }

    public string? SetupMode { get; set; }

    public DateTimeOffset? SetupCompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary><c>system.document_series</c>: numeración interna.</summary>
public sealed class DocumentSeriesRecord
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid BranchId { get; set; }

    public Guid? PosTerminalId { get; set; }

    public string DocumentType { get; set; } = string.Empty;

    public string Prefix { get; set; } = string.Empty;

    public long NextNumber { get; set; } = 1;

    public short Padding { get; set; } = 6;

    public string Status { get; set; } = "ACTIVE";
}

/// <summary><c>system.settings</c>: excepciones de configuración.</summary>
public sealed class SettingRecord
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string ScopeType { get; set; } = "COMPANY";

    public Guid ScopeId { get; set; }

    public string Key { get; set; } = string.Empty;

    public JsonDocument Value { get; set; } = JsonDocument.Parse("null");

    public long RowVersion { get; set; } = 1;
}

/// <summary><c>system.outbox_messages</c>.</summary>
public sealed class OutboxMessageRecord
{
    public Guid Id { get; set; }

    public long NodeSeq { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string Destination { get; set; } = "LOCAL";

    public string Type { get; set; } = string.Empty;

    public JsonDocument Payload { get; set; } = JsonDocument.Parse("{}");

    public string? CorrelationId { get; set; }

    public string Status { get; set; } = "PENDING";

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary><c>system.inbox_messages</c>.</summary>
public sealed class InboxMessageRecord
{
    public Guid MessageId { get; set; }

    public Guid SourceNodeId { get; set; }

    public long SourceSeq { get; set; }

    public string Type { get; set; } = string.Empty;

    public string ReceivedVia { get; set; } = "ONLINE";

    public DateTimeOffset AppliedAt { get; set; }

    public string Result { get; set; } = "APPLIED";
}
