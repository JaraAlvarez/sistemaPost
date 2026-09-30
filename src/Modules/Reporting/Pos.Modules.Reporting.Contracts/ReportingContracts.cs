using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Reporting.Contracts;

/// <summary>Permisos de reportes (docs/fases/fase-09-propuesta.md §8, D9-10). Utilidad y antifraude son sensibles.</summary>
public static class ReportingPermissions
{
    public const string SalesBasic = "reporting.sales.basic";
    public const string SalesAdvanced = "reporting.sales.advanced";
    public const string ProfitView = "reporting.profit.view";
    public const string TaxesView = "reporting.taxes.view";
    public const string InventoryView = "reporting.inventory.view";
    public const string PurchasesView = "reporting.purchases.view";
    public const string CashView = "reporting.cash.view";
    public const string AntifraudView = "reporting.antifraud.view";

    /// <summary>El doc de la propuesta lo llama <c>reporting.export</c>; el formato de permisos exige tres partes.</summary>
    public const string Export = "reporting.report.export";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(SalesBasic, "Ver ventas por día, hora, medio de pago, cajero y caja, y el tablero del día", isSensitive: false),
        new(SalesAdvanced, "Ver ventas por producto, categoría, marca, cliente, lista, descuentos y promociones", isSensitive: false),
        new(ProfitView, "Ver utilidad, costos y márgenes de las ventas", isSensitive: true),
        new(TaxesView, "Ver impuestos de ventas y compras y el libro de ventas diario", isSensitive: false),
        new(InventoryView, "Ver reportes de inventario: valorizado, bajo mínimo, sin rotación, vencimientos y ajustes", isSensitive: false),
        new(PurchasesView, "Ver reportes de compras, devoluciones, cartera por pagar y gastos", isSensitive: false),
        new(CashView, "Ver reportes de cierres, retiros e ingresos de caja", isSensitive: false),
        new(AntifraudView, "Ver el panel antifraude por cajero y el detalle de sus eventos", isSensitive: true),
        new(Export, "Exportar reportes a Excel, CSV o PDF (queda en la auditoría)", isSensitive: true),
    ];
}

/// <summary>Tipo de una columna: define el formato en pantalla y en los archivos.</summary>
public enum ReportColumnType
{
    Text,
    Date,
    DateTime,
    Count,
    Quantity,
    Money,
    Percent,
    Boolean,
}

/// <summary>Tipo de un parámetro del reporte (se recibe en la cadena de consulta).</summary>
public enum ReportParameterType
{
    Date,
    Number,
    Id,
}

public enum ReportFormat
{
    Json,
    Xlsx,
    Csv,
    Pdf,
}

public sealed record ReportParameterDto(string Name, string Label, ReportParameterType Type, bool Required, string? DefaultValue);

public sealed record ReportColumnDto(string Key, string Label, ReportColumnType Type, bool HasTotal);

public sealed record ReportInfoDto(
    string Code,
    string Name,
    string Group,
    string Description,
    IReadOnlyList<ReportParameterDto> Parameters,
    IReadOnlyList<ReportColumnDto> Columns);

/// <summary>Resultado en JSON: filas de la página pedida y totales de TODAS las filas.</summary>
public sealed record ReportResultDto(
    string Code,
    string Name,
    string Group,
    IReadOnlyDictionary<string, string?> Parameters,
    IReadOnlyList<ReportColumnDto> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlyDictionary<string, object?> Totals,
    int TotalRows,
    int Page,
    int PageSize,
    bool Truncated,
    string? Note,
    DateTimeOffset GeneratedAt);

/// <summary>Archivo exportado.</summary>
public sealed record ReportFileDto(string FileName, string ContentType, byte[] Content, int Rows, bool Truncated);

public sealed record DashboardSalesDto(DateOnly BusinessDate, int Tickets, decimal NetSales, decimal AverageTicket, int VoidedTickets);

public sealed record DashboardHourDto(int Hour, int Tickets, decimal Sales);

public sealed record DashboardProductDto(Guid ProductId, string Sku, string Name, decimal Quantity, decimal Sales);

/// <summary>Tablero del día (§6): hoy vs. el mismo día de la semana anterior y alertas de operación.</summary>
public sealed record DashboardDto(
    DashboardSalesDto Today,
    DashboardSalesDto SameDayLastWeek,
    decimal? GrossProfitToday,
    IReadOnlyList<DashboardHourDto> SalesByHour,
    IReadOnlyList<DashboardProductDto> TopProducts,
    int OpenCashSessions,
    int ClosingsWithUnreviewedDifference,
    int ProductsBelowMinimum,
    int LotsExpiringSoon,
    int DataRequestsDueSoon,
    int OpenIntegrityIncidents,
    DateTimeOffset GeneratedAt,
    int BackupAlerts = 0);
