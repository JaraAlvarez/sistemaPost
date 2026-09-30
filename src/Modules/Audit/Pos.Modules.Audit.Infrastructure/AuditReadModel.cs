using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Auditing;
using Pos.Modules.Audit.Application;
using Pos.Modules.Audit.Contracts;

namespace Pos.Modules.Audit.Infrastructure;

/// <summary>Lectura de la bitácora, las verificaciones y los incidentes (Dapper). Presenta los antes/después como diferencias (D10-03).</summary>
internal sealed class AuditReadModel(NpgsqlDataSource dataSource, AuditVerifier verifier, IInstallationContext installation) : IAuditReadModel
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AuditLogPage> SearchAsync(AuditLogFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var order = filter.Chronological ? "ASC" : "DESC";
        var rows = (await connection.QueryAsync<AuditRow>(new CommandDefinition(
            $"""
            SELECT id AS Id, occurred_at AS OccurredAt, seq AS Seq, module AS Module, action AS Action, severity AS Severity,
                   entity_type AS EntityType, entity_id AS EntityId, entity_label AS EntityLabel, summary AS Summary,
                   user_id AS UserId, user_display_name AS UserDisplayName, correlation_id AS CorrelationId,
                   old_values::text AS OldValues, new_values::text AS NewValues, pos_terminal_id AS PosTerminalId, authorized_by AS AuthorizedBy
            FROM audit.audit_log
            WHERE (company_id = @company OR company_id IS NULL OR @company::uuid IS NULL)
              AND (@entityType::text IS NULL OR entity_type = @entityType)
              AND (@entityId::uuid IS NULL OR entity_id = @entityId)
              AND (@userId::uuid IS NULL OR user_id = @userId OR (@userOrAuthorizer AND authorized_by = @userId))
              AND (@module::text IS NULL OR module = @module)
              AND (@action::text IS NULL OR action = @action)
              AND (@from::timestamptz IS NULL OR occurred_at >= @from)
              AND (@to::timestamptz IS NULL OR occurred_at < @to)
              AND (@severity::text IS NULL OR severity = @severity)
              AND (@terminal::uuid IS NULL OR pos_terminal_id = @terminal)
              AND (@authorizedBy::uuid IS NULL OR authorized_by = @authorizedBy)
              AND (@text::text IS NULL OR (COALESCE(summary, '') || ' ' || COALESCE(entity_label, '')) ILIKE '%' || @text || '%')
            ORDER BY occurred_at {order}, seq {order}
            LIMIT @limit OFFSET @offset
            """,
            new
            {
                company = installation.CompanyId,
                entityType = filter.EntityType,
                entityId = filter.EntityId,
                userId = filter.UserId,
                userOrAuthorizer = filter.UserOrAuthorizer,
                module = filter.Module,
                action = filter.Action,
                from = filter.From,
                to = filter.To,
                severity = filter.Severity,
                terminal = filter.TerminalId,
                authorizedBy = filter.AuthorizedBy,
                text = filter.Text?.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal),
                limit = filter.PageSize + 1,
                offset = (filter.Page - 1) * filter.PageSize,
            },
            cancellationToken: cancellationToken))).ToList();

        var hasMore = rows.Count > filter.PageSize;
        var items = rows.Take(filter.PageSize)
            .Select(r => new AuditLogEntryDto(
                r.Id, new DateTimeOffset(DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc)), r.Seq, r.Module, r.Action, r.Severity,
                r.EntityType, r.EntityId, r.EntityLabel, r.Summary, r.UserId, r.UserDisplayName, r.CorrelationId, r.OldValues, r.NewValues,
                AuditActions.NameOf(r.Action), r.PosTerminalId, r.AuthorizedBy, Changes(r.OldValues, r.NewValues)))
            .ToList();
        return new AuditLogPage(items, filter.Page, filter.PageSize, hasMore);
    }

    /// <summary>Diferencias campo a campo: la unión de los campos del antes y el después, con el nombre en español (D10-03).</summary>
    internal static IReadOnlyList<AuditChangeDto> Changes(string? oldValues, string? newValues)
    {
        var before = Parse(oldValues);
        var after = Parse(newValues);
        return [.. before.Keys.Union(after.Keys)
            .Select(field => new AuditChangeDto(field, AuditFieldNames.LabelOf(field), before.GetValueOrDefault(field), after.GetValueOrDefault(field)))
            .Where(c => c.Before != c.After)];
    }

    private static Dictionary<string, string?> Parse(string? json)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => "Sí",
                JsonValueKind.False => "No",
                _ => property.Value.GetRawText(),
            };
        }

        return result;
    }

    public async Task<AuditSealCheckDto> CheckSealAsync(long sealNo, string code, CancellationToken cancellationToken)
    {
        var seal = await verifier.CheckSealCodeAsync(installation.NodeId, sealNo, code, cancellationToken);
        var report = await verifier.VerifyAsync(cancellationToken);
        return new AuditSealCheckDto(sealNo, seal is not null, seal?.Matches ?? false, report.IsValid, seal?.SealedAt);
    }

    public async Task<IReadOnlyList<AuditVerificationRunDto>> ListVerificationsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            """
            SELECT id AS Id, kind AS Kind, started_at AS StartedAt, finished_at AS FinishedAt, from_seal_no AS FromSealNo, last_seal_no AS LastSealNo,
                   last_seal_code AS LastSealCode, seals_checked AS SealsChecked, rows_checked AS RowsChecked, unsealed_rows AS UnsealedRows,
                   is_valid AS IsValid, findings::text AS Findings, requested_by AS RequestedBy
            FROM audit.verification_runs WHERE node_id = @node ORDER BY started_at DESC LIMIT @limit
            """,
            new { node = installation.NodeId, limit },
            cancellationToken: cancellationToken));
        return [.. rows.Select(r => new AuditVerificationRunDto(
            r.Id, r.Kind, Utc(r.StartedAt), Utc(r.FinishedAt), r.FromSealNo, r.LastSealNo, r.LastSealCode, r.SealsChecked, r.RowsChecked, r.UnsealedRows,
            r.IsValid, Findings(r.Findings), r.RequestedBy))];
    }

    public async Task<IReadOnlyList<IntegrityIncidentDto>> ListIncidentsAsync(bool onlyOpen, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<IncidentRow>(new CommandDefinition(
            """
            SELECT i.id AS Id, i.detected_at AS DetectedAt, i.findings_count AS FindingsCount, i.summary AS Summary, i.verification_run_id AS RunId,
                   a.acknowledged_by AS AcknowledgedBy, u.display_name AS AcknowledgedByName, a.acknowledged_at AS AcknowledgedAt, a.note AS Note,
                   r.findings::text AS Findings
            FROM audit.integrity_incidents i
            JOIN audit.verification_runs r ON r.id = i.verification_run_id
            LEFT JOIN audit.integrity_incident_acknowledgements a ON a.incident_id = i.id
            LEFT JOIN identity.users u ON u.id = a.acknowledged_by
            WHERE i.node_id = @node AND (NOT @onlyOpen OR a.id IS NULL)
            ORDER BY i.detected_at DESC
            LIMIT 200
            """,
            new { node = installation.NodeId, onlyOpen },
            cancellationToken: cancellationToken));
        return [.. rows.Select(r => new IntegrityIncidentDto(
            r.Id, Utc(r.DetectedAt), r.FindingsCount, r.Summary, r.RunId, r.AcknowledgedBy is not null, r.AcknowledgedBy, r.AcknowledgedByName,
            r.AcknowledgedAt is { } at ? Utc(at) : null, r.Note, Findings(r.Findings)))];
    }

    internal static IReadOnlyList<AuditFindingDto> Findings(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<AuditFindingDto>>(json, Json) ?? [];

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class RunRow
    {
        public Guid Id { get; set; }

        public string Kind { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; }

        public DateTime FinishedAt { get; set; }

        public long? FromSealNo { get; set; }

        public long? LastSealNo { get; set; }

        public string? LastSealCode { get; set; }

        public int SealsChecked { get; set; }

        public long RowsChecked { get; set; }

        public long UnsealedRows { get; set; }

        public bool IsValid { get; set; }

        public string Findings { get; set; } = "[]";

        public Guid? RequestedBy { get; set; }
    }

    private sealed class IncidentRow
    {
        public Guid Id { get; set; }

        public DateTime DetectedAt { get; set; }

        public int FindingsCount { get; set; }

        public string Summary { get; set; } = string.Empty;

        public Guid RunId { get; set; }

        public Guid? AcknowledgedBy { get; set; }

        public string? AcknowledgedByName { get; set; }

        public DateTime? AcknowledgedAt { get; set; }

        public string? Note { get; set; }

        public string Findings { get; set; } = "[]";
    }
}

/// <summary>Fila leída con Dapper (timestamptz llega como DateTime UTC).</summary>
internal sealed class AuditRow
{
    public Guid Id { get; set; }

    public DateTime OccurredAt { get; set; }

    public long Seq { get; set; }

    public string Module { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public string? EntityLabel { get; set; }

    public string? Summary { get; set; }

    public Guid? UserId { get; set; }

    public string? UserDisplayName { get; set; }

    public string? CorrelationId { get; set; }

    public string? OldValues { get; set; }

    public string? NewValues { get; set; }

    public Guid? PosTerminalId { get; set; }

    public Guid? AuthorizedBy { get; set; }
}

public static class AuditInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddScoped<IAuditReadModel, AuditReadModel>();
        services.AddScoped<IAuditIntegrityStore, AuditIntegrityStore>();
        services.AddScoped<IIntegrityStatus, IntegrityStatus>();
        services.AddScoped<IIntegrityCertificateRenderer, IntegrityCertificateRenderer>();
        services.AddScoped<IntegrityService>();
        services.AddHostedService<AuditVerificationScheduler>();
        services.AddSingleton<Pos.Infrastructure.Persistence.IDatabaseReadyHook, AuditStartupHook>();
    }
}
