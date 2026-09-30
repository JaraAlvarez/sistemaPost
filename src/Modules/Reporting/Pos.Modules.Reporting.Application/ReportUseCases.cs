using System.Globalization;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Backup.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Reporting.Contracts;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Reporting.Application;

/// <summary>Configuraciones de reportes. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class ReportingSettings
{
    public static readonly SettingDefinition<int> MaxRangeDays = new(
        "reporting.max_range_days", 366, SettingScope.Company, "Rango máximo de fechas de un reporte (RN-REP-03).",
        v => v is >= 1 and <= 1100 ? null : "Entre 1 y 1100 días.");

    public static readonly SettingDefinition<int> MaxRows = new(
        "reporting.max_rows", 100_000, SettingScope.Company, "Filas máximas de un reporte; el resultado avisa si se cortó (RN-REP-03).",
        v => v is >= 100 and <= 1_000_000 ? null : "Entre 100 y 1.000.000.");

    public static readonly SettingDefinition<int> StatementTimeoutSeconds = new(
        "reporting.statement_timeout_seconds", 30, SettingScope.Company, "Tiempo máximo de una consulta de reporte (D9-01): un reporte lento nunca frena la caja.",
        v => v is >= 5 and <= 300 ? null : "Entre 5 y 300 segundos.");

    public static readonly SettingDefinition<decimal> AntifraudThresholdFactor = new(
        "reporting.antifraud_threshold_factor", 2m, SettingScope.Company,
        "Un cajero se resalta en el panel antifraude cuando sus eventos por tiquete superan el promedio de la tienda multiplicado por este factor (D9-11).",
        v => v is >= 1m and <= 10m ? null : "Entre 1 y 10.");

    public static readonly SettingDefinition<int> DashboardExpiringDays = new(
        "reporting.dashboard_expiring_days", 15, SettingScope.Company, "Días hacia adelante de la alerta de lotes por vencer del tablero.",
        v => v is >= 1 and <= 120 ? null : "Entre 1 y 120.");

    /// <summary>Horario "fuera de horario" del reporte de auditoría (Fase 10, pregunta 6): desde esta hora…</summary>
    public static readonly SettingDefinition<int> AfterHoursStart = new(
        "reporting.after_hours_start", 22, SettingScope.Company | SettingScope.Branch,
        "Hora local desde la que la actividad se considera fuera de horario (reporte AUDIT_AFTER_HOURS).",
        v => v is >= 0 and <= 23 ? null : "Entre 0 y 23.");

    /// <summary>…hasta esta hora (sin incluirla).</summary>
    public static readonly SettingDefinition<int> AfterHoursEnd = new(
        "reporting.after_hours_end", 6, SettingScope.Company | SettingScope.Branch,
        "Hora local hasta la que la actividad se considera fuera de horario (sin incluirla).",
        v => v is >= 0 and <= 23 ? null : "Entre 0 y 23.");

    public static IEnumerable<SettingDefinition> All =>
        [MaxRangeDays, MaxRows, StatementTimeoutSeconds, AntifraudThresholdFactor, DashboardExpiringDays, AfterHoursStart, AfterHoursEnd];
}

public sealed class ReportingSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => ReportingSettings.All;
}

public sealed class ReportingPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => ReportingPermissions.All;
}

public static class ReportingErrors
{
    public static readonly Error SetupRequired = Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
    public static readonly Error NotFound = Error.NotFound("REPORTING.NOT_FOUND", "El reporte no existe.");
    public static readonly Error PermissionDenied = Error.Forbidden("AUTH.PERMISSION_DENIED", "No tiene permiso para ver este reporte.");
    public static readonly Error ExportDenied = Error.Forbidden("AUTH.PERMISSION_DENIED", "No tiene permiso para exportar reportes.");
    public static readonly Error Timeout = Error.BusinessRule(
        "REPORTING.TIMEOUT", "El reporte tardó más del tiempo permitido. Reduzca el rango de fechas o use más filtros.");
    public static readonly Error InvalidFormat = Error.Validation("REPORTING.INVALID_FORMAT", "Formato no válido: use json, xlsx, csv o pdf.");

    public static Error InvalidParameter(string name, string message) =>
        Error.Validation("REPORTING.INVALID_PARAMETER", $"Parámetro '{name}': {message}");

