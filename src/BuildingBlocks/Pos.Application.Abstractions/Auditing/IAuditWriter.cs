namespace Pos.Application.Abstractions.Auditing;

public enum AuditSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// Evento de auditoría de negocio (doc 06). El usuario, equipo, IP y correlación los completa la infraestructura.
/// </summary>
/// <param name="Module">Módulo, p. ej. <c>catalog</c>.</param>
/// <param name="Action">Acción, p. ej. <c>PRODUCT_PRICE_CHANGED</c>.</param>
/// <param name="EntityType">Tipo de entidad afectada.</param>
/// <param name="EntityId">Id de la entidad afectada.</param>
/// <param name="EntityLabel">Descripción legible de la entidad (se guarda como snapshot).</param>
/// <param name="Summary">Resumen legible: "Juan cambió el precio de X de $4.500 a $4.800".</param>
/// <param name="OldValues">Valores anteriores (solo campos modificados).</param>
/// <param name="NewValues">Valores nuevos.</param>
/// <param name="AuthorizedBy">Supervisor que autorizó la acción, si aplica.</param>
/// <param name="Severity">Severidad.</param>
public sealed record AuditEntry(
    string Module,
    string Action,
    string? EntityType = null,
    Guid? EntityId = null,
    string? EntityLabel = null,
    string? Summary = null,
    IReadOnlyDictionary<string, object?>? OldValues = null,
    IReadOnlyDictionary<string, object?>? NewValues = null,
    Guid? AuthorizedBy = null,
    AuditSeverity Severity = AuditSeverity.Info);

/// <summary>Registra auditoría en la misma transacción del caso de uso. Se implementa en la Fase 2.</summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
