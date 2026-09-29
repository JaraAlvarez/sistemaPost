using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Infrastructure.Auditing;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Infrastructure.Auditing;

/// <summary>
/// Auditoría explícita de las acciones del portal y de la API: la fila queda pendiente en el contexto y se guarda en la MISMA
/// transacción del caso de uso; el interceptor le asigna seq, contexto y row_hash.
/// </summary>
internal sealed class CloudAuditWriter(CloudDbContext context, IClock clock, IIdGenerator ids) : IAuditWriter, IAttributedAuditWriter
{
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Add(entry, null, null);
        return Task.CompletedTask;
    }

    public Task WriteAsync(AuditEntry entry, Guid actorId, string actorName, CancellationToken cancellationToken = default)
    {
        Add(entry, actorId, actorName);
        return Task.CompletedTask;
    }

    private void Add(AuditEntry entry, Guid? actorId, string? actorName)
    {
        ArgumentNullException.ThrowIfNull(entry);
        context.Add(new AuditLogRecord
        {
            Id = ids.NewId(),
            OccurredAt = clock.UtcNow,
            Module = entry.Module,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            EntityLabel = entry.EntityLabel is { Length: > 200 } label ? label[..200] : entry.EntityLabel,
            Summary = entry.Summary is { Length: > 500 } summary ? summary[..500] : entry.Summary,
            OldValues = CloudSaveChangesInterceptor.ToDocument(ToObject(entry.OldValues)),
            NewValues = CloudSaveChangesInterceptor.ToDocument(ToObject(entry.NewValues)),
            AuthorizedBy = entry.AuthorizedBy,
            Severity = entry.Severity.ToString().ToUpperInvariant(),
            UserId = actorId,
            UserDisplayName = actorName,
        });
    }

    private static JsonObject? ToObject(IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null)
        {
            return null;
        }

        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            obj[key] = CloudSaveChangesInterceptor.AuditValue(value);
        }

        return obj;
    }
}

/// <summary>Consulta de la bitácora para el portal y verificación de su integridad con el verificador del POS.</summary>
internal sealed class CloudAuditLog(NpgsqlDataSource dataSource, AuditVerifier verifier) : ICloudAuditLog
{
    public async Task<IReadOnlyList<CloudAuditEntryDto>> SearchAsync(CloudAuditFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var text = string.IsNullOrWhiteSpace(filter.Text) ? null : $"%{filter.Text.Trim()}%";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id, seq, occurred_at, module, action, severity, user_display_name, entity_type, entity_id, entity_label, summary,
                   host(ip_address) AS ip_address, old_values::text AS old_values, new_values::text AS new_values
            FROM audit.audit_log
            WHERE (@from::timestamptz IS NULL OR occurred_at >= @from)
              AND (@to::timestamptz IS NULL OR occurred_at < @to)
              AND (@module::text IS NULL OR module = @module)
              AND (@action::text IS NULL OR action = @action)
              AND (@entityId::uuid IS NULL OR entity_id = @entityId)
              AND (@text::text IS NULL OR summary ILIKE @text OR entity_label ILIKE @text OR user_display_name ILIKE @text)
            ORDER BY occurred_at DESC, seq DESC
            LIMIT @limit OFFSET @offset
            """,
            new
            {
                from = filter.From,
                to = filter.To,
                module = filter.Module,
                action = filter.Action,
                entityId = filter.EntityId,
                text,
                limit = Math.Clamp(filter.Limit, 1, 1000),
                offset = Math.Max(0, filter.Offset),
            },
            cancellationToken: cancellationToken));
        return [.. rows.Select(r => new CloudAuditEntryDto(
            r.Id, r.Seq, new DateTimeOffset(DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc)), r.Module, r.Action, r.Severity, r.UserDisplayName,
            r.EntityType, r.EntityId, r.EntityLabel, r.Summary, r.IpAddress, r.OldValues, r.NewValues))];
    }

    public async Task<CloudAuditIntegrityDto> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var report = await verifier.VerifyAsync(cancellationToken);
        return new CloudAuditIntegrityDto(
            report.IsValid,
            report.SealsChecked,
            report.RowsChecked,
            report.UnsealedRows,
            report.LastSealShortCode,
            [.. report.Findings.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Kind}: {f.Message}"))]);
    }

    private sealed class Row
    {
        public Guid Id { get; set; }

        public long Seq { get; set; }

        public DateTime OccurredAt { get; set; }

        public string Module { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string Severity { get; set; } = string.Empty;

        public string? UserDisplayName { get; set; }

        public string? EntityType { get; set; }

        public Guid? EntityId { get; set; }

        public string? EntityLabel { get; set; }

        public string? Summary { get; set; }

        public string? IpAddress { get; set; }

        public string? OldValues { get; set; }

        public string? NewValues { get; set; }
    }
}
