using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Files;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Audit.Application;
using Pos.Modules.Audit.Contracts;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Audit.Infrastructure;

/// <summary>Conexión y transacción del contexto EF: las verificaciones e incidentes se guardan en la transacción del caso de uso.</summary>
internal static class AuditDbSession
{
    public static async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(PosDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }
}

/// <summary>Verificador + tablas <c>audit.verification_runs</c>, <c>integrity_incidents</c> y sus reconocimientos (D10-04/D10-05).</summary>
internal sealed class AuditIntegrityStore(PosDbContext context, AuditVerifier verifier, IInstallationContext installation) : IAuditIntegrityStore
{
    public async Task<long?> LastVerifiedSealAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await AuditDbSession.OpenAsync(context, cancellationToken);
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            SELECT last_seal_no FROM audit.verification_runs
            WHERE node_id = @node AND last_seal_no IS NOT NULL ORDER BY started_at DESC LIMIT 1
            """,
            new { node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<IntegrityCheck> VerifyAsync(long? rowsVerifiedUpToSeal, CancellationToken cancellationToken)
    {
        var report = await verifier.VerifyAsync(
            rowsVerifiedUpToSeal is { } from ? new Dictionary<Guid, long> { [installation.NodeId] = from } : null, cancellationToken);
        long? lastSealNo = report.LastSealNoByNode.TryGetValue(installation.NodeId, out var n) ? n : null;
        return new IntegrityCheck(
            [.. report.Findings.Select(f => new IntegrityFinding(f.Kind.ToString(), f.NodeId, f.SealNo, f.Seq, f.Message))],
            report.NodesChecked, report.SealsChecked, report.RowsChecked, report.UnsealedRows, lastSealNo, report.LastSealHash, report.LastSealShortCode);
    }

    public async Task<IReadOnlySet<string>> AcknowledgedFindingKeysAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await AuditDbSession.OpenAsync(context, cancellationToken);
        var documents = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT r.findings::text FROM audit.integrity_incidents i
            JOIN audit.integrity_incident_acknowledgements a ON a.incident_id = i.id
            JOIN audit.verification_runs r ON r.id = i.verification_run_id
            WHERE i.node_id = @node
            """,
            new { node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
        return documents.SelectMany(AuditReadModel.Findings)
            .Select(f => new IntegrityFinding(f.Kind, f.NodeId, f.SealNo, f.Seq, f.Message).Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task SaveRunAsync(VerificationRunRecord run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (connection, transaction) = await AuditDbSession.OpenAsync(context, cancellationToken);
        var check = run.Check;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit.verification_runs (id, node_id, company_id, kind, started_at, finished_at, from_seal_no, last_seal_no, last_seal_code,
                seals_checked, rows_checked, unsealed_rows, is_valid, findings_count, findings, requested_by)
            VALUES (@Id, @node, @company, @Kind, @StartedAt, @FinishedAt, @FromSealNo, @lastSealNo, @lastSealCode, @sealsChecked, @rowsChecked,
                @unsealedRows, @isValid, @findingsCount, @findings::jsonb, @RequestedBy)
            """,
            new
            {
                run.Id, node = installation.NodeId, company = installation.CompanyId, run.Kind, run.StartedAt, run.FinishedAt, run.FromSealNo,
                lastSealNo = check.LastSealNo, lastSealCode = check.LastSealCode, sealsChecked = check.SealsChecked, rowsChecked = check.RowsChecked,
                unsealedRows = check.UnsealedRows, isValid = check.Findings.Count == 0, findingsCount = check.Findings.Count,
                findings = JsonSerializer.Serialize(check.Findings.Take(500).Select(f => f.ToDto()), AuditReadModel.Json), run.RequestedBy,
            },
            transaction, cancellationToken: cancellationToken));
    }

    public async Task SaveIncidentAsync(Guid id, Guid runId, DateTimeOffset detectedAt, int findingsCount, string summary, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await AuditDbSession.OpenAsync(context, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit.integrity_incidents (id, node_id, company_id, verification_run_id, detected_at, findings_count, summary)
            VALUES (@id, @node, @company, @runId, @detectedAt, @findingsCount, @summary)
            """,
            new { id, node = installation.NodeId, company = installation.CompanyId, runId, detectedAt, findingsCount, summary },
            transaction, cancellationToken: cancellationToken));
    }