    public static Error RangeTooLarge(int maxDays) =>
        Error.Validation("REPORTING.RANGE_TOO_LARGE", $"El rango de fechas no puede superar {maxDays} días.");
}

// ------------------------------------------------------------------------------------------------ Puertos

/// <summary>Tipo de un argumento SQL (los nulos también llevan tipo).</summary>
public enum SqlArgumentType
{
    Uuid,
    Date,
    WholeNumber,
    Numeric,
}

public sealed record SqlArgument(string Name, SqlArgumentType Type, object? Value);

/// <summary>Consulta de un reporte: se ejecuta en una transacción de SOLO LECTURA con tiempo límite (D9-01).</summary>
public sealed record ReportQuery(string Sql, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<SqlArgument> Arguments, int RowLimit, int TimeoutSeconds);

public interface IReportQueryRunner
{
    /// <summary>Filas (una por registro, valores en el orden de <see cref="ReportQuery.Columns"/>), hasta <c>RowLimit</c> filas.</summary>
    Task<Result<IReadOnlyList<object?[]>>> RunAsync(ReportQuery query, CancellationToken cancellationToken);
}

/// <summary>Encabezado de los archivos exportados.</summary>
public sealed record ReportHeader(string CompanyName, string CompanyIdentification, string BranchName);

public sealed record DashboardRequest(Guid CompanyId, Guid BranchId, DateOnly Today, bool IncludeProfit, int ExpiringDays, int TimeoutSeconds);

public interface IReportingReadModel
{
    Task<ReportHeader> GetHeaderAsync(Guid companyId, Guid branchId, CancellationToken cancellationToken);

    Task<Result<DashboardDto>> GetDashboardAsync(DashboardRequest request, CancellationToken cancellationToken);
}

/// <summary>Documento listo para exportar (D9-09).</summary>
public sealed record ReportDocument(
    string Code,
    string Title,
    string Group,
    ReportHeader Header,
    IReadOnlyList<KeyValuePair<string, string>> Parameters,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    object?[] Totals,
    bool Truncated,
    string? Note,
    DateTimeOffset GeneratedAt);

public interface IReportExporter
{
    ReportFileDto Export(ReportDocument document, ReportFormat format);
}

// ------------------------------------------------------------------------------------------------ Motor

/// <summary>Resultado completo de un reporte (todas las filas hasta el límite, con totales).</summary>
public sealed record ExecutedReport(
    ReportDefinition Definition,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<KeyValuePair<string, string>> Parameters,
    IReadOnlyList<object?[]> Rows,
    object?[] Totals,
    bool Truncated,
    Guid CompanyId,
    Guid BranchId);

