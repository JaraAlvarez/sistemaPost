using Pos.Application.Abstractions.Auditing;

namespace Pos.Cloud.Abstractions;

/// <summary>
/// Auditoría a nombre de un usuario que aún no tiene sesión completa (intentos de acceso, segundo factor): el autor de la fila es
/// quien intenta entrar, no el usuario técnico.
/// </summary>
public interface IAttributedAuditWriter
{
    Task WriteAsync(AuditEntry entry, Guid actorId, string actorName, CancellationToken cancellationToken = default);
}

/// <summary>Fila de la auditoría de la nube tal como la consulta el portal.</summary>
public sealed record CloudAuditEntryDto(
    Guid Id,
    long Seq,
    DateTimeOffset OccurredAt,
    string Module,
    string Action,
    string Severity,
    string? UserDisplayName,
    string? EntityType,
    Guid? EntityId,
    string? EntityLabel,
    string? Summary,
    string? IpAddress,
    string? OldValues,
    string? NewValues);

public sealed record CloudAuditFilter(DateTimeOffset? From, DateTimeOffset? To, string? Module, string? Action, Guid? EntityId, string? Text, int Limit = 200);

/// <summary>Estado de integridad de la cadena de auditoría (filas, sellos y último código).</summary>
public sealed record CloudAuditIntegrityDto(bool IsValid, int SealsChecked, long RowsChecked, long UnsealedRows, string? LastSealCode, IReadOnlyList<string> Findings);

/// <summary>Lectura de la bitácora y de su integridad (solo lectura; la escritura es <c>IAuditWriter</c>).</summary>
public interface ICloudAuditLog
{
    Task<IReadOnlyList<CloudAuditEntryDto>> SearchAsync(CloudAuditFilter filter, CancellationToken cancellationToken = default);

    Task<CloudAuditIntegrityDto> VerifyAsync(CancellationToken cancellationToken = default);
}
