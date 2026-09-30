using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Audit.Contracts;

/// <summary>Permisos del módulo Audit.</summary>
public static class AuditPermissions
{
    public const string LogView = "audit.log.view";
    public const string LogVerify = "audit.log.verify";

    /// <summary>Reconocer un incidente de integridad (D10-05/D10-10): solo el Propietario.</summary>
    public const string IncidentAcknowledge = "audit.incident.acknowledge";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(LogView, "Consultar la bitácora de auditoría", isSensitive: true),
        new(LogVerify, "Verificar la integridad de la bitácora de auditoría", isSensitive: true),
        new(IncidentAcknowledge, "Reconocer un incidente de integridad de la bitácora (solo el propietario)", isSensitive: true),
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
    string? NewValues,
    string ActionName = "",
    Guid? PosTerminalId = null,
    Guid? AuthorizedBy = null,
    IReadOnlyList<AuditChangeDto>? Changes = null);

/// <summary>Un campo que cambió (D10-03): nombre técnico, nombre en español, valor anterior y nuevo.</summary>
public sealed record AuditChangeDto(string Field, string Label, string? Before, string? After);

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
    IReadOnlyList<AuditFindingDto> Findings,
    Guid? RunId = null,
    Guid? IncidentId = null);

/// <summary>Una verificación registrada (D10-04): diaria incremental, semanal completa o manual.</summary>
public sealed record AuditVerificationRunDto(
    Guid Id,
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long? FromSealNo,
    long? LastSealNo,
    string? LastSealCode,
    int SealsChecked,
    long RowsChecked,
    long UnsealedRows,
    bool IsValid,
    IReadOnlyList<AuditFindingDto> Findings,
    Guid? RequestedBy);

/// <summary>Incidente de integridad (D10-05): abierto hasta que el propietario lo reconoce con una nota.</summary>
public sealed record IntegrityIncidentDto(
    Guid Id,
    DateTimeOffset DetectedAt,
    int FindingsCount,
    string Summary,
    Guid VerificationRunId,
    bool Acknowledged,
    Guid? AcknowledgedBy,
    string? AcknowledgedByName,
    DateTimeOffset? AcknowledgedAt,
    string? Note,
    IReadOnlyList<AuditFindingDto> Findings);

/// <summary>Constancia de integridad en PDF (ancla externa manual, D10-09).</summary>
public sealed record IntegrityCertificateDto(string FileName, byte[] Content, long? SealNo, string? SealCode);

/// <summary>Estado de integridad para otros módulos (tablero, perfil del usuario).</summary>
public interface IIntegrityStatus
{
    /// <summary>Incidentes de integridad sin reconocer del nodo.</summary>
    Task<int> OpenIncidentsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Nombres en español de los campos que aparecen en los antes/después (D10-03). Diccionario común por columna; si un campo no está,
/// se muestra su nombre técnico.
/// </summary>
public static class AuditFieldNames
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["price"] = "Precio",
        ["valid_from"] = "Vigente desde",
        ["valid_to"] = "Vigente hasta",
        ["price_list_id"] = "Lista de precio",
        ["product_id"] = "Producto",
        ["packaging_id"] = "Presentación",
        ["name"] = "Nombre",
        ["short_name"] = "Nombre corto",
        ["description"] = "Descripción",
        ["sku"] = "SKU",
        ["code"] = "Código",
        ["barcode"] = "Código de barras",
        ["status"] = "Estado",
        ["category_id"] = "Categoría",
        ["brand_id"] = "Marca",
        ["tax_id"] = "Impuesto",
        ["rate"] = "Tarifa",
        ["last_cost"] = "Último costo",
        ["unit_cost"] = "Costo unitario",
        ["cost"] = "Costo",
        ["min_qty"] = "Mínimo",
        ["max_qty"] = "Máximo",
        ["reorder_point"] = "Punto de pedido",
        ["reorder_qty"] = "Cantidad de pedido",
        ["username"] = "Usuario",
        ["display_name"] = "Nombre visible",
        ["email"] = "Correo",
        ["phone"] = "Teléfono",
        ["address"] = "Dirección",
        ["notes"] = "Notas",
        ["legal_name"] = "Razón social",
        ["trade_name"] = "Nombre comercial",
        ["first_names"] = "Nombres",
        ["last_names"] = "Apellidos",
        ["identification_type"] = "Tipo de identificación",
        ["identification_number"] = "Número de identificación",
        ["kind"] = "Tipo",
        ["amount"] = "Valor",
        ["total"] = "Total",
        ["balance"] = "Saldo",
        ["due_date"] = "Vence",
        ["is_active"] = "Activo",
        ["group_id"] = "Grupo",
        ["credit_limit"] = "Cupo",
        ["difference"] = "Diferencia",
        ["counted_total"] = "Contado",
        ["expected_total"] = "Esperado",
        ["allows_open_price"] = "Admite precio abierto",
        ["adjustment_percent"] = "Ajuste %",
    };

    public static string LabelOf(string field) => Labels.GetValueOrDefault(field) ?? field;
}
