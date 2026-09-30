using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Backup.Contracts;

/// <summary>Permisos de backups (docs/fases/fase-11-propuesta.md §7, D11-11). Restaurar se hace desde la consola del servidor.</summary>
public static class BackupPermissions
{
    public const string Run = "backup.backup.run";
    public const string View = "backup.backup.view";
    public const string DestinationConfigure = "backup.destination.configure";
    public const string RecoveryManage = "backup.recovery.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(Run, "Respaldar ahora y verificar un backup", isSensitive: false),
        new(View, "Ver el historial de backups, los destinos y las alertas; descargar un backup", isSensitive: false),
        new(DestinationConfigure, "Configurar los destinos de los backups (carpetas, disco externo, red, nube)", isSensitive: true),
        new(RecoveryManage, "Generar y confirmar el código de recuperación de los backups (solo el propietario)", isSensitive: true),
    ];
}

/// <summary>Tipos de backup (D11-05).</summary>
public static class BackupKinds
{
    public const string Scheduled = "SCHEDULED";
    public const string Nightly = "NIGHTLY";
    public const string CashClosing = "CASH_CLOSING";
    public const string PreUpdate = "PRE_UPDATE";
    public const string PreRestore = "PRE_RESTORE";
    public const string Manual = "MANUAL";
}

/// <summary>Tipos de destino (D11-06).</summary>
public static class DestinationKinds
{
    public const string Local = "LOCAL";
    public const string External = "EXTERNAL";
    public const string Network = "NETWORK";
    public const string S3 = "S3";
}

public sealed record BackupCopyDto(Guid DestinationId, string DestinationName, string Status, string? Location, DateTimeOffset? CopiedAt, string? Error);

public sealed record BackupRunDto(
    Guid Id,
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Succeeded,
    bool Verified,
    string? FileName,
    long? SizeBytes,
    string? SchemaVersion,
    long? AuditSealNo,
    string? AuditSealCode,
    string? Error,
    IReadOnlyList<BackupCopyDto> Copies);

/// <summary>Destino. El secreto de S3 nunca se devuelve (solo si está configurado).</summary>
public sealed record BackupDestinationDto(
    Guid Id,
    string Kind,
    string Name,
    string? Path,
    string? VolumeLabel,
    string? Endpoint,
    string? Region,
    string? Bucket,
    string? Prefix,
    string? AccessKey,
    bool HasSecret,
    bool OnScheduled,
    bool OnNightly,
    bool OnClosing,
    bool OnManual,
    int KeepDaily,
    int KeepWeekly,
    int KeepMonthly,
    bool IsActive,
    string? LastStatus,
    string? LastError,
    DateTimeOffset? LastSuccessAt);

public sealed record RestoreTestDto(
    Guid Id,
    Guid RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Succeeded,
    bool? AuditValid,
    bool? SealMatches,
    bool? CountsMatch,
    string? Error);

/// <summary>El código se muestra UNA sola vez (D11-04).</summary>
public sealed record RecoveryCodeDto(int Version, string Code, string Instructions);

public sealed record BackupVerificationDto(Guid RunId, bool Valid, int DumpEntries, string? Error);

/// <summary>Alertas de backups (D11-10) para el tablero y el perfil del usuario.</summary>
public sealed record BackupAlertsDto(
    DateTimeOffset? LastVerifiedBackupAt,
    bool LastBackupTooOld,
    int FailingDestinations,
    bool RestoreTestFailed,
    bool RecoveryCodePending)
{
    public int Count => (LastBackupTooOld ? 1 : 0) + FailingDestinations + (RestoreTestFailed ? 1 : 0) + (RecoveryCodePending ? 1 : 0);
}

/// <summary>Estado de los backups para otros módulos (Identity: <c>/auth/me</c>; Reporting: tablero).</summary>
public interface IBackupStatus
{
    Task<BackupAlertsDto> GetAlertsAsync(CancellationToken cancellationToken);
}