/// <summary>
/// Motor de reportes (D9-03): resuelve la definición, verifica el permiso, valida los parámetros y el rango, ejecuta el SQL con el
/// límite de filas y calcula los totales. Oculta las columnas de costo sin permiso (RN-REP-04).
/// </summary>
public sealed class ReportEngine(
    IInstallationContext installation,
    IPermissionChecker permissions,
    ISettingsReader settings,
    IReportQueryRunner runner,
    IClock clock)
{
    public async Task<bool> CanSeeCostsAsync(CancellationToken cancellationToken) =>
        await permissions.HasPermissionAsync(InventoryPermissions.CostView, cancellationToken: cancellationToken)
        || await permissions.HasPermissionAsync(ReportingPermissions.ProfitView, cancellationToken: cancellationToken);

    public async Task<Result<ExecutedReport>> ExecuteAsync(string code, IReadOnlyDictionary<string, string?> arguments, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId || installation.BranchId is not { } branchId)
        {
            return ReportingErrors.SetupRequired;
        }

        var definition = ReportCatalog.Find(code ?? string.Empty);
        if (definition is null)
        {
            return ReportingErrors.NotFound;
        }

        if (!await permissions.HasPermissionAsync(definition.Permission, cancellationToken: cancellationToken))
        {
            return ReportingErrors.PermissionDenied;
        }

        var context = new SettingContext(companyId, branchId);
        var today = clock.Today;
        var parsed = ParseArguments(definition, arguments, today);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var (sqlArguments, shown) = parsed.Value;
        if (sqlArguments.FirstOrDefault(a => a.Name == "from")?.Value is DateOnly from && sqlArguments.FirstOrDefault(a => a.Name == "to")?.Value is DateOnly to)
        {
            if (from > to)
            {
                return ReportingErrors.InvalidParameter("from", "la fecha inicial es posterior a la final.");
            }

            var maxDays = await settings.GetAsync(ReportingSettings.MaxRangeDays, context, cancellationToken);
            if (to.DayNumber - from.DayNumber + 1 > maxDays)
            {
                return ReportingErrors.RangeTooLarge(maxDays);
            }
        }

        var maxRows = await settings.GetAsync(ReportingSettings.MaxRows, context, cancellationToken);
        var timeout = await settings.GetAsync(ReportingSettings.StatementTimeoutSeconds, context, cancellationToken);
        var factor = await settings.GetAsync(ReportingSettings.AntifraudThresholdFactor, context, cancellationToken);
        var afterHoursStart = await settings.GetAsync(ReportingSettings.AfterHoursStart, context, cancellationToken);
        var afterHoursEnd = await settings.GetAsync(ReportingSettings.AfterHoursEnd, context, cancellationToken);
        List<SqlArgument> all =
        [
            new("company_id", SqlArgumentType.Uuid, companyId),
            new("branch_id", SqlArgumentType.Uuid, branchId),
            new("today", SqlArgumentType.Date, today),
            new("threshold_factor", SqlArgumentType.Numeric, factor),
            new("after_hours_start", SqlArgumentType.WholeNumber, afterHoursStart),
            new("after_hours_end", SqlArgumentType.WholeNumber, afterHoursEnd),
            .. sqlArguments,
        ];

        var rows = await runner.RunAsync(new ReportQuery(definition.Sql, definition.Columns, all, maxRows + 1, timeout), cancellationToken);
        if (rows.IsFailure)
        {
            return rows.Error;
        }

        var truncated = rows.Value.Count > maxRows;
        IReadOnlyList<object?[]> all0 = truncated ? [.. rows.Value.Take(maxRows)] : rows.Value;
        var includeCost = await CanSeeCostsAsync(cancellationToken);
        var visible = definition.VisibleColumns(includeCost);
        var indexes = visible.Select(c => definition.Columns.ToList().IndexOf(c)).ToArray();
        IReadOnlyList<object?[]> projected = indexes.Length == definition.Columns.Count
            ? all0
            : [.. all0.Select(r => indexes.Select(i => r[i]).ToArray())];
        return new ExecutedReport(definition, visible, shown, projected, ComputeTotals(visible, projected), truncated, companyId, branchId);
    }

    /// <summary>Totales de la columna: suma o razón entre dos columnas sumadas (p. ej. margen = utilidad ÷ venta × 100).</summary>
    public static object?[] ComputeTotals(IReadOnlyList<ReportColumn> columns, IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var sums = new decimal[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            sums[c] = rows.Sum(r => ToDecimal(r[c]));
        }

        var totals = new object?[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            if (column.Ratio is { } ratio)
            {
                var n = IndexOf(columns, ratio.Numerator);
                var d = IndexOf(columns, ratio.Denominator);
                totals[c] = n < 0 || d < 0 || sums[d] == 0m ? null : Math.Round(sums[n] / sums[d] * ratio.Factor, 2, MidpointRounding.AwayFromZero);
            }
            else if (column.Total)
            {
                totals[c] = column.Type == ReportColumnType.Count ? (object)(long)sums[c] : sums[c];
            }
        }

        return totals;
    }

    private static int IndexOf(IReadOnlyList<ReportColumn> columns, string key)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        long l => l,
        int i => i,
        short s => s,
        _ => 0m,
    };

    internal static Result<(List<SqlArgument> Arguments, List<KeyValuePair<string, string>> Shown)> ParseArguments(
        ReportDefinition definition, IReadOnlyDictionary<string, string?> arguments, DateOnly today)
    {
        var result = new List<SqlArgument>();
        var shown = new List<KeyValuePair<string, string>>();
        foreach (var parameter in definition.Parameters)
        {
            var raw = arguments.FirstOrDefault(a => string.Equals(a.Key, parameter.Name, StringComparison.OrdinalIgnoreCase)).Value;
            raw = string.IsNullOrWhiteSpace(raw) ? parameter.DefaultValue : raw.Trim();
            if (raw is null)
            {
                if (parameter.Required)
                {
                    return ReportingErrors.InvalidParameter(parameter.Name, "es obligatorio.");
                }

                result.Add(new SqlArgument(parameter.SqlName, TypeOf(parameter.Type), null));
                continue;
            }

            object value;
            switch (parameter.Type)
            {
                case ReportParameterType.Date:
                    if (raw.StartsWith("today", StringComparison.OrdinalIgnoreCase))
                    {
                        var offset = raw.Length > 5 && int.TryParse(raw[5..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var o) ? o : 0;
                        value = today.AddDays(offset);
                    }
                    else if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        value = date;
                    }
                    else
                    {
                        return ReportingErrors.InvalidParameter(parameter.Name, "use el formato AAAA-MM-DD.");
                    }

                    break;
                case ReportParameterType.Number:
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number is < 0 or > 3650)
                    {
                        return ReportingErrors.InvalidParameter(parameter.Name, "debe ser un número entre 0 y 3650.");
                    }

                    value = number;
                    break;
                default:
                    if (!Guid.TryParse(raw, out var id))
                    {
                        return ReportingErrors.InvalidParameter(parameter.Name, "no es un identificador válido.");
                    }

                    value = id;
                    break;
            }

            result.Add(new SqlArgument(parameter.SqlName, TypeOf(parameter.Type), value));
            shown.Add(new(parameter.Label, value is DateOnly d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return (result, shown);
    }

    private static SqlArgumentType TypeOf(ReportParameterType type) => type switch
    {
        ReportParameterType.Date => SqlArgumentType.Date,
        ReportParameterType.Number => SqlArgumentType.WholeNumber,
        _ => SqlArgumentType.Uuid,
    };
}

// ------------------------------------------------------------------------------------------------ Casos de uso

public sealed record ListReportsQuery : IQuery<IReadOnlyList<ReportInfoDto>>;

public sealed record RunReportQuery(string Code, IReadOnlyDictionary<string, string?> Arguments, int Page = 1, int PageSize = 200) : IQuery<ReportResultDto>;

/// <summary>Exportación a archivo (D9-09). Es un comando para que la auditoría (D9-12) se guarde en la transacción del caso de uso.</summary>
public sealed record ExportReportCommand(string Code, IReadOnlyDictionary<string, string?> Arguments, ReportFormat Format) : ICommand<ReportFileDto>, IAllowedWhenRestricted;

public sealed record GetDashboardQuery : IQuery<DashboardDto>;

internal sealed class ListReportsHandler(ReportEngine engine, IPermissionChecker permissions) : IQueryHandler<ListReportsQuery, IReadOnlyList<ReportInfoDto>>
{
    public async Task<Result<IReadOnlyList<ReportInfoDto>>> Handle(ListReportsQuery request, CancellationToken cancellationToken)
    {
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var permission in ReportCatalog.All.Select(r => r.Permission).Distinct())
        {
            allowed[permission] = await permissions.HasPermissionAsync(permission, cancellationToken: cancellationToken);
        }

        var includeCost = await engine.CanSeeCostsAsync(cancellationToken);
        return ReportCatalog.All.Where(r => allowed[r.Permission]).Select(r => r.ToInfo(includeCost)).ToList();
    }
}

