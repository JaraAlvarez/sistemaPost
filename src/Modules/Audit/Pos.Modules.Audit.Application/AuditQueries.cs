using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Audit.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Audit.Application;

/// <summary>Filtros de consulta de la bitácora (§5.2).</summary>
public sealed record AuditLogFilter(
    string? EntityType,
    Guid? EntityId,
    Guid? UserId,
    string? Module,
    string? Action,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize,
    string? Severity = null,
    Guid? TerminalId = null,
    Guid? AuthorizedBy = null,
    string? Text = null,
    bool UserOrAuthorizer = false,
    bool Chronological = false);

public interface IAuditReadModel
{
    Task<AuditLogPage> SearchAsync(AuditLogFilter filter, CancellationToken cancellationToken);

    Task<AuditSealCheckDto> CheckSealAsync(long sealNo, string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditVerificationRunDto>> ListVerificationsAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegrityIncidentDto>> ListIncidentsAsync(bool onlyOpen, CancellationToken cancellationToken);
}

// ------------------------------------------------------------------------------------------------ Integridad (puertos)

public sealed record IntegrityFinding(string Kind, Guid NodeId, long? SealNo, long? Seq, string Message)
{
    /// <summary>Identidad del hallazgo: el mismo hallazgo ya reconocido no abre otro incidente.</summary>
    public string Key => $"{Kind}|{NodeId:N}|{SealNo}|{Seq}";

    public AuditFindingDto ToDto() => new(Kind, NodeId, SealNo, Seq, Message);
}

public sealed record IntegrityCheck(
    IReadOnlyList<IntegrityFinding> Findings,
    int NodesChecked,
    int SealsChecked,
    long RowsChecked,
    long UnsealedRows,
    long? LastSealNo,
    string? LastSealHash,
    string? LastSealCode);

public sealed record VerificationRunRecord(
    Guid Id,
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long? FromSealNo,
    IntegrityCheck Check,
    Guid? RequestedBy);

public sealed record IncidentState(Guid Id, bool Acknowledged, int FindingsCount, string Summary);

/// <summary>Verificación y registro de la integridad (Infraestructura: verificador y tablas de <c>audit</c>).</summary>
public interface IAuditIntegrityStore
{
    /// <summary>Último sello cubierto por la verificación anterior (punto de partida de la incremental).</summary>
    Task<long?> LastVerifiedSealAsync(CancellationToken cancellationToken);

    Task<IntegrityCheck> VerifyAsync(long? rowsVerifiedUpToSeal, CancellationToken cancellationToken);

    /// <summary>Claves de los hallazgos de incidentes ya reconocidos por el propietario.</summary>
    Task<IReadOnlySet<string>> AcknowledgedFindingKeysAsync(CancellationToken cancellationToken);

    Task SaveRunAsync(VerificationRunRecord run, CancellationToken cancellationToken);

    Task SaveIncidentAsync(Guid id, Guid runId, DateTimeOffset detectedAt, int findingsCount, string summary, CancellationToken cancellationToken);

    Task<IncidentState?> GetIncidentAsync(Guid id, CancellationToken cancellationToken);

    Task SaveAcknowledgementAsync(Guid id, Guid incidentId, Guid userId, DateTimeOffset at, string note, CancellationToken cancellationToken);

    Task<int> OpenIncidentsAsync(CancellationToken cancellationToken);
}

public sealed record IntegrityCertificateData(
    string CompanyName,
    string CompanyIdentification,
    string BranchName,
    Guid NodeId,
    long? SealNo,
    string? SealCode,
    DateTimeOffset? SealedAt,
    AuditVerificationRunDto? LastVerification,
    int OpenIncidents,
    string IssuedBy,
    DateTimeOffset IssuedAt);

public interface IIntegrityCertificateRenderer
{
    Task<(string CompanyName, string CompanyIdentification, string BranchName)> ReadHeaderAsync(CancellationToken cancellationToken);