    public async Task<IncidentState?> GetIncidentAsync(Guid id, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await AuditDbSession.OpenAsync(context, cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<(Guid Id, bool Acknowledged, int FindingsCount, string Summary)?>(new CommandDefinition(
            """
            SELECT i.id, EXISTS (SELECT 1 FROM audit.integrity_incident_acknowledgements a WHERE a.incident_id = i.id), i.findings_count, i.summary
            FROM audit.integrity_incidents i WHERE i.id = @id AND i.node_id = @node
            """,
            new { id, node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
        return row is { } r ? new IncidentState(r.Id, r.Acknowledged, r.FindingsCount, r.Summary) : null;
    }

    public async Task SaveAcknowledgementAsync(Guid id, Guid incidentId, Guid userId, DateTimeOffset at, string note, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await AuditDbSession.OpenAsync(context, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit.integrity_incident_acknowledgements (id, incident_id, acknowledged_by, acknowledged_at, note)
            VALUES (@id, @incidentId, @userId, @at, @note)
            """,
            new { id, incidentId, userId, at, note }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<int> OpenIncidentsAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await AuditDbSession.OpenAsync(context, cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*)::int FROM audit.integrity_incidents i
            WHERE i.node_id = @node AND NOT EXISTS (SELECT 1 FROM audit.integrity_incident_acknowledgements a WHERE a.incident_id = i.id)
            """,
            new { node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
    }
}

internal sealed class IntegrityStatus(IAuditIntegrityStore store) : IIntegrityStatus
{
    public Task<int> OpenIncidentsAsync(CancellationToken cancellationToken) => store.OpenIncidentsAsync(cancellationToken);
}

/// <summary>
/// Verificación automática (D10-04): cada 15 minutos revisa si ya pasó la hora configurada (<c>audit.verification_hour</c>, 03:00) y si
/// no hubo una verificación automática en las últimas 20 horas. El día configurado (<c>audit.full_verification_weekday</c>, domingo) es
/// COMPLETA; los demás, INCREMENTAL. Nunca bloquea la venta: es una lectura con foto consistente en su propia conexión.
/// </summary>
internal sealed partial class AuditVerificationScheduler(
    IServiceScopeFactory scopes, DatabaseReadiness readiness, IInstallationContext installation, IClock clock, ILogger<AuditVerificationScheduler> logger)
    : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    internal async Task<bool> RunIfDueAsync(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady || installation.CompanyId is not { } companyId)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var settings = services.GetRequiredService<ISettingsReader>();
        var settingContext = new SettingContext(companyId);
        var hour = await settings.GetAsync(AuditSettings.VerificationHour, settingContext, cancellationToken);
        var local = clock.ToBusinessTime(clock.UtcNow);
        if (local.Hour < hour)
        {
            return false;
        }

        var context = services.GetRequiredService<PosDbContext>();
        var (connection, _) = await AuditDbSession.OpenAsync(context, cancellationToken);
        var last = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT max(started_at) FROM audit.verification_runs WHERE node_id = @node AND kind IN ('INCREMENTAL', 'FULL')",
            new { node = installation.NodeId }, cancellationToken: cancellationToken));
        if (last is { } previous && clock.UtcNow - new DateTimeOffset(DateTime.SpecifyKind(previous, DateTimeKind.Utc)) < TimeSpan.FromHours(20))
        {
            return false;
        }

        var fullDay = await settings.GetAsync(AuditSettings.FullVerificationWeekday, settingContext, cancellationToken);
        var kind = (int)local.DayOfWeek == fullDay ? IntegrityService.Full : IntegrityService.Incremental;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var result = await services.GetRequiredService<IntegrityService>().RunAsync(kind, null, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogCompleted(logger, kind, result.SealsChecked, result.RowsChecked, result.Findings.Count);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la verificación automática de la bitácora.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verificación {Kind} de la bitácora: {Seals} sellos, {Rows} filas, {Findings} hallazgos.")]
    private static partial void LogCompleted(ILogger logger, string kind, int seals, long rows, int findings);
}

/// <summary>
/// Eventos del sistema al arrancar con la BD lista (§5.1): <c>SERVER_STARTED</c> (versión y número de migraciones aplicadas),
/// <c>DATABASE_MIGRATED</c> si cambió el número de migraciones desde el arranque anterior y <c>CLOCK_JUMP_DETECTED</c> si el reloj del
/// servidor está atrasado frente a la última fila de la bitácora del nodo más de lo tolerado.
/// </summary>
internal sealed class AuditStartupHook : IDatabaseReadyHook
{
    public async Task RunAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var context = scopedServices.GetRequiredService<PosDbContext>();
        var installation = scopedServices.GetRequiredService<IInstallationContext>();
        var clock = scopedServices.GetRequiredService<IClock>();
        var audit = scopedServices.GetRequiredService<IAuditWriter>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var (connection, dbTransaction) = await AuditDbSession.OpenAsync(context, cancellationToken);
        var migrations = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM system.schema_migrations", transaction: dbTransaction, cancellationToken: cancellationToken));
        var previous = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT new_values ->> 'migrations' FROM audit.audit_log
            WHERE node_id = @node AND action = 'SERVER_STARTED' ORDER BY occurred_at DESC, seq DESC LIMIT 1
            """,
            new { node = installation.NodeId }, dbTransaction, cancellationToken: cancellationToken));
        var lastRow = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT max(occurred_at) FROM audit.audit_log WHERE node_id = @node",
            new { node = installation.NodeId }, dbTransaction, cancellationToken: cancellationToken));

        var now = clock.UtcNow;
        var tolerance = installation.CompanyId is { } company
            ? await scopedServices.GetRequiredService<ISettingsReader>().GetAsync(AuditSettings.ClockToleranceMinutes, new SettingContext(company), cancellationToken)
            : AuditSettings.ClockToleranceMinutes.DefaultValue;
        if (lastRow is { } last && new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) - now > TimeSpan.FromMinutes(tolerance))
        {
            await audit.WriteAsync(
                new AuditEntry("system", "CLOCK_JUMP_DETECTED", "Node", installation.NodeId, "Reloj del servidor",
                    string.Create(CultureInfo.InvariantCulture,
                        $"El reloj del servidor ({now:yyyy-MM-dd HH:mm} UTC) está atrasado frente a la última fila de la bitácora ({last:yyyy-MM-dd HH:mm} UTC). Revise la hora del equipo."),
                    Severity: AuditSeverity.Critical),
                cancellationToken);
        }

        var count = migrations.ToString(CultureInfo.InvariantCulture);
        if (previous is not null && previous != count)
        {
            await audit.WriteAsync(
                new AuditEntry("system", "DATABASE_MIGRATED", "Node", installation.NodeId, "Base de datos",
                    $"La base de datos pasó de {previous} a {count} migraciones aplicadas.", Severity: AuditSeverity.Warning),
                cancellationToken);
        }

        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "desconocida";
        await audit.WriteAsync(
            new AuditEntry("system", "SERVER_STARTED", "Node", installation.NodeId, "Servidor",
                $"Servidor iniciado (versión {version}, {count} migraciones aplicadas).",
                NewValues: new Dictionary<string, object?> { ["version"] = version, ["migrations"] = count }),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>Constancia de integridad en PDF (D10-09): el último sello del nodo y cómo comprobarlo.</summary>
internal sealed class IntegrityCertificateRenderer(NpgsqlDataSource dataSource, IInstallationContext installation, IClock clock) : IIntegrityCertificateRenderer
{
    public async Task<(string CompanyName, string CompanyIdentification, string BranchName)> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<(string Company, string Identification, string Branch)?>(new CommandDefinition(
            """
            SELECT COALESCE(NULLIF(c.trade_name, ''), c.legal_name), c.identification_number || COALESCE('-' || c.check_digit, ''), b.code || ' · ' || b.name
            FROM org.branches b JOIN org.companies c ON c.id = b.company_id WHERE b.id = @branch
            """,
            new { branch = installation.BranchId }, cancellationToken: cancellationToken));
        return row is { } r ? (r.Company, r.Identification, r.Branch) : (string.Empty, string.Empty, string.Empty);
    }

    public byte[] Render(IntegrityCertificateData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        PdfFonts.EnsureConfigured();
        var document = new Document();
        document.Info.Title = "Constancia de integridad de la bitácora";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.Family;
        normal.Font.Size = 10;
        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.Letter;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        var title = section.AddParagraph(data.CompanyName);
        title.Format.Font.Size = 14;
        title.Format.Font.Bold = true;
        section.AddParagraph($"{data.CompanyIdentification} · {data.BranchName}");
        var heading = section.AddParagraph("Constancia de integridad de la bitácora de auditoría");
        heading.Format.Font.Size = 13;
        heading.Format.Font.Bold = true;
        heading.Format.SpaceBefore = Unit.FromPoint(12);
        heading.Format.SpaceAfter = Unit.FromPoint(8);

        var table = section.AddTable();
        table.Borders.Width = 0.25;
        table.AddColumn(Unit.FromCentimeter(6));
        table.AddColumn(Unit.FromCentimeter(11));
        void Row(string label, string value)
        {
            var row = table.AddRow();
            row.Cells[0].AddParagraph(label).Format.Font.Bold = true;
            row.Cells[1].AddParagraph(value);
        }

        Row("Nodo (instalación)", data.NodeId.ToString("D"));
        Row("Último sello", data.SealNo is { } no ? $"#{no.ToString(CultureInfo.InvariantCulture)}" : "Sin sellos todavía");
        Row("Código del sello", data.SealCode ?? "—");
        Row("Fecha del sello", data.SealedAt is { } at ? Local(at) : "—");
        Row("Última verificación", data.LastVerification is { } v
            ? $"{Local(v.StartedAt)} · {Kind(v.Kind)} · {(v.IsValid ? "sin hallazgos" : $"{v.Findings.Count} hallazgo(s)")}"
            : "Aún no se ha verificado");
        Row("Incidentes de integridad abiertos", data.OpenIncidents.ToString(CultureInfo.InvariantCulture));
        Row("Emitida por", data.IssuedBy);
        Row("Emitida el", Local(data.IssuedAt));

        var help = section.AddParagraph(
            "Guarde esta constancia fuera del equipo (impresa o en otro lugar). El código resume toda la bitácora hasta ese sello: si alguien "
            + "modifica, borra o reescribe registros anteriores, el código deja de coincidir. Para comprobarlo: "
            + $"GET /api/v1/audit/seals/{data.SealNo?.ToString(CultureInfo.InvariantCulture) ?? "N"}/check?code={data.SealCode ?? "CODIGO"} "
            + "o, en el servidor, Pos.Server.Migrator verify-audit --seal N --code CODIGO.");
        help.Format.SpaceBefore = Unit.FromPoint(12);
        help.Format.Font.Size = 9;

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, false);
        return stream.ToArray();
    }

    private string Local(DateTimeOffset instant) =>
        clock.ToBusinessTime(instant).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Kind(string kind) => kind switch
    {
        IntegrityService.Incremental => "incremental",
        IntegrityService.Full => "completa",
        _ => "manual",
    };
}