internal sealed class RunReportHandler(ReportEngine engine, IClock clock) : IQueryHandler<RunReportQuery, ReportResultDto>
{
    public async Task<Result<ReportResultDto>> Handle(RunReportQuery request, CancellationToken cancellationToken)
    {
        var executed = await engine.ExecuteAsync(request.Code, request.Arguments, cancellationToken);
        if (executed.IsFailure)
        {
            return executed.Error;
        }

        var report = executed.Value;
        var pageSize = Math.Clamp(request.PageSize, 1, 1000);
        var page = Math.Max(request.Page, 1);
        var rows = report.Rows.Skip((page - 1) * pageSize).Take(pageSize).Select(r => ToRow(report.Columns, r)).ToList();
        return new ReportResultDto(
            report.Definition.Code,
            report.Definition.Name,
            report.Definition.Group,
            report.Parameters.ToDictionary(p => p.Key, p => (string?)p.Value),
            [.. report.Columns.Select(c => new ReportColumnDto(c.Key, c.Label, c.Type, c.HasTotal))],
            rows,
            ToRow(report.Columns, report.Totals),
            report.Rows.Count,
            page,
            pageSize,
            report.Truncated,
            report.Definition.Note,
            clock.UtcNow);
    }

    private static Dictionary<string, object?> ToRow(IReadOnlyList<ReportColumn> columns, object?[] values)
    {
        var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            row[columns[i].Key] = values[i];
        }