    byte[] Render(IntegrityCertificateData data);
}

/// <summary>Configuración de la auditoría. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class AuditSettings
{
    public static readonly SettingDefinition<int> VerificationHour = new(
        "audit.verification_hour", 3, SettingScope.Company, "Hora local desde la que corre la verificación automática diaria de la bitácora (D10-04).",
        v => v is >= 0 and <= 23 ? null : "Entre 0 y 23.");

    public static readonly SettingDefinition<int> FullVerificationWeekday = new(
        "audit.full_verification_weekday", 0, SettingScope.Company,
        "Día de la semana de la verificación COMPLETA (0 = domingo … 6 = sábado); los demás días es incremental.",
        v => v is >= 0 and <= 6 ? null : "Entre 0 y 6.");

    public static readonly SettingDefinition<int> ClockToleranceMinutes = new(
        "audit.clock_tolerance_minutes", 5, SettingScope.Company,
        "Minutos de atraso del reloj del servidor frente a la última fila de la bitácora que se registran como incidente (CLOCK_JUMP_DETECTED).",
        v => v is >= 1 and <= 120 ? null : "Entre 1 y 120.");

    public static IEnumerable<SettingDefinition> All => [VerificationHour, FullVerificationWeekday, ClockToleranceMinutes];
}

public sealed class AuditSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => AuditSettings.All;
}

public static class AuditErrors
{
    public static readonly Error IncidentNotFound = Error.NotFound("AUDIT.INCIDENT_NOT_FOUND", "El incidente de integridad no existe.");
    public static readonly Error IncidentAlreadyAcknowledged = Error.Conflict("AUDIT.INCIDENT_ALREADY_ACKNOWLEDGED", "El incidente ya fue reconocido.");
    public static readonly Error NoteRequired = Error.Validation(
        "AUDIT.NOTE_REQUIRED", "Escriba qué revisó y qué encontró (entre 10 y 1000 caracteres).");
}

/// <summary>
/// Verificación de la integridad (D10-04/D10-05): ejecuta el verificador (incremental desde el último sello verificado o completa),
/// guarda la verificación y, si aparecen hallazgos nuevos (no reconocidos antes), abre un incidente CRÍTICO. Nunca bloquea la venta.
/// Se llama desde un comando o desde la tarea programada, dentro de una transacción.
/// </summary>
public sealed class IntegrityService(IAuditIntegrityStore store, IAuditWriter audit, IIdGenerator ids, IClock clock)
{
    public const string Incremental = "INCREMENTAL";
    public const string Full = "FULL";
    public const string Manual = "MANUAL";

    public async Task<AuditVerificationDto> RunAsync(string kind, Guid? requestedBy, CancellationToken cancellationToken)
    {
        var started = clock.UtcNow;
        var from = kind == Incremental ? await store.LastVerifiedSealAsync(cancellationToken) : null;
        var check = await store.VerifyAsync(from, cancellationToken);
        var runId = ids.NewId();
        await store.SaveRunAsync(new VerificationRunRecord(runId, kind, started, clock.UtcNow, from, check, requestedBy), cancellationToken);

        Guid? incidentId = null;
        if (check.Findings.Count == 0)
        {
            await audit.WriteAsync(
                new AuditEntry("audit", "AUDIT_VERIFIED", "AuditVerification", runId, KindLabel(kind),
                    $"Verificación {KindLabel(kind).ToLowerInvariant()} sin hallazgos: {check.SealsChecked} sellos, {check.RowsChecked} filas recalculadas"
                    + (check.LastSealCode is null ? "." : $"; último sello #{check.LastSealNo} · {check.LastSealCode}.")),
                cancellationToken);
        }
        else
        {
            var known = await store.AcknowledgedFindingKeysAsync(cancellationToken);
            var fresh = check.Findings.Where(f => !known.Contains(f.Key)).ToList();
            if (fresh.Count > 0)
            {
                incidentId = ids.NewId();
                var summary = Summary(fresh);
                await store.SaveIncidentAsync(incidentId.Value, runId, clock.UtcNow, fresh.Count, summary, cancellationToken);
                await audit.WriteAsync(
                    new AuditEntry("audit", "AUDIT_VERIFICATION_FAILED", "IntegrityIncident", incidentId, KindLabel(kind),
                        $"La bitácora fue alterada: {summary}", Severity: AuditSeverity.Critical),
                    cancellationToken);
            }
            else
            {
                await audit.WriteAsync(
                    new AuditEntry("audit", "AUDIT_VERIFICATION_FAILED", "AuditVerification", runId, KindLabel(kind),
                        $"La verificación encontró {check.Findings.Count} hallazgo(s) ya reconocido(s) por el propietario; no se abre un incidente nuevo.",
                        Severity: AuditSeverity.Warning),
                    cancellationToken);
            }
        }

        return new AuditVerificationDto(
            check.Findings.Count == 0,
            check.NodesChecked,
            check.SealsChecked,
            check.RowsChecked,
            check.UnsealedRows,
            check.LastSealHash,
            check.LastSealCode,
            [.. check.Findings.Select(f => f.ToDto())],
            runId,
            incidentId);
    }

