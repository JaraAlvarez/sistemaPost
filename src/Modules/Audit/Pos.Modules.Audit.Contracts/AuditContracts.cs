using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Audit.Contracts;

/// <summary>Permisos del módulo Audit.</summary>
public static class AuditPermissions
{
    public const string LogView = "audit.log.view";
    public const string LogVerify = "audit.log.verify";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(LogView, "Consultar la bitácora de auditoría", isSensitive: true),
        new(LogVerify, "Verificar la integridad de la bitácora de auditoría", isSensitive: true),
    ];
}

/// <summary>Fila de la bitácora para la API.</summary>
public sealed record AuditLogEntryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    long Seq,
    string Module,
    string Action,
    string Severity,
    string? EntityType,
    Guid? EntityId,
    string? EntityLabel,
    string? Summary,
    Guid? UserId,
    string? UserDisplayName,
    string? CorrelationId,
    string? OldValues,
    string? NewValues);

/// <summary>Página de resultados.</summary>
public sealed record AuditLogPage(IReadOnlyList<AuditLogEntryDto> Items, int Page, int PageSize, bool HasMore);

/// <summary>Hallazgo de la verificación.</summary>
public sealed record AuditFindingDto(string Kind, Guid NodeId, long? SealNo, long? Seq, string Message);

/// <summary>
/// Comprobación del sello impreso en un reporte Z: el código corresponde al sello del nodo y la bitácora está íntegra.
/// </summary>
public sealed record AuditSealCheckDto(long SealNo, bool Exists, bool CodeMatches, bool AuditIsValid, DateTimeOffset? SealedAt);

/// <summary>Resultado de la verificación de integridad.</summary>
public sealed record AuditVerificationDto(
    bool IsValid,
    int NodesChecked,
    int SealsChecked,
    long RowsChecked,
    long UnsealedRows,
    string? LastSealHash,
    string? LastSealShortCode,
    IReadOnlyList<AuditFindingDto> Findings);