        return row;
    }
}

internal sealed class ExportReportHandler(
    ReportEngine engine,
    IPermissionChecker permissions,
    IReportingReadModel read,
    IReportExporter exporter,
    IAuditWriter audit,
    IClock clock) : ICommandHandler<ExportReportCommand, ReportFileDto>
{
    public async Task<Result<ReportFileDto>> Handle(ExportReportCommand request, CancellationToken cancellationToken)
    {
        if (request.Format == ReportFormat.Json)
        {
            return ReportingErrors.InvalidFormat;
        }

        if (!await permissions.HasPermissionAsync(ReportingPermissions.Export, cancellationToken: cancellationToken))
        {
            return ReportingErrors.ExportDenied;
        }

        var executed = await engine.ExecuteAsync(request.Code, request.Arguments, cancellationToken);
        if (executed.IsFailure)
        {
            return executed.Error;
        }

        var report = executed.Value;
        var header = await read.GetHeaderAsync(report.CompanyId, report.BranchId, cancellationToken);
        var file = exporter.Export(
            new ReportDocument(report.Definition.Code, report.Definition.Name, report.Definition.Group, header, report.Parameters, report.Columns,
                report.Rows, report.Totals, report.Truncated, report.Definition.Note, clock.UtcNow),
            request.Format);

        // D9-12 / RN-REP-06: quién, qué reporte, parámetros y filas.
        await audit.WriteAsync(
            new AuditEntry(
                "reporting",
                "REPORT_EXPORTED",
                EntityType: "Report",
                EntityLabel: report.Definition.Code,
                Summary: $"Exportó '{report.Definition.Name}' en {request.Format.ToString().ToUpperInvariant()} ({report.Rows.Count} filas).",
                NewValues: new Dictionary<string, object?>
                {
                    ["report"] = report.Definition.Code,
                    ["format"] = request.Format.ToString().ToUpperInvariant(),
                    ["parameters"] = report.Parameters.ToDictionary(p => p.Key, p => p.Value),
                    ["rows"] = report.Rows.Count,
                    ["truncated"] = report.Truncated,
                    ["includesCosts"] = report.Columns.Any(c => c.IsCost),
                },
                Severity: report.Columns.Any(c => c.IsCost) || report.Definition.Permission == ReportingPermissions.AntifraudView
                    ? AuditSeverity.Warning
                    : AuditSeverity.Info),
            cancellationToken);
        return file;
    }
}

internal sealed class GetDashboardHandler(
    IInstallationContext installation,
    IPermissionChecker permissions,
    ISettingsReader settings,
    IReportingReadModel read,
    IBackupStatus backups,
    IClock clock) : IQueryHandler<GetDashboardQuery, DashboardDto>
{
    public async Task<Result<DashboardDto>> Handle(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId || installation.BranchId is not { } branchId)
        {
            return ReportingErrors.SetupRequired;
        }

        var context = new SettingContext(companyId, branchId);
        var includeProfit = await permissions.HasPermissionAsync(ReportingPermissions.ProfitView, cancellationToken: cancellationToken);
        var expiringDays = await settings.GetAsync(ReportingSettings.DashboardExpiringDays, context, cancellationToken);
        var timeout = await settings.GetAsync(ReportingSettings.StatementTimeoutSeconds, context, cancellationToken);
        var dashboard = await read.GetDashboardAsync(new DashboardRequest(companyId, branchId, clock.Today, includeProfit, expiringDays, timeout), cancellationToken);
        if (dashboard.IsFailure)
        {
            return dashboard.Error;
        }

        var backupAlerts = await permissions.HasPermissionAsync(BackupPermissions.View, cancellationToken: cancellationToken)
            ? (await backups.GetAlertsAsync(cancellationToken)).Count
            : 0;
        return dashboard.Value with { GeneratedAt = clock.UtcNow, BackupAlerts = backupAlerts };
    }
}