    private static string KindLabel(string kind) => kind switch
    {
        Incremental => "Incremental",
        Full => "Completa",
        _ => "Manual",
    };

    private static string Summary(List<IntegrityFinding> findings)
    {
        var parts = findings.GroupBy(f => f.Kind).Select(g => $"{g.Count()} {Describe(g.Key)}");
        var text = $"{string.Join(", ", parts)}. {findings[0].Message}";
        return text.Length > 500 ? text[..500] : text;
    }

    private static string Describe(string kind) => kind switch
    {
        "RowAltered" => "fila(s) modificada(s)",
        "SealContentMismatch" => "rango(s) sellado(s) con filas borradas o insertadas",
        "SealAltered" => "sello(s) alterado(s)",
        "ChainBroken" => "ruptura(s) de la cadena de sellos",
        _ => kind,
    };
}

// ------------------------------------------------------------------------------------------------ Consultas

/// <summary>¿El sello impreso en un reporte Z (número + código) corresponde a la bitácora de este nodo? (D6-08)</summary>
public sealed record CheckSealQuery(long SealNo, string Code) : IQuery<AuditSealCheckDto>;

internal sealed class CheckSealHandler(IAuditReadModel read) : IQueryHandler<CheckSealQuery, AuditSealCheckDto>
{
    public async Task<Result<AuditSealCheckDto>> Handle(CheckSealQuery request, CancellationToken cancellationToken) =>
        await read.CheckSealAsync(request.SealNo, request.Code, cancellationToken);
}

public sealed record SearchAuditLogQuery(
    string? EntityType = null,
    Guid? EntityId = null,
    Guid? UserId = null,
    string? Module = null,
    string? Action = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50,
    string? Severity = null,
    Guid? TerminalId = null,
    Guid? AuthorizedBy = null,
    string? Q = null) : IQuery<AuditLogPage>;

internal sealed class SearchAuditLogValidator : AbstractValidator<SearchAuditLogQuery>
{
    public SearchAuditLogValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
        RuleFor(x => x.To).GreaterThan(x => x.From).When(x => x.From is not null && x.To is not null);
        RuleFor(x => x.Severity).Must(s => s is null or "INFO" or "WARNING" or "CRITICAL").WithMessage("Severidad: INFO, WARNING o CRITICAL.");
        RuleFor(x => x.Q).MaximumLength(100);
    }
}

internal sealed class SearchAuditLogHandler(IAuditReadModel read) : IQueryHandler<SearchAuditLogQuery, AuditLogPage>
{
    public async Task<Result<AuditLogPage>> Handle(SearchAuditLogQuery q, CancellationToken cancellationToken) =>
        await read.SearchAsync(
            new AuditLogFilter(q.EntityType, q.EntityId, q.UserId, q.Module, q.Action, q.From, q.To, q.Page, q.PageSize,
                q.Severity?.ToUpperInvariant(), q.TerminalId, q.AuthorizedBy, string.IsNullOrWhiteSpace(q.Q) ? null : q.Q.Trim()),
            cancellationToken);
}

