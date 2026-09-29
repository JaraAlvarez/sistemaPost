using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Auditing;
using Pos.Modules.Audit.Application;
using Pos.Modules.Audit.Contracts;

namespace Pos.Modules.Audit.Infrastructure;

internal sealed class AuditReadModel(NpgsqlDataSource dataSource, AuditVerifier verifier, IInstallationContext installation) : IAuditReadModel
{
    public async Task<AuditLogPage> SearchAsync(AuditLogFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<AuditRow>(new CommandDefinition(
            """
            SELECT id AS Id, occurred_at AS OccurredAt, seq AS Seq, module AS Module, action AS Action, severity AS Severity,
                   entity_type AS EntityType, entity_id AS EntityId, entity_label AS EntityLabel, summary AS Summary,
                   user_id AS UserId, user_display_name AS UserDisplayName, correlation_id AS CorrelationId,
                   old_values::text AS OldValues, new_values::text AS NewValues
            FROM audit.audit_log
            WHERE (company_id = @company OR company_id IS NULL OR @company::uuid IS NULL)
              AND (@entityType::text IS NULL OR entity_type = @entityType)
              AND (@entityId::uuid IS NULL OR entity_id = @entityId)
              AND (@userId::uuid IS NULL OR user_id = @userId)
              AND (@module::text IS NULL OR module = @module)
              AND (@action::text IS NULL OR action = @action)
              AND (@from::timestamptz IS NULL OR occurred_at >= @from)
              AND (@to::timestamptz IS NULL OR occurred_at < @to)
            ORDER BY occurred_at DESC, seq DESC
            LIMIT @limit OFFSET @offset
            """,
            new
            {
                company = installation.CompanyId,
                entityType = filter.EntityType,
                entityId = filter.EntityId,
                userId = filter.UserId,
                module = filter.Module,
                action = filter.Action,
                from = filter.From,
                to = filter.To,
                limit = filter.PageSize + 1,
                offset = (filter.Page - 1) * filter.PageSize,
            },
            cancellationToken: cancellationToken))).ToList();

        var hasMore = rows.Count > filter.PageSize;
        var items = rows.Take(filter.PageSize)
            .Select(r => new AuditLogEntryDto(
                r.Id, new DateTimeOffset(DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc)), r.Seq, r.Module, r.Action, r.Severity,
                r.EntityType, r.EntityId, r.EntityLabel, r.Summary, r.UserId, r.UserDisplayName, r.CorrelationId, r.OldValues, r.NewValues))
            .ToList();
        return new AuditLogPage(items, filter.Page, filter.PageSize, hasMore);
    }

    public async Task<AuditVerificationDto> VerifyAsync(CancellationToken cancellationToken)
    {
        var report = await verifier.VerifyAsync(cancellationToken);
        return new AuditVerificationDto(
            report.IsValid,
            report.NodesChecked,
            report.SealsChecked,
            report.RowsChecked,
            report.UnsealedRows,
            report.LastSealHash,
            report.LastSealShortCode,
            [.. report.Findings.Select(f => new AuditFindingDto(f.Kind.ToString(), f.NodeId, f.SealNo, f.Seq, f.Message))]);
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
}

public static class AuditInfrastructureRegistration
{
    public static void Register(IServiceCollection services) => services.AddScoped<IAuditReadModel, AuditReadModel>();
}
