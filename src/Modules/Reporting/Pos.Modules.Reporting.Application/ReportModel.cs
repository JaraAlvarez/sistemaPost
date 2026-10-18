using Pos.Modules.Reporting.Contracts;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Reporting.Application;

/// <summary>
/// Parámetro de un reporte. <see cref="DefaultValue"/> admite "today", "today-N" (fechas) o un número. Los nombres de los parámetros
/// son los de la cadena de consulta (camelCase) y en el SQL van en snake_case (<c>warehouseId</c> → <c>@warehouse_id</c>).
/// </summary>
public sealed record ReportParameter(string Name, string Label, ReportParameterType Type, bool Required = false, string? DefaultValue = null)
{
    public string SqlName => ReportParameters.ToSnake(Name);
}

/// <summary>Total de una columna calculado como razón de otras dos (p. ej. margen = utilidad ÷ venta × 100).</summary>
public sealed record RatioTotal(string Numerator, string Denominator, decimal Factor);

/// <summary>
/// Columna del reporte. <see cref="IsCost"/>: se oculta sin <c>inventory.cost.view</c> ni <c>reporting.profit.view</c> (RN-REP-04).
/// <see cref="Hidden"/>: se lee del SQL (y se suma) pero nunca se publica, p. ej. la base de un cálculo. <see cref="Computed"/>: no viene del
/// SQL; la llena el <see cref="IReportCalculation"/> del reporte. <see cref="Style"/>: presentación en la pantalla.
/// </summary>
public sealed record ReportColumn(
    string Key,
    string Label,
    ReportColumnType Type,
    bool Total = false,
    bool IsCost = false,
    RatioTotal? Ratio = null,
    bool Hidden = false,
    bool Computed = false,
    ReportColumnStyle Style = ReportColumnStyle.None)
{
    public bool HasTotal => Total || Ratio is not null;
}

/// <summary>
/// Lo que ve un cálculo: la fecha de hoy, los argumentos SQL ya validados (nombres en snake_case) y el rango máximo de fechas
/// (<c>reporting.max_range_days</c>).
/// </summary>
public sealed record ReportCalculationContext(DateOnly Today, IReadOnlyList<SqlArgument> Arguments, int MaxRangeDays = 366)
{
    public object? Value(string sqlName) => Arguments.FirstOrDefault(a => a.Name == sqlName)?.Value;
}

/// <summary>Argumentos SQL adicionales y parámetros que se muestran en la pantalla y en los archivos (p. ej. los periodos comparados).</summary>
public sealed record ReportPreparation(IReadOnlyList<SqlArgument> Arguments, IReadOnlyList<KeyValuePair<string, string>> Shown)
{
    public static ReportPreparation None { get; } = new([], []);
}

/// <summary>
/// Cálculo en C# de un reporte (mejora de reportes de análisis): <see cref="Prepare"/> valida y agrega argumentos antes del SQL;
/// <see cref="Apply"/> llena las columnas <see cref="ReportColumn.Computed"/> de cada fila; <see cref="CompleteTotals"/> corrige los totales
/// que no son una suma (variaciones, promedios). Las filas y los totales llegan con TODAS las columnas de la definición, en su orden.
/// </summary>
public interface IReportCalculation
{
    Result<ReportPreparation> Prepare(ReportCalculationContext context);

    void Apply(ReportCalculationContext context, IReadOnlyList<ReportColumn> columns, IReadOnlyList<object?[]> rows);

    void CompleteTotals(ReportCalculationContext context, IReadOnlyList<ReportColumn> columns, object?[] totals);
}

/// <summary>
/// Definición de un reporte (D9-03): código, nombre, grupo, permiso, parámetros, columnas y el SQL sobre las vistas <c>reporting.*</c>.
/// El SQL recibe siempre <c>@company_id</c>, <c>@branch_id</c> (datos de este nodo, D9-13), <c>@today</c> y los parámetros declarados.
/// No termina en punto y coma: el motor le agrega el límite de filas.
/// </summary>
public sealed record ReportDefinition(
    string Code,
    string Name,
    string Group,
    string Description,
    string Permission,
    IReadOnlyList<ReportParameter> Parameters,
    IReadOnlyList<ReportColumn> Columns,
    string Sql,
    string? Note = null,
    IReportCalculation? Calculation = null)
{
    public ReportInfoDto ToInfo(bool includeCost, DateOnly? today = null) => new(
        Code,
        Name,
        Group,
        Description,
        [.. Parameters.Select(p => new ReportParameterDto(p.Name, p.Label, p.Type, p.Required, p.DefaultValue))],
        [.. VisibleColumns(includeCost).Select(c => c.ToDto())],
        today);

    /// <summary>Columnas publicadas: sin las ocultas y, sin permiso de costos, sin las de costo.</summary>
    public IReadOnlyList<ReportColumn> VisibleColumns(bool includeCost) => [.. Columns.Where(c => !c.Hidden && (includeCost || !c.IsCost))];

    /// <summary>Columnas que se leen del SQL (todas menos las calculadas en C#).</summary>
    public IReadOnlyList<ReportColumn> QueryColumns => [.. Columns.Where(c => !c.Computed)];
}

internal static class ReportColumnExtensions
{
    public static ReportColumnDto ToDto(this ReportColumn column) => new(column.Key, column.Label, column.Type, column.HasTotal, column.Style);
}

/// <summary>Parámetros comunes de los reportes.</summary>
public static class ReportParameters
{
    public static readonly ReportParameter From = new("from", "Desde (fecha de negocio)", ReportParameterType.Date, DefaultValue: "today");
    public static readonly ReportParameter To = new("to", "Hasta (fecha de negocio)", ReportParameterType.Date, DefaultValue: "today");
    public static readonly ReportParameter AsOf = new("asOf", "A la fecha (vacío = hoy, en línea)", ReportParameterType.Date);
    public static readonly ReportParameter Warehouse = new("warehouseId", "Bodega", ReportParameterType.Id);
    public static readonly ReportParameter Cashier = new("cashierId", "Cajero", ReportParameterType.Id);

    public static ReportParameter Days(string label, int defaultValue) =>
        new("days", label, ReportParameterType.Number, DefaultValue: defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static IReadOnlyList<ReportParameter> DateRange => [From, To];

    public static string ToSnake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : string.Empty) + char.ToLowerInvariant(c) : c.ToString()));
}