/// <summary>Historial completo de un registro, en orden cronológico, con las diferencias campo a campo (§5.2).</summary>
public sealed record EntityHistoryQuery(string EntityType, Guid EntityId, int Page = 1, int PageSize = 100) : IQuery<AuditLogPage>;

internal sealed class EntityHistoryHandler(IAuditReadModel read) : IQueryHandler<EntityHistoryQuery, AuditLogPage>
{
    public async Task<Result<AuditLogPage>> Handle(EntityHistoryQuery q, CancellationToken cancellationToken) =>
        await read.SearchAsync(
            new AuditLogFilter(q.EntityType, q.EntityId, null, null, null, null, null, Math.Max(q.Page, 1), Math.Clamp(q.PageSize, 1, 200),
                Chronological: true),
            cancellationToken);
}

/// <summary>Actividad de un usuario: lo que hizo y lo que autorizó como supervisor (§5.2).</summary>
public sealed record UserActivityQuery(Guid UserId, DateTimeOffset? From, DateTimeOffset? To, int Page = 1, int PageSize = 100) : IQuery<AuditLogPage>;

internal sealed class UserActivityHandler(IAuditReadModel read) : IQueryHandler<UserActivityQuery, AuditLogPage>
{
    public async Task<Result<AuditLogPage>> Handle(UserActivityQuery q, CancellationToken cancellationToken) =>
        await read.SearchAsync(
            new AuditLogFilter(null, null, q.UserId, null, null, q.From, q.To, Math.Max(q.Page, 1), Math.Clamp(q.PageSize, 1, 200), UserOrAuthorizer: true),
            cancellationToken);
}

public sealed record ListAuditActionsQuery : IQuery<IReadOnlyList<AuditActionDefinition>>;

internal sealed class ListAuditActionsHandler : IQueryHandler<ListAuditActionsQuery, IReadOnlyList<AuditActionDefinition>>
{
    public Task<Result<IReadOnlyList<AuditActionDefinition>>> Handle(ListAuditActionsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success<IReadOnlyList<AuditActionDefinition>>([.. AuditActions.All.OrderBy(a => a.Module).ThenBy(a => a.Code)]));
}

public sealed record ListVerificationsQuery(int Limit = 50) : IQuery<IReadOnlyList<AuditVerificationRunDto>>;

internal sealed class ListVerificationsHandler(IAuditReadModel read) : IQueryHandler<ListVerificationsQuery, IReadOnlyList<AuditVerificationRunDto>>
{
    public async Task<Result<IReadOnlyList<AuditVerificationRunDto>>> Handle(ListVerificationsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.ListVerificationsAsync(Math.Clamp(request.Limit, 1, 500), cancellationToken));
}

public sealed record ListIncidentsQuery(bool OnlyOpen = false) : IQuery<IReadOnlyList<IntegrityIncidentDto>>;

internal sealed class ListIncidentsHandler(IAuditReadModel read) : IQueryHandler<ListIncidentsQuery, IReadOnlyList<IntegrityIncidentDto>>
{
    public async Task<Result<IReadOnlyList<IntegrityIncidentDto>>> Handle(ListIncidentsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.ListIncidentsAsync(request.OnlyOpen, cancellationToken));
}

// ------------------------------------------------------------------------------------------------ Comandos

/// <summary>Verificación manual de toda la bitácora (queda registrada como MANUAL; con hallazgos nuevos abre un incidente).</summary>
public sealed record VerifyAuditCommand : ICommand<AuditVerificationDto>, IAllowedWhenRestricted;

internal sealed class VerifyAuditHandler(IntegrityService integrity, ICurrentUser user) : ICommandHandler<VerifyAuditCommand, AuditVerificationDto>
{
    public async Task<Result<AuditVerificationDto>> Handle(VerifyAuditCommand request, CancellationToken cancellationToken) =>
        await integrity.RunAsync(IntegrityService.Manual, user.UserId, cancellationToken);
}

