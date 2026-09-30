using Pos.Modules.Reporting.Contracts;

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

/// <summary>Columna del reporte. <see cref="IsCost"/>: se oculta sin <c>inventory.cost.view</c> ni <c>reporting.profit.view</c> (RN-REP-04).</summary>
public sealed record ReportColumn(string Key, string Label, ReportColumnType Type, bool Total = false, bool IsCost = false, RatioTotal? Ratio = null)
{
    public bool HasTotal => Total || Ratio is not null;
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
    string? Note = null)
{
    public ReportInfoDto ToInfo(bool includeCost) => new(
        Code,
        Name,
        Group,
        Description,
        [.. Parameters.Select(p => new ReportParameterDto(p.Name, p.Label, p.Type, p.Required, p.DefaultValue))],
        [.. VisibleColumns(includeCost).Select(c => new ReportColumnDto(c.Key, c.Label, c.Type, c.HasTotal))]);

    public IReadOnlyList<ReportColumn> VisibleColumns(bool includeCost) => [.. Columns.Where(c => includeCost || !c.IsCost)];
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