/// <summary>El propietario reconoce un incidente de integridad con una nota (D10-05). El reconocimiento es una fila nueva.</summary>
public sealed record AcknowledgeIncidentCommand(Guid IncidentId, string Note) : ICommand<IntegrityIncidentDto>, IAllowedWhenRestricted;

internal sealed class AcknowledgeIncidentHandler(
    IAuditIntegrityStore store, IAuditReadModel read, IAuditWriter audit, ICurrentUser user, IIdGenerator ids, IClock clock)
    : ICommandHandler<AcknowledgeIncidentCommand, IntegrityIncidentDto>
{
    public async Task<Result<IntegrityIncidentDto>> Handle(AcknowledgeIncidentCommand request, CancellationToken cancellationToken)
    {
        var note = (request.Note ?? string.Empty).Trim();
        if (note.Length is < 10 or > 1000)
        {
            return AuditErrors.NoteRequired;
        }

        var incident = await store.GetIncidentAsync(request.IncidentId, cancellationToken);
        if (incident is null)
        {
            return AuditErrors.IncidentNotFound;
        }

        if (incident.Acknowledged)
        {
            return AuditErrors.IncidentAlreadyAcknowledged;
        }

        await store.SaveAcknowledgementAsync(ids.NewId(), incident.Id, user.UserId!.Value, clock.UtcNow, note, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("audit", "INTEGRITY_INCIDENT_ACKNOWLEDGED", "IntegrityIncident", incident.Id, incident.Summary,
                $"Incidente de integridad reconocido ({incident.FindingsCount} hallazgo(s)): {note}", Severity: AuditSeverity.Warning),
            cancellationToken);
        var all = await read.ListIncidentsAsync(false, cancellationToken);
        return all.FirstOrDefault(i => i.Id == incident.Id) is { } found ? found with { Acknowledged = true, Note = note } : AuditErrors.IncidentNotFound;
    }
}

/// <summary>Constancia de integridad en PDF con el último sello (ancla externa manual, D10-09).</summary>
public sealed record IssueIntegrityCertificateCommand : ICommand<IntegrityCertificateDto>, IAllowedWhenRestricted;

internal sealed class IssueIntegrityCertificateHandler(
    IAuditAnchor anchor,
    IAuditReadModel read,
    IAuditIntegrityStore store,
    IIntegrityCertificateRenderer renderer,
    IAuditWriter audit,
    ICurrentUser user,
    Pos.Application.Abstractions.Installation.IInstallationContext installation,
    IClock clock) : ICommandHandler<IssueIntegrityCertificateCommand, IntegrityCertificateDto>
{
    public async Task<Result<IntegrityCertificateDto>> Handle(IssueIntegrityCertificateCommand request, CancellationToken cancellationToken)
    {
        var seal = await anchor.SealNowAsync(cancellationToken);
        var header = await renderer.ReadHeaderAsync(cancellationToken);
        var runs = await read.ListVerificationsAsync(1, cancellationToken);
        var last = runs.Count > 0 ? runs[0] : null;
        var open = await store.OpenIncidentsAsync(cancellationToken);
        var now = clock.UtcNow;
        var content = renderer.Render(new IntegrityCertificateData(
            header.CompanyName, header.CompanyIdentification, header.BranchName, installation.NodeId, seal?.SealNo, seal?.ShortCode, seal?.SealedAt,
            last, open, user.DisplayName ?? string.Empty, now));
        await audit.WriteAsync(
            new AuditEntry("audit", "INTEGRITY_CERTIFICATE_ISSUED", "AuditSeal", null, seal is null ? "Sin sello" : $"Sello #{seal.SealNo}",
                seal is null ? "Constancia emitida sin sellos todavía." : $"Constancia del sello #{seal.SealNo} · {seal.ShortCode}."),
            cancellationToken);
        return new IntegrityCertificateDto($"constancia-integridad-{now:yyyyMMdd-HHmm}.pdf", content, seal?.SealNo, seal?.ShortCode);
    }
}

public sealed class AuditPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => AuditPermissions.All;
}
