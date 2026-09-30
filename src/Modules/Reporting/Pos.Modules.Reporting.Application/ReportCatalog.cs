using Pos.Modules.Audit.Contracts;
using Pos.Modules.Reporting.Contracts;
using static Pos.Modules.Reporting.Application.ReportParameters;
using T = Pos.Modules.Reporting.Contracts.ReportColumnType;

namespace Pos.Modules.Reporting.Application;

/// <summary>
/// Catálogo de reportes (docs/fases/fase-09-propuesta.md §5). Agregar un reporte = agregar una definición aquí.
/// Reglas: fecha de negocio (RN-REP-01); ventas netas = completadas − créditos de cambios − reintegros (RN-REP-02, D9-05);
/// utilidad con el costo guardado en cada línea al cobrar, neto de lo devuelto (D9-06); inventario a una fecha con el saldo del
/// último movimiento del kardex (D9-07).
/// </summary>
public static class ReportCatalog
{
    public const string Sales = "Ventas";
    public const string Profit = "Utilidad";
    public const string Taxes = "Impuestos";
    public const string Inventory = "Inventario";
    public const string Purchases = "Compras y gastos";
    public const string Cash = "Caja";
    public const string Antifraud = "Antifraude";
    public const string AuditGroup = "Auditoría";

    /// <summary>La bitácora es por nodo: filas de la empresa y del sistema (sin empresa).</summary>
    private const string AuditCompany = "(a.company_id = @company_id OR a.company_id IS NULL)";

    private const string SalesInRange = "s.company_id = @company_id AND s.branch_id = @branch_id AND s.business_date BETWEEN @from AND @to";
    private const string LinesSold =
        "l.company_id = @company_id AND l.branch_id = @branch_id AND l.business_date BETWEEN @from AND @to AND l.sale_status = 'COMPLETED' AND l.line_status = 'ACTIVE'";
    private const string LinesReturned =
        "rl.company_id = @company_id AND rl.branch_id = @branch_id AND rl.business_date BETWEEN @from AND @to AND rl.status = 'COMPLETED'";
    private const string NoBrand = "'00000000-0000-0000-0000-000000000000'::uuid";

    private static readonly ReportColumn[] ProfitColumns =
    [
        new("net_sales", "Venta neta sin impuestos", T.Money, Total: true),
        new("cost", "Costo", T.Money, Total: true, IsCost: true),
        new("profit", "Utilidad", T.Money, Total: true, IsCost: true),
        new("margin", "Margen %", T.Percent, IsCost: true, Ratio: new RatioTotal("profit", "net_sales", 100m)),
    ];

    private static readonly ReportColumn[] MixColumns =
    [
        new("tickets", "Tiquetes", T.Count),
        new("quantity_sold", "Unidades vendidas", T.Quantity, Total: true),
        new("quantity_returned", "Unidades devueltas", T.Quantity, Total: true),
        new("gross", "Venta bruta", T.Money, Total: true),
        new("discounts", "Promociones y descuentos", T.Money, Total: true),
        new("total", "Venta con impuestos", T.Money, Total: true),
        new("returned", "Devuelto (cambios y garantías)", T.Money, Total: true),
        new("net_sales", "Venta neta", T.Money, Total: true),
    ];

    public static IReadOnlyList<ReportDefinition> All { get; } =
    [
        // ------------------------------------------------------------------------------------------------ Ventas
        new("SALES_DAILY", "Resumen diario de ventas", Sales,
            "Por fecha de negocio: tiquetes, bruto, promociones, descuentos, base, impuestos, redondeo, total, cambios, reintegros, neto y anuladas.",
            ReportingPermissions.SalesBasic, DateRange,
            [
                new("business_date", "Fecha", T.Date),
                new("tickets", "Tiquetes", T.Count, Total: true),
                new("gross", "Bruto", T.Money, Total: true),
                new("promotions", "Promociones", T.Money, Total: true),
                new("discounts", "Descuentos", T.Money, Total: true),
                new("subtotal", "Base (sin impuestos)", T.Money, Total: true),
                new("taxes", "Impuestos", T.Money, Total: true),
                new("rounding", "Ajuste de redondeo", T.Money, Total: true),
                new("total", "Total vendido", T.Money, Total: true),
                new("exchanges", "Créditos de cambios", T.Money, Total: true),
                new("refunds", "Reintegros por garantía", T.Money, Total: true),
                new("net_sales", "Ventas netas", T.Money, Total: true),
                new("average_ticket", "Ticket promedio", T.Money, Ratio: new RatioTotal("total", "tickets", 1m)),
                new("voided_tickets", "Anuladas", T.Count, Total: true),
                new("voided_total", "Valor anulado", T.Money, Total: true),
            ],
            $"""
            WITH d AS (
                SELECT s.business_date,
                       count(*) FILTER (WHERE s.status = 'COMPLETED') AS tickets,
                       COALESCE(sum(s.gross) FILTER (WHERE s.status = 'COMPLETED'), 0) AS gross,
                       COALESCE(sum(s.promotion_total) FILTER (WHERE s.status = 'COMPLETED'), 0) AS promotions,
                       COALESCE(sum(s.discount_total) FILTER (WHERE s.status = 'COMPLETED'), 0) AS discounts,
                       COALESCE(sum(s.subtotal) FILTER (WHERE s.status = 'COMPLETED'), 0) AS subtotal,
                       COALESCE(sum(s.tax_total) FILTER (WHERE s.status = 'COMPLETED'), 0) AS taxes,
                       COALESCE(sum(s.rounding_adjustment) FILTER (WHERE s.status = 'COMPLETED'), 0) AS rounding,
                       COALESCE(sum(s.total) FILTER (WHERE s.status = 'COMPLETED'), 0) AS total,
                       count(*) FILTER (WHERE s.status = 'VOIDED') AS voided_tickets,
                       COALESCE(sum(s.total) FILTER (WHERE s.status = 'VOIDED'), 0) AS voided_total
                FROM reporting.sales s
                WHERE {SalesInRange} AND s.status IN ('COMPLETED', 'VOIDED')
                GROUP BY s.business_date),
            r AS (
                SELECT r.business_date,
                       COALESCE(sum(r.credit_total) FILTER (WHERE r.kind = 'EXCHANGE'), 0) AS exchanges,
                       COALESCE(sum(r.credit_total) FILTER (WHERE r.kind = 'WARRANTY_REFUND'), 0) AS refunds
                FROM reporting.returns r
                WHERE r.company_id = @company_id AND r.branch_id = @branch_id AND r.business_date BETWEEN @from AND @to AND r.status = 'COMPLETED'
                GROUP BY r.business_date)
            SELECT business_date, COALESCE(d.tickets, 0) AS tickets, COALESCE(d.gross, 0) AS gross, COALESCE(d.promotions, 0) AS promotions,
                   COALESCE(d.discounts, 0) AS discounts, COALESCE(d.subtotal, 0) AS subtotal, COALESCE(d.taxes, 0) AS taxes,
                   COALESCE(d.rounding, 0) AS rounding, COALESCE(d.total, 0) AS total, COALESCE(r.exchanges, 0) AS exchanges,
                   COALESCE(r.refunds, 0) AS refunds, COALESCE(d.total, 0) - COALESCE(r.exchanges, 0) - COALESCE(r.refunds, 0) AS net_sales,
                   round(d.total / NULLIF(d.tickets, 0), 2) AS average_ticket, COALESCE(d.voided_tickets, 0) AS voided_tickets,
                   COALESCE(d.voided_total, 0) AS voided_total
            FROM d FULL JOIN r USING (business_date)
            ORDER BY business_date
            """,
            "Se agrupa por jornada (fecha de negocio): una venta de las 00:30 pertenece al turno que la hizo."),

        new("SALES_BY_HOUR", "Ventas por hora", Sales, "Tiquetes y ventas por hora del día (hora local de Colombia).",
            ReportingPermissions.SalesBasic, DateRange,
            [
                new("hour", "Hora", T.Count),
                new("tickets", "Tiquetes", T.Count, Total: true),
                new("total", "Ventas", T.Money, Total: true),
                new("average_ticket", "Ticket promedio", T.Money, Ratio: new RatioTotal("total", "tickets", 1m)),
            ],
            $"""
            SELECT extract(hour FROM s.completed_local)::int AS hour, count(*) AS tickets, sum(s.total) AS total,
                   round(sum(s.total) / count(*), 2) AS average_ticket
            FROM reporting.sales s
            WHERE {SalesInRange} AND s.status = 'COMPLETED'
            GROUP BY 1
            ORDER BY 1
            """,
            "La hora es la hora real del cobro; el rango de fechas es por jornada."),

        new("SALES_BY_PAYMENT", "Ventas por medio de pago", Sales,
            "Valor aplicado por medio de pago de las ventas completadas (cuadra con la caja por medio) y reintegros por garantía.",
            ReportingPermissions.SalesBasic, DateRange,
            [
                new("method_code", "Código", T.Text),
                new("method_name", "Medio de pago", T.Text),
                new("transactions", "Transacciones", T.Count, Total: true),
                new("applied", "Valor aplicado", T.Money, Total: true),
                new("refunds", "Reintegros", T.Money, Total: true),
                new("net", "Neto", T.Money, Total: true),
            ],
            """
            WITH p AS (
                SELECT p.payment_method_id, max(p.method_code) AS method_code, max(p.method_name) AS method_name, count(*) AS transactions,
                       sum(p.applied) AS applied
                FROM reporting.payments p
                WHERE p.company_id = @company_id AND p.branch_id = @branch_id AND p.business_date BETWEEN @from AND @to AND p.sale_status = 'COMPLETED'
                GROUP BY p.payment_method_id),
            r AS (
                SELECT r.refund_payment_method_id AS payment_method_id, sum(r.credit_total) AS refunds
                FROM reporting.returns r
                WHERE r.company_id = @company_id AND r.branch_id = @branch_id AND r.business_date BETWEEN @from AND @to
                  AND r.status = 'COMPLETED' AND r.kind = 'WARRANTY_REFUND'
                GROUP BY r.refund_payment_method_id)
            SELECT COALESCE(p.method_code, '?') AS method_code, COALESCE(p.method_name, 'Reintegro sin ventas del medio') AS method_name,
                   COALESCE(p.transactions, 0) AS transactions, COALESCE(p.applied, 0) AS applied, COALESCE(r.refunds, 0) AS refunds,
                   COALESCE(p.applied, 0) - COALESCE(r.refunds, 0) AS net
            FROM p FULL JOIN r ON r.payment_method_id = p.payment_method_id
            ORDER BY applied DESC
            """),

        new("SALES_BY_CASHIER", "Ventas por cajero", Sales, "Tiquetes, unidades, ventas, ticket promedio y anuladas por cajero.",
            ReportingPermissions.SalesBasic, DateRange, GroupedSalesColumns("cashier_name", "Cajero"), GroupedSalesSql("s.cashier_name", "cashier_name")),

        new("SALES_BY_TERMINAL", "Ventas por caja", Sales, "Tiquetes, unidades, ventas, ticket promedio y anuladas por caja.",
            ReportingPermissions.SalesBasic, DateRange, GroupedSalesColumns("terminal_name", "Caja"),
            GroupedSalesSql("s.terminal_code || ' · ' || s.terminal_name", "terminal_name")),

        new("SALES_BY_PRODUCT", "Ventas por producto", Sales,
            "Unidades y ventas netas por producto (más vendidos primero; los menos vendidos al final).",
            ReportingPermissions.SalesAdvanced, DateRange,
            [new("sku", "SKU", T.Text), new("product_name", "Producto", T.Text), new("category_name", "Categoría", T.Text), .. MixColumns],
            MixSql("l.product_id", "rl.product_id",
                "p.sku, p.product_name, p.category_name", "JOIN reporting.products p ON p.product_id = x.k")),

        new("SALES_BY_CATEGORY", "Ventas por categoría", Sales, "Unidades y ventas netas por categoría.",
            ReportingPermissions.SalesAdvanced, DateRange,
            [new("category_path", "Ruta", T.Text), new("category_name", "Categoría", T.Text), .. MixColumns],
            MixSql("l.category_id", "rl.category_id",
                "c.category_path, c.category_name", "JOIN reporting.categories c ON c.category_id = x.k")),

        new("SALES_BY_BRAND", "Ventas por marca", Sales, "Unidades y ventas netas por marca.",
            ReportingPermissions.SalesAdvanced, DateRange,
            [new("brand_name", "Marca", T.Text), .. MixColumns],
            MixSql($"COALESCE(l.brand_id, {NoBrand})", $"COALESCE(rl.brand_id, {NoBrand})",
                "COALESCE(b.brand_name, '(sin marca)') AS brand_name", "LEFT JOIN reporting.brands b ON b.brand_id = x.k")),

        new("SALES_BY_CUSTOMER", "Ventas por cliente", Sales,
            "Compras por cliente identificado. Solo se detallan los clientes con autorización de datos para atención (Ley 1581); los demás se agrupan.",
            ReportingPermissions.SalesAdvanced, DateRange,
            [
                new("customer", "Cliente", T.Text),
                new("identification", "Identificación", T.Text),
                new("tickets", "Tiquetes", T.Count, Total: true),
                new("total", "Compras", T.Money, Total: true),
                new("average_ticket", "Ticket promedio", T.Money, Ratio: new RatioTotal("total", "tickets", 1m)),
                new("last_purchase", "Última compra", T.Date),
            ],
            $"""
            WITH x AS (
                SELECT CASE WHEN s.customer_id IS NULL THEN 'CONSUMIDOR FINAL'
                            WHEN cc.granted THEN s.customer_name
                            ELSE 'CLIENTES SIN AUTORIZACIÓN DE DATOS' END AS customer,
                       CASE WHEN s.customer_id IS NOT NULL AND cc.granted THEN s.customer_identification END AS identification,
                       s.total, s.business_date
                FROM reporting.sales s
                LEFT JOIN reporting.customer_consents cc
                       ON cc.company_id = s.company_id AND cc.party_id = s.customer_id AND cc.purpose = 'SERVICE'
                WHERE {SalesInRange} AND s.status = 'COMPLETED')
            SELECT customer, identification, count(*) AS tickets, sum(total) AS total, round(sum(total) / count(*), 2) AS average_ticket,
                   max(business_date) AS last_purchase
            FROM x
            GROUP BY customer, identification
            ORDER BY total DESC
            """,
            "RN-REP-05: el detalle por cliente exige la autorización SERVICE vigente."),

        new("SALES_BY_PRICE_LIST", "Ventas por lista de precio", Sales, "Tiquetes y ventas por lista de precio y grupo de cliente.",
            ReportingPermissions.SalesAdvanced, DateRange,
            [
                new("price_list_code", "Lista", T.Text),
                new("customer_group_code", "Grupo de cliente", T.Text),
                new("tickets", "Tiquetes", T.Count, Total: true),
                new("total", "Ventas", T.Money, Total: true),
            ],
            $"""
            SELECT COALESCE(s.price_list_code, '(general)') AS price_list_code, COALESCE(s.customer_group_code, '(sin grupo)') AS customer_group_code,
                   count(*) AS tickets, sum(s.total) AS total
            FROM reporting.sales s
            WHERE {SalesInRange} AND s.status = 'COMPLETED'
            GROUP BY 1, 2
            ORDER BY total DESC
            """),

        new("DISCOUNTS_AND_PROMOTIONS", "Descuentos y promociones", Sales,
            "Descuentos manuales (quién los aplicó y quién autorizó) y promociones aplicadas en ventas completadas.",
            ReportingPermissions.SalesAdvanced, DateRange,
            [
                new("kind", "Tipo", T.Text),
                new("description", "Promoción / alcance", T.Text),
                new("applied_by", "Aplicó", T.Text),
                new("authorized_by", "Autorizó", T.Text),
                new("times", "Veces", T.Count, Total: true),
                new("amount", "Valor", T.Money, Total: true),
            ],
            """
            SELECT 'DESCUENTO MANUAL' AS kind, CASE d.scope WHEN 'LINE' THEN 'Línea' ELSE 'Toda la venta' END AS description,
                   d.applied_by_name AS applied_by, COALESCE(d.authorized_by_name, '(con permiso propio)') AS authorized_by, count(*) AS times,
                   COALESCE(sum(e.amount), 0) AS amount
            FROM reporting.discounts d
            JOIN reporting.antifraud_events e ON e.event_type = 'MANUAL_DISCOUNT' AND e.source_id = d.discount_id
            WHERE d.company_id = @company_id AND d.branch_id = @branch_id AND d.business_date BETWEEN @from AND @to
              AND d.sale_status = 'COMPLETED' AND d.status = 'ACTIVE'
            GROUP BY 1, 2, 3, 4
            UNION ALL
            SELECT 'PROMOCIÓN', l.promotion_name, NULL, NULL, count(*), sum(l.promotion_discount)
            FROM reporting.sale_lines l
            WHERE l.company_id = @company_id AND l.branch_id = @branch_id AND l.business_date BETWEEN @from AND @to
              AND l.sale_status = 'COMPLETED' AND l.line_status = 'ACTIVE' AND l.promotion_id IS NOT NULL
            GROUP BY l.promotion_name
            ORDER BY 1, 6 DESC
            """),

        // ------------------------------------------------------------------------------------------------ Utilidad
        new("PROFIT_BY_DAY", "Utilidad por día", Profit, "Venta neta sin impuestos, costo del kardex, utilidad y margen por fecha de negocio.",
            ReportingPermissions.ProfitView, DateRange, [new("business_date", "Fecha", T.Date), .. ProfitColumns],
            ProfitSql("l.business_date", "rl.business_date", string.Empty, "x.k AS business_date", string.Empty, "x.k")),

        new("PROFIT_BY_PRODUCT", "Utilidad por producto", Profit, "Venta neta sin impuestos, costo, utilidad y margen por producto.",
            ReportingPermissions.ProfitView, DateRange,
            [new("sku", "SKU", T.Text), new("product_name", "Producto", T.Text), new("category_name", "Categoría", T.Text), .. ProfitColumns],
            ProfitSql("l.product_id", "rl.product_id", string.Empty, "p.sku, p.product_name, p.category_name",
                "JOIN reporting.products p ON p.product_id = x.k", "profit DESC")),

        new("PROFIT_BY_CATEGORY", "Utilidad por categoría", Profit, "Venta neta sin impuestos, costo, utilidad y margen por categoría.",
            ReportingPermissions.ProfitView, DateRange,
            [new("category_path", "Ruta", T.Text), new("category_name", "Categoría", T.Text), .. ProfitColumns],
            ProfitSql("l.category_id", "rl.category_id", string.Empty, "c.category_path, c.category_name",
                "JOIN reporting.categories c ON c.category_id = x.k", "profit DESC")),

        new("PROFIT_BY_CASHIER", "Utilidad por cajero", Profit,
            "Venta neta sin impuestos, costo, utilidad y margen por cajero (las devoluciones restan al cajero de la venta original).",
            ReportingPermissions.ProfitView, DateRange, [new("cashier_name", "Cajero", T.Text), .. ProfitColumns],
            ProfitSql("l.cashier_id", "r.original_cashier_id", "JOIN reporting.returns r ON r.return_id = rl.return_id", "u.display_name AS cashier_name",
                "JOIN reporting.users u ON u.user_id = x.k", "profit DESC")),

        // ------------------------------------------------------------------------------------------------ Impuestos
        new("TAXES_SALES", "Impuestos generados en ventas", Taxes,
            "IVA e impuestos al consumo por tipo y tarifa de las ventas completadas (base y valor). Para impuestos de valor fijo la base son unidades.",
            ReportingPermissions.TaxesView, DateRange,
            [
                new("tax_kind", "Tipo", T.Text),
                new("tax_code", "Impuesto", T.Text),
                new("tax_name", "Nombre", T.Text),
                new("rate", "Tarifa %", T.Percent),
                new("fixed_amount", "Valor fijo", T.Money),
                new("base", "Base gravable", T.Money, Total: true),
                new("base_units", "Unidades (valor fijo)", T.Quantity, Total: true),
                new("amount", "Impuesto", T.Money, Total: true),
            ],
            """
            SELECT x.tax_kind, x.tax_code, max(x.tax_name) AS tax_name, x.rate, x.fixed_amount,
                   COALESCE(sum(x.tax_base) FILTER (WHERE x.rate IS NOT NULL), 0) AS base,
                   COALESCE(sum(x.tax_base) FILTER (WHERE x.rate IS NULL), 0) AS base_units, sum(x.amount) AS amount
            FROM reporting.sale_line_taxes x
            WHERE x.company_id = @company_id AND x.branch_id = @branch_id AND x.business_date BETWEEN @from AND @to
              AND x.sale_status = 'COMPLETED' AND x.line_status = 'ACTIVE'
            GROUP BY x.tax_kind, x.tax_code, x.rate, x.fixed_amount
            ORDER BY x.tax_kind, x.tax_code, x.rate
            """,
            "Las devoluciones no se restan aquí: el libro de ventas y la contabilidad las registran por separado."),

        new("TAXES_PURCHASES", "Impuestos de compras", Taxes,
            "IVA e impuestos de las compras contabilizadas por impuesto y tarifa; separa lo descontable de lo que va al costo.",
            ReportingPermissions.TaxesView, DateRange,
            [
                new("tax_code", "Impuesto", T.Text),
                new("is_vat", "Es IVA", T.Boolean),
                new("rate", "Tarifa %", T.Percent),
                new("base", "Base", T.Money, Total: true),
                new("deductible", "Descontable", T.Money, Total: true),
                new("non_deductible", "Mayor valor del costo", T.Money, Total: true),
                new("amount", "Total impuesto", T.Money, Total: true),
            ],
            """
            SELECT t.tax_code, t.is_vat, t.rate, sum(t.base) AS base,
                   COALESCE(sum(t.amount) FILTER (WHERE t.is_deductible), 0) AS deductible,
                   COALESCE(sum(t.amount) FILTER (WHERE NOT t.is_deductible), 0) AS non_deductible, sum(t.amount) AS amount
            FROM reporting.purchase_line_taxes t
            WHERE t.company_id = @company_id AND t.branch_id = @branch_id AND t.business_date BETWEEN @from AND @to AND t.status = 'POSTED'
            GROUP BY t.tax_code, t.is_vat, t.rate
            ORDER BY t.tax_code, t.rate
            """),

        new("SALES_BOOK", "Libro de ventas diario", Taxes,
            "Por día y caja: consecutivo inicial y final, tiquetes, base por tarifa de IVA, excluidos/exentos, impuestos, total y medios de pago.",
            ReportingPermissions.TaxesView, DateRange,
            [
                new("business_date", "Fecha", T.Date),
                new("terminal", "Caja", T.Text),
                new("first_number", "Desde el tiquete", T.Text),
                new("last_number", "Hasta el tiquete", T.Text),
                new("tickets", "Tiquetes", T.Count, Total: true),
                new("excluded_exempt", "Excluidos y exentos", T.Money, Total: true),
                new("base_5", "Base IVA 5 %", T.Money, Total: true),
                new("vat_5", "IVA 5 %", T.Money, Total: true),
                new("base_19", "Base IVA 19 %", T.Money, Total: true),
                new("vat_19", "IVA 19 %", T.Money, Total: true),
                new("other_vat", "Otro IVA", T.Money, Total: true),
                new("consumption", "Impuesto al consumo", T.Money, Total: true),
                new("other_taxes", "Otros impuestos", T.Money, Total: true),
                new("rounding", "Redondeo", T.Money, Total: true),
                new("total", "Total", T.Money, Total: true),
                new("cash", "Efectivo", T.Money, Total: true),
                new("cards", "Tarjetas", T.Money, Total: true),
                new("other_methods", "Otros medios", T.Money, Total: true),
                new("voided", "Anuladas", T.Count, Total: true),
            ],
            $"""
            WITH s AS (
                SELECT s.* FROM reporting.sales s WHERE {SalesInRange} AND s.status IN ('COMPLETED', 'VOIDED')),
            vat AS (
                SELECT l.sale_id, l.sale_line_id, bool_or(x.tax_kind = 'VAT' AND COALESCE(x.rate, 0) > 0 AND NOT x.is_exempt) AS taxed
                FROM reporting.sale_lines l
                LEFT JOIN reporting.sale_line_taxes x ON x.sale_line_id = l.sale_line_id
                WHERE {LinesSold}
                GROUP BY l.sale_id, l.sale_line_id),
            lines AS (
                SELECT l.sale_id, sum(l.tax_base) FILTER (WHERE NOT COALESCE(v.taxed, false)) AS excluded_exempt
                FROM reporting.sale_lines l JOIN vat v ON v.sale_line_id = l.sale_line_id
                GROUP BY l.sale_id),
            taxes AS (
                SELECT x.sale_id,
                       sum(x.tax_base) FILTER (WHERE x.tax_kind = 'VAT' AND x.rate = 5) AS base_5,
                       sum(x.amount) FILTER (WHERE x.tax_kind = 'VAT' AND x.rate = 5) AS vat_5,
                       sum(x.tax_base) FILTER (WHERE x.tax_kind = 'VAT' AND x.rate = 19) AS base_19,
                       sum(x.amount) FILTER (WHERE x.tax_kind = 'VAT' AND x.rate = 19) AS vat_19,
                       sum(x.amount) FILTER (WHERE x.tax_kind = 'VAT' AND x.rate NOT IN (5, 19)) AS other_vat,
                       sum(x.amount) FILTER (WHERE x.tax_kind = 'CONSUMPTION') AS consumption,
                       sum(x.amount) FILTER (WHERE x.tax_kind NOT IN ('VAT', 'CONSUMPTION')) AS other_taxes
                FROM reporting.sale_line_taxes x
                WHERE x.company_id = @company_id AND x.branch_id = @branch_id AND x.business_date BETWEEN @from AND @to
                  AND x.sale_status = 'COMPLETED' AND x.line_status = 'ACTIVE'
                GROUP BY x.sale_id),
            pay AS (
                SELECT p.sale_id,
                       sum(p.applied) FILTER (WHERE p.method_kind = 'CASH') AS cash,
                       sum(p.applied) FILTER (WHERE p.method_kind IN ('DEBIT_CARD', 'CREDIT_CARD')) AS cards,
                       sum(p.applied) FILTER (WHERE p.method_kind NOT IN ('CASH', 'DEBIT_CARD', 'CREDIT_CARD')) AS other_methods
                FROM reporting.payments p
                WHERE p.company_id = @company_id AND p.branch_id = @branch_id AND p.business_date BETWEEN @from AND @to AND p.sale_status = 'COMPLETED'
                GROUP BY p.sale_id)
            SELECT s.business_date, s.terminal_code || ' · ' || s.terminal_name AS terminal, min(s.number) AS first_number, max(s.number) AS last_number,
                   count(*) FILTER (WHERE s.status = 'COMPLETED') AS tickets, COALESCE(sum(lines.excluded_exempt), 0) AS excluded_exempt,
                   COALESCE(sum(taxes.base_5), 0) AS base_5, COALESCE(sum(taxes.vat_5), 0) AS vat_5, COALESCE(sum(taxes.base_19), 0) AS base_19,
                   COALESCE(sum(taxes.vat_19), 0) AS vat_19, COALESCE(sum(taxes.other_vat), 0) AS other_vat,
                   COALESCE(sum(taxes.consumption), 0) AS consumption, COALESCE(sum(taxes.other_taxes), 0) AS other_taxes,
                   COALESCE(sum(s.rounding_adjustment) FILTER (WHERE s.status = 'COMPLETED'), 0) AS rounding,
                   COALESCE(sum(s.total) FILTER (WHERE s.status = 'COMPLETED'), 0) AS total, COALESCE(sum(pay.cash), 0) AS cash,
                   COALESCE(sum(pay.cards), 0) AS cards, COALESCE(sum(pay.other_methods), 0) AS other_methods,
                   count(*) FILTER (WHERE s.status = 'VOIDED') AS voided
            FROM s
            LEFT JOIN lines ON lines.sale_id = s.sale_id
            LEFT JOIN taxes ON taxes.sale_id = s.sale_id
            LEFT JOIN pay ON pay.sale_id = s.sale_id
            GROUP BY s.business_date, s.terminal_code, s.terminal_name
            ORDER BY s.business_date, s.terminal_code
            """,
            "Formato estándar a validar con el contador. El rango de consecutivos incluye las anuladas (se cuentan aparte)."),

        new("FISCAL_RECONCILIATION", "Conciliación de facturación electrónica", Taxes,
            "Por día y sucursal (Fase 11-B): ventas contra facturas aceptadas por la DIAN, pendientes, en contingencia, rechazadas, sin documento, "
            + "canceladas y notas crédito, con el valor sin factura aceptada y la diferencia de valores.",
            ReportingPermissions.TaxesView, DateRange,
            [
                new("business_date", "Fecha", T.Date),
                new("branch_name", "Sucursal", T.Text),
                new("sales", "Ventas", T.Count, Total: true),
                new("sales_total", "Valor vendido", T.Money, Total: true),
                new("internal_receipts", "Con comprobante interno", T.Count, Total: true),
                new("accepted", "Facturas aceptadas", T.Count, Total: true),
                new("accepted_total", "Valor facturado aceptado", T.Money, Total: true),
                new("pending", "Pendientes", T.Count, Total: true),
                new("contingency", "En contingencia", T.Count, Total: true),
                new("rejected", "Rechazadas", T.Count, Total: true),
                new("without_document", "Sin documento", T.Count, Total: true),
                new("unreconciled", "Ventas sin factura aceptada", T.Count, Total: true),
                new("unreconciled_total", "Valor sin factura aceptada", T.Money, Total: true),
                new("amount_difference", "Diferencia venta − factura", T.Money, Total: true),
                new("voided", "Anuladas", T.Count, Total: true),
                new("cancelled", "Facturas canceladas antes de enviar", T.Count, Total: true),
                new("credit_notes", "Notas crédito aceptadas", T.Count, Total: true),
                new("credit_notes_open", "Notas crédito por aceptar", T.Count, Total: true),
            ],
            $"""
            WITH x AS (
                SELECT s.business_date, s.branch_id, s.status, s.total, f.document_type, f.status AS fiscal_status, f.total AS fiscal_total,
                       (s.status = 'COMPLETED' AND f.document_type IS DISTINCT FROM 'INTERNAL_RECEIPT' AND f.status IS DISTINCT FROM 'ACCEPTED') AS open_sale
                FROM reporting.sales s
                LEFT JOIN reporting.fiscal_documents f ON f.source = 'SALE' AND f.source_id = s.sale_id
                WHERE {SalesInRange} AND s.status IN ('COMPLETED', 'VOIDED')),
            d AS (
                SELECT x.business_date, x.branch_id,
                       count(*) FILTER (WHERE x.status = 'COMPLETED') AS sales,
                       COALESCE(sum(x.total) FILTER (WHERE x.status = 'COMPLETED'), 0) AS sales_total,
                       count(*) FILTER (WHERE x.status = 'COMPLETED' AND x.document_type = 'INTERNAL_RECEIPT') AS internal_receipts,
                       count(*) FILTER (WHERE x.status = 'COMPLETED' AND x.fiscal_status = 'ACCEPTED') AS accepted,
                       COALESCE(sum(x.fiscal_total) FILTER (WHERE x.status = 'COMPLETED' AND x.fiscal_status = 'ACCEPTED'), 0) AS accepted_total,
                       count(*) FILTER (WHERE x.status = 'COMPLETED' AND x.fiscal_status IN ('PENDING', 'SUBMITTING', 'ERROR')) AS pending,
                       count(*) FILTER (WHERE x.status = 'COMPLETED' AND x.fiscal_status = 'CONTINGENCY') AS contingency,
                       count(*) FILTER (WHERE x.status = 'COMPLETED' AND x.fiscal_status = 'REJECTED') AS rejected,
                       count(*) FILTER (WHERE x.status = 'COMPLETED' AND x.document_type IS NULL) AS without_document,
                       count(*) FILTER (WHERE x.open_sale) AS unreconciled,
                       COALESCE(sum(x.total) FILTER (WHERE x.open_sale), 0) AS unreconciled_total,
                       COALESCE(sum(x.total - x.fiscal_total) FILTER (WHERE x.status = 'COMPLETED' AND x.fiscal_status = 'ACCEPTED'), 0) AS amount_difference,
                       count(*) FILTER (WHERE x.status = 'VOIDED') AS voided,
                       count(*) FILTER (WHERE x.fiscal_status = 'CANCELLED') AS cancelled
                FROM x
                GROUP BY x.business_date, x.branch_id),
            n AS (
                SELECT f.business_date, f.branch_id, count(*) FILTER (WHERE f.status = 'ACCEPTED') AS credit_notes,
                       count(*) FILTER (WHERE f.status <> 'ACCEPTED') AS credit_notes_open
                FROM reporting.fiscal_documents f
                WHERE f.company_id = @company_id AND f.branch_id = @branch_id AND f.business_date BETWEEN @from AND @to
                  AND f.document_type = 'CREDIT_NOTE'
                GROUP BY f.business_date, f.branch_id)
            SELECT business_date, b.branch_name, COALESCE(d.sales, 0) AS sales, COALESCE(d.sales_total, 0) AS sales_total,
                   COALESCE(d.internal_receipts, 0) AS internal_receipts, COALESCE(d.accepted, 0) AS accepted,
                   COALESCE(d.accepted_total, 0) AS accepted_total, COALESCE(d.pending, 0) AS pending, COALESCE(d.contingency, 0) AS contingency,
                   COALESCE(d.rejected, 0) AS rejected, COALESCE(d.without_document, 0) AS without_document,
                   COALESCE(d.unreconciled, 0) AS unreconciled, COALESCE(d.unreconciled_total, 0) AS unreconciled_total,
                   COALESCE(d.amount_difference, 0) AS amount_difference, COALESCE(d.voided, 0) AS voided, COALESCE(d.cancelled, 0) AS cancelled,
                   COALESCE(n.credit_notes, 0) AS credit_notes, COALESCE(n.credit_notes_open, 0) AS credit_notes_open
            FROM d FULL JOIN n USING (business_date, branch_id)
            JOIN reporting.branches b ON b.branch_id = COALESCE(d.branch_id, n.branch_id)
            ORDER BY business_date, b.branch_name
            """,
            "Con la facturación apagada todas las ventas llevan comprobante interno. Una venta queda conciliada con su factura aceptada; "
            + "la diferencia de valores debe ser cero (redondeos a revisar con el contador)."),

        // ------------------------------------------------------------------------------------------------ Inventario
        new("INVENTORY_VALUATION", "Inventario valorizado", Inventory,
            "Existencias y valor por bodega y producto, hoy (saldos en línea) o a una fecha (saldo del último movimiento del kardex).",
            ReportingPermissions.InventoryView, [AsOf, Warehouse],
            [
                new("warehouse_name", "Bodega", T.Text),
                new("sku", "SKU", T.Text),
                new("product_name", "Producto", T.Text),
                new("category_name", "Categoría", T.Text),
                new("base_unit_code", "Unidad", T.Text),
                new("quantity", "Existencia", T.Quantity, Total: true),
                new("average_cost", "Costo promedio", T.Money, IsCost: true),
                new("total_value", "Valor", T.Money, Total: true, IsCost: true),
            ],
            $"""
            WITH cur AS (
                SELECT b.warehouse_id, max(b.warehouse_name) AS warehouse_name, b.product_id, sum(b.quantity) AS quantity, sum(b.total_value) AS total_value
                FROM reporting.stock_balances b
                WHERE @as_of IS NULL AND {N("b")} AND (@warehouse_id IS NULL OR b.warehouse_id = @warehouse_id)
                GROUP BY b.warehouse_id, b.product_id),
            hist AS (
                SELECT DISTINCT ON (m.warehouse_id, m.product_id) m.warehouse_id, m.warehouse_name, m.product_id,
                       m.balance_quantity AS quantity, m.balance_value AS total_value
                FROM reporting.stock_movements m
                WHERE @as_of IS NOT NULL AND {N("m")} AND m.business_date <= @as_of AND (@warehouse_id IS NULL OR m.warehouse_id = @warehouse_id)
                ORDER BY m.warehouse_id, m.product_id, m.seq DESC),
            b AS (SELECT * FROM cur UNION ALL SELECT * FROM hist)
            SELECT b.warehouse_name, p.sku, p.product_name, p.category_name, p.base_unit_code, b.quantity,
                   round(b.total_value / NULLIF(b.quantity, 0), 2) AS average_cost, round(b.total_value, 2) AS total_value
            FROM b JOIN reporting.products p ON p.product_id = b.product_id
            WHERE b.quantity <> 0 OR b.total_value <> 0
            ORDER BY b.warehouse_name, p.category_name, p.product_name
            """,
            "Cuadra con el kardex: el valor a una fecha es el saldo que dejó el último movimiento de esa fecha o anterior."),

        new("BELOW_MINIMUM", "Productos bajo mínimo", Inventory,
            "Productos con existencia en o por debajo del punto de pedido, con el sugerido de compra y el proveedor preferido.",
            ReportingPermissions.InventoryView, [Warehouse],
            [
                new("warehouse_name", "Bodega", T.Text),
                new("sku", "SKU", T.Text),
                new("product_name", "Producto", T.Text),
                new("quantity", "Existencia", T.Quantity),
                new("min_qty", "Mínimo", T.Quantity),
                new("reorder_point", "Punto de pedido", T.Quantity),
                new("max_qty", "Máximo", T.Quantity),
                new("pending", "Pedido pendiente", T.Quantity),
                new("suggested", "Sugerido de compra", T.Quantity, Total: true),
                new("supplier_name", "Proveedor preferido", T.Text),
                new("last_cost", "Último costo", T.Money, IsCost: true),
                new("suggested_value", "Valor sugerido", T.Money, Total: true, IsCost: true),
            ],
            $"""
            WITH bal AS (
                SELECT b.warehouse_id, b.product_id, sum(b.quantity) AS quantity
                FROM reporting.stock_balances b WHERE {N("b")} GROUP BY b.warehouse_id, b.product_id),
            x AS (
                SELECT sp.warehouse_id, sp.product_id, COALESCE(bal.quantity, 0) AS quantity, sp.min_qty, sp.reorder_point, sp.max_qty, sp.reorder_qty,
                       COALESCE(po.pending_quantity, 0) AS pending
                FROM reporting.stock_policies sp
                LEFT JOIN bal ON bal.warehouse_id = sp.warehouse_id AND bal.product_id = sp.product_id
                LEFT JOIN reporting.pending_orders po ON po.warehouse_id = sp.warehouse_id AND po.product_id = sp.product_id
                WHERE {N("sp")} AND (@warehouse_id IS NULL OR sp.warehouse_id = @warehouse_id)
                  AND COALESCE(bal.quantity, 0) <= COALESCE(sp.reorder_point, sp.min_qty)),
            s AS (
                SELECT x.*, GREATEST(CASE WHEN x.max_qty IS NOT NULL THEN x.max_qty - (x.quantity + x.pending)
                                          WHEN x.reorder_qty IS NOT NULL THEN x.reorder_qty - x.pending
                                          ELSE x.min_qty - (x.quantity + x.pending) END, 0) AS suggested
                FROM x)
            SELECT w.warehouse_name, p.sku, p.product_name, s.quantity, s.min_qty, s.reorder_point, s.max_qty, s.pending, s.suggested,
                   sup.supplier_name, sup.last_cost, round(s.suggested * sup.last_cost, 2) AS suggested_value
            FROM s
            JOIN reporting.products p ON p.product_id = s.product_id
            JOIN (SELECT DISTINCT sb.warehouse_id, sb.warehouse_name FROM reporting.stock_balances sb WHERE {N("sb")}
                  UNION SELECT DISTINCT sm.warehouse_id, sm.warehouse_name FROM reporting.stock_movements sm WHERE {N("sm")}) w ON w.warehouse_id = s.warehouse_id
            LEFT JOIN LATERAL (
                SELECT spr.supplier_name, spr.last_cost FROM reporting.supplier_products spr
                WHERE spr.product_id = s.product_id AND spr.company_id = @company_id
                ORDER BY spr.is_preferred DESC, spr.last_cost NULLS LAST LIMIT 1) sup ON true
            ORDER BY w.warehouse_name, p.product_name
            """,
            "Sugerido = máximo − (existencia + pedido pendiente); sin máximo, la cantidad de pedido o hasta el mínimo."),

        new("NO_ROTATION", "Productos sin rotación", Inventory, "Productos con existencia y sin ventas en los últimos N días, con el valor inmovilizado.",
            ReportingPermissions.InventoryView, [Days("Días sin ventas", 30)],
            [
                new("sku", "SKU", T.Text),
                new("product_name", "Producto", T.Text),
                new("category_name", "Categoría", T.Text),
                new("quantity", "Existencia", T.Quantity, Total: true),
                new("total_value", "Valor inmovilizado", T.Money, Total: true, IsCost: true),
                new("last_sale", "Última venta", T.Date),
                new("days_without_sale", "Días sin venta", T.Count),
            ],
            $"""
            WITH bal AS (
                SELECT b.product_id, sum(b.quantity) AS quantity, sum(b.total_value) AS total_value
                FROM reporting.stock_balances b WHERE {N("b")}
                GROUP BY b.product_id HAVING sum(b.quantity) > 0),
            ls AS (
                SELECT m.product_id, max(m.business_date) AS last_sale
                FROM reporting.stock_movements m WHERE {N("m")} AND m.movement_type = 'SALE'
                GROUP BY m.product_id)
            SELECT p.sku, p.product_name, p.category_name, bal.quantity, round(bal.total_value, 2) AS total_value, ls.last_sale,
                   @today - ls.last_sale AS days_without_sale
            FROM bal
            JOIN reporting.products p ON p.product_id = bal.product_id
            LEFT JOIN ls ON ls.product_id = bal.product_id
            WHERE ls.last_sale IS NULL OR ls.last_sale < @today - @days
            ORDER BY bal.total_value DESC
            """),

        new("EXPIRING_LOTS", "Lotes por vencer y vencidos", Inventory, "Lotes con existencia que vencen en los próximos N días o ya vencieron, con su valor.",
            ReportingPermissions.InventoryView, [Days("Días hacia adelante", 30)],
            [
                new("warehouse_name", "Bodega", T.Text),
                new("sku", "SKU", T.Text),
                new("product_name", "Producto", T.Text),
                new("lot_number", "Lote", T.Text),
                new("expiry_date", "Vence", T.Date),
                new("days_left", "Días", T.Count),
                new("status", "Estado", T.Text),
                new("quantity", "Existencia", T.Quantity, Total: true),
                new("total_value", "Valor", T.Money, Total: true, IsCost: true),
            ],
            $"""
            SELECT b.warehouse_name, p.sku, p.product_name, lo.lot_number, lo.expiry_date, lo.expiry_date - @today AS days_left,
                   CASE WHEN lo.expiry_date < @today THEN 'VENCIDO' ELSE 'POR VENCER' END AS status, b.quantity, round(b.total_value, 2) AS total_value
            FROM reporting.stock_balances b
            JOIN reporting.lots lo ON lo.lot_id = b.lot_id
            JOIN reporting.products p ON p.product_id = b.product_id
            WHERE {N("b")} AND b.quantity > 0 AND lo.expiry_date IS NOT NULL AND lo.expiry_date <= @today + @days
            ORDER BY lo.expiry_date, p.product_name
            """),

        new("ADJUSTMENTS", "Ajustes de inventario por motivo", Inventory,
            "Ajustes contabilizados por motivo (incluidos los rápidos de la caja) con su valor.",
            ReportingPermissions.InventoryView, DateRange,
            [
                new("reason_name", "Motivo", T.Text),
                new("reason_kind", "Clase", T.Text),
                new("adjustments", "Ajustes", T.Count, Total: true),
                new("quick", "Desde la caja", T.Count, Total: true),
                new("lines", "Líneas", T.Count, Total: true),
                new("total_value", "Valor", T.Money, Total: true, IsCost: true),
            ],
            $"""
            SELECT a.reason_name, a.reason_kind, count(*) AS adjustments, count(*) FILTER (WHERE a.is_quick) AS quick, sum(a.line_count) AS lines,
                   round(sum(a.total_value), 2) AS total_value
            FROM reporting.inventory_adjustments a
            WHERE {N("a")} AND a.business_date BETWEEN @from AND @to AND a.status = 'POSTED'
            GROUP BY a.reason_name, a.reason_kind
            ORDER BY total_value
            """),

        // ------------------------------------------------------------------------------------------------ Compras y gastos
        new("PURCHASES_BY_SUPPLIER", "Compras por proveedor", Purchases, "Compras contabilizadas y devoluciones por proveedor en el período.",
            ReportingPermissions.PurchasesView, DateRange,
            [
                new("supplier_name", "Proveedor", T.Text),
                new("supplier_identification", "NIT / identificación", T.Text),
                new("purchases", "Compras", T.Count, Total: true),
                new("subtotal", "Subtotal", T.Money, Total: true),
                new("discounts", "Descuentos", T.Money, Total: true),
                new("taxes", "Impuestos", T.Money, Total: true),
                new("withholdings", "Retenciones", T.Money, Total: true),
                new("total", "Total", T.Money, Total: true),
                new("returns", "Devoluciones", T.Money, Total: true),
                new("net", "Neto", T.Money, Total: true),
            ],
            $"""
            WITH p AS (
                SELECT p.supplier_id, max(p.supplier_name) AS supplier_name, max(p.supplier_identification) AS supplier_identification, count(*) AS purchases,
                       sum(p.subtotal) AS subtotal, sum(p.discount_total) AS discounts, sum(p.tax_total) AS taxes, sum(p.withholding_total) AS withholdings,
                       sum(p.total) AS total
                FROM reporting.purchases p
                WHERE {N("p")} AND p.business_date BETWEEN @from AND @to AND p.status = 'POSTED'
                GROUP BY p.supplier_id),
            r AS (
                SELECT r.supplier_id, max(r.supplier_name) AS supplier_name, sum(r.total) AS returns
                FROM reporting.supplier_returns r
                WHERE {N("r")} AND r.business_date BETWEEN @from AND @to AND r.status IN ('POSTED', 'SETTLED')
                GROUP BY r.supplier_id)
            SELECT COALESCE(p.supplier_name, r.supplier_name) AS supplier_name, p.supplier_identification, COALESCE(p.purchases, 0) AS purchases,
                   COALESCE(p.subtotal, 0) AS subtotal, COALESCE(p.discounts, 0) AS discounts, COALESCE(p.taxes, 0) AS taxes,
                   COALESCE(p.withholdings, 0) AS withholdings, COALESCE(p.total, 0) AS total, COALESCE(r.returns, 0) AS returns,
                   COALESCE(p.total, 0) - COALESCE(r.returns, 0) AS net
            FROM p FULL JOIN r ON r.supplier_id = p.supplier_id
            ORDER BY net DESC
            """),

        new("SUPPLIER_RETURNS", "Devoluciones a proveedores", Purchases, "Devoluciones contabilizadas o liquidadas en el período.",
            ReportingPermissions.PurchasesView, DateRange,
            [
                new("business_date", "Fecha", T.Date),
                new("number", "Número", T.Text),
                new("supplier_name", "Proveedor", T.Text),
                new("reason", "Motivo", T.Text),
                new("status", "Estado", T.Text),
                new("settlement", "Liquidación", T.Text),
                new("total", "Total", T.Money, Total: true),
                new("credit_total", "Crédito", T.Money, Total: true),
            ],
            $"""
            SELECT r.business_date, r.number, r.supplier_name, r.reason, r.status, r.settlement, r.total, r.credit_total
            FROM reporting.supplier_returns r
            WHERE {N("r")} AND r.business_date BETWEEN @from AND @to AND r.status IN ('POSTED', 'SETTLED')
            ORDER BY r.business_date, r.number
            """),

        new("PAYABLES_AGING", "Edades de cartera por pagar", Purchases, "Saldo por proveedor por vencimiento: al día, 1–30, 31–60, 61–90 y más de 90 días.",
            ReportingPermissions.PurchasesView, [],
            [
                new("supplier_name", "Proveedor", T.Text),
                new("documents", "Documentos", T.Count, Total: true),
                new("not_due", "Al día", T.Money, Total: true),
                new("days_1_30", "1–30", T.Money, Total: true),
                new("days_31_60", "31–60", T.Money, Total: true),
                new("days_61_90", "61–90", T.Money, Total: true),
                new("days_over_90", "Más de 90", T.Money, Total: true),
                new("balance", "Saldo", T.Money, Total: true),
            ],
            $"""
            SELECT a.supplier_name, count(*) AS documents,
                   COALESCE(sum(a.balance) FILTER (WHERE a.due_date >= @today), 0) AS not_due,
                   COALESCE(sum(a.balance) FILTER (WHERE @today - a.due_date BETWEEN 1 AND 30), 0) AS days_1_30,
                   COALESCE(sum(a.balance) FILTER (WHERE @today - a.due_date BETWEEN 31 AND 60), 0) AS days_31_60,
                   COALESCE(sum(a.balance) FILTER (WHERE @today - a.due_date BETWEEN 61 AND 90), 0) AS days_61_90,
                   COALESCE(sum(a.balance) FILTER (WHERE @today - a.due_date > 90), 0) AS days_over_90,
                   sum(a.balance) AS balance
            FROM reporting.payables a
            WHERE {N("a")} AND a.status = 'OPEN' AND a.balance > 0
            GROUP BY a.supplier_id, a.supplier_name
            ORDER BY balance DESC
            """),

        new("EXPENSES_BY_CATEGORY", "Gastos por categoría", Purchases, "Gastos registrados (no anulados) por categoría en el período.",
            ReportingPermissions.PurchasesView, DateRange,
            [
                new("parent_name", "Grupo", T.Text),
                new("category_name", "Categoría", T.Text),
                new("expenses", "Gastos", T.Count, Total: true),
                new("amount", "Valor", T.Money, Total: true),
                new("tax_amount", "Impuestos", T.Money, Total: true),
                new("from_cash", "Desde la caja", T.Money, Total: true),
            ],
            $"""
            SELECT COALESCE(e.category_parent_name, e.category_name) AS parent_name, e.category_name, count(*) AS expenses, sum(e.amount) AS amount,
                   sum(e.tax_amount) AS tax_amount, COALESCE(sum(e.amount) FILTER (WHERE e.cash_session_id IS NOT NULL), 0) AS from_cash
            FROM reporting.expenses e
            WHERE {N("e")} AND e.business_date BETWEEN @from AND @to AND e.status = 'POSTED'
            GROUP BY 1, 2
            ORDER BY 1, amount DESC
            """),

        // ------------------------------------------------------------------------------------------------ Caja
        new("CASH_CLOSINGS", "Cierres de caja", Cash, "Jornadas cerradas por medio de pago: esperado, contado y diferencia; revisadas o no.",
            ReportingPermissions.CashView, DateRange,
            [
                new("business_date", "Fecha", T.Date),
                new("session_number", "Jornada", T.Text),
                new("terminal", "Caja", T.Text),
                new("cashier_name", "Cajero", T.Text),
                new("method_name", "Medio", T.Text),
                new("transactions", "Transacciones", T.Count, Total: true),
                new("expected", "Esperado", T.Money, Total: true),
                new("counted", "Contado", T.Money, Total: true),
                new("difference", "Diferencia", T.Money, Total: true),
                new("review_required", "Requiere revisión", T.Boolean),
                new("reviewed", "Revisada", T.Boolean),
            ],
            $"""
            SELECT cs.business_date, cs.number AS session_number, cs.terminal_code || ' · ' || cs.terminal_name AS terminal, cs.cashier_name,
                   t.method_name, t.transactions, t.expected, t.counted, t.difference, cs.review_required, cs.reviewed_at IS NOT NULL AS reviewed
            FROM reporting.cash_sessions cs
            JOIN reporting.cash_session_totals t ON t.session_id = cs.session_id
            WHERE {N("cs")} AND cs.business_date BETWEEN @from AND @to AND cs.status = 'CLOSED'
            ORDER BY cs.business_date, cs.number, t.method_name
            """),

        new("CASH_WITHDRAWALS", "Retiros, ingresos y aperturas sin venta", Cash,
            "Movimientos manuales de la caja con quién los hizo y quién autorizó.",
            ReportingPermissions.CashView, DateRange,
            [
                new("occurred_at", "Fecha y hora", T.DateTime),
                new("session_number", "Jornada", T.Text),
                new("terminal_code", "Caja", T.Text),
                new("movement_type", "Tipo", T.Text),
                new("method_name", "Medio", T.Text),
                new("amount", "Valor", T.Money, Total: true),
                new("reason", "Motivo", T.Text),
                new("user_name", "Usuario", T.Text),
                new("authorized_by_name", "Autorizó", T.Text),
            ],
            $"""
            SELECT m.occurred_at, m.session_number, m.terminal_code,
                   CASE m.movement_type WHEN 'CASH_OUT_WITHDRAWAL' THEN 'RETIRO' WHEN 'CASH_IN' THEN 'INGRESO' WHEN 'NO_SALE_DRAWER_OPEN' THEN 'APERTURA SIN VENTA'
                        ELSE 'CORRECCIÓN' END AS movement_type,
                   m.method_name, m.amount * m.direction AS amount, m.reason, m.user_name, m.authorized_by_name
            FROM reporting.cash_movements m
            WHERE {N("m")} AND m.business_date BETWEEN @from AND @to
              AND m.movement_type IN ('CASH_OUT_WITHDRAWAL', 'CASH_IN', 'NO_SALE_DRAWER_OPEN', 'CORRECTION')
            ORDER BY m.occurred_at
            """,
            "Los retiros y correcciones de salida se muestran en negativo."),

        // ------------------------------------------------------------------------------------------------ Antifraude
        new("ANTIFRAUD_BY_CASHIER", "Antifraude por cajero", Antifraud,
            "Indicadores por cajero (conteo y valor) y eventos por cada 100 tiquetes comparados con el promedio de la tienda; se resaltan los que superan el umbral.",
            ReportingPermissions.AntifraudView, DateRange,
            [
                new("cashier_name", "Cajero", T.Text),
                new("tickets", "Tiquetes", T.Count, Total: true),
                new("line_voids", "Líneas eliminadas", T.Count, Total: true),
                new("line_void_value", "Valor eliminado", T.Money, Total: true),
                new("cancels", "Ventas canceladas", T.Count, Total: true),
                new("cancel_value", "Valor cancelado", T.Money, Total: true),
                new("voids", "Anulaciones", T.Count, Total: true),
                new("void_value", "Valor anulado", T.Money, Total: true),
                new("discounts", "Descuentos", T.Count, Total: true),
                new("discount_value", "Valor descuentos", T.Money, Total: true),
                new("price_overrides", "Precios cambiados", T.Count, Total: true),
                new("price_override_value", "Valor precios", T.Money, Total: true),
                new("no_sale_opens", "Cajón sin venta", T.Count, Total: true),
                new("expired_sales", "Vencidos vendidos", T.Count, Total: true),
                new("quick_adjustments", "Ajustes rápidos", T.Count, Total: true),
                new("quick_adjustment_value", "Valor ajustes", T.Money, Total: true),
                new("cash_differences", "Cierres con diferencia", T.Count, Total: true),
                new("cash_difference_value", "Valor diferencias", T.Money, Total: true),
                new("events", "Eventos", T.Count, Total: true),
                new("events_per_100", "Eventos por 100 tiquetes", T.Percent, Ratio: new RatioTotal("events", "tickets", 100m)),
                new("store_events_per_100", "Promedio de la tienda", T.Percent),
                new("flagged", "Fuera de lo normal", T.Boolean),
            ],
            $"""
            WITH t AS (
                SELECT s.cashier_id, count(*) AS tickets FROM reporting.sales s
                WHERE {SalesInRange} AND s.status IN ('COMPLETED', 'VOIDED') GROUP BY s.cashier_id),
            e AS (
                SELECT e.cashier_id,
                       count(*) FILTER (WHERE e.event_type = 'LINE_VOID') AS line_voids,
                       COALESCE(sum(e.amount) FILTER (WHERE e.event_type = 'LINE_VOID'), 0) AS line_void_value,
                       count(*) FILTER (WHERE e.event_type = 'SALE_CANCEL') AS cancels,
                       COALESCE(sum(e.amount) FILTER (WHERE e.event_type = 'SALE_CANCEL'), 0) AS cancel_value,
                       count(*) FILTER (WHERE e.event_type = 'SALE_VOID') AS voids,
                       COALESCE(sum(e.amount) FILTER (WHERE e.event_type = 'SALE_VOID'), 0) AS void_value,
                       count(*) FILTER (WHERE e.event_type = 'MANUAL_DISCOUNT') AS discounts,
                       COALESCE(sum(e.amount) FILTER (WHERE e.event_type = 'MANUAL_DISCOUNT'), 0) AS discount_value,
                       count(*) FILTER (WHERE e.event_type = 'PRICE_OVERRIDE') AS price_overrides,
                       COALESCE(sum(e.amount) FILTER (WHERE e.event_type = 'PRICE_OVERRIDE'), 0) AS price_override_value,
                       count(*) FILTER (WHERE e.event_type = 'NO_SALE_DRAWER') AS no_sale_opens,
                       count(*) FILTER (WHERE e.event_type = 'EXPIRED_LOT_SALE') AS expired_sales,
                       count(*) FILTER (WHERE e.event_type = 'QUICK_ADJUSTMENT') AS quick_adjustments,
                       COALESCE(sum(e.amount) FILTER (WHERE e.event_type = 'QUICK_ADJUSTMENT'), 0) AS quick_adjustment_value,
                       count(*) FILTER (WHERE e.event_type = 'CASH_DIFFERENCE') AS cash_differences,
                       COALESCE(sum(e.amount) FILTER (WHERE e.event_type = 'CASH_DIFFERENCE'), 0) AS cash_difference_value,
                       count(*) AS events
                FROM reporting.antifraud_events e
                WHERE {N("e")} AND e.business_date BETWEEN @from AND @to
                GROUP BY e.cashier_id),
            c AS (
                SELECT COALESCE(t.cashier_id, e.cashier_id) AS cashier_id, COALESCE(t.tickets, 0) AS tickets,
                       COALESCE(e.line_voids, 0) AS line_voids, COALESCE(e.line_void_value, 0) AS line_void_value,
                       COALESCE(e.cancels, 0) AS cancels, COALESCE(e.cancel_value, 0) AS cancel_value, COALESCE(e.voids, 0) AS voids,
                       COALESCE(e.void_value, 0) AS void_value, COALESCE(e.discounts, 0) AS discounts, COALESCE(e.discount_value, 0) AS discount_value,
                       COALESCE(e.price_overrides, 0) AS price_overrides, COALESCE(e.price_override_value, 0) AS price_override_value,
                       COALESCE(e.no_sale_opens, 0) AS no_sale_opens, COALESCE(e.expired_sales, 0) AS expired_sales,
                       COALESCE(e.quick_adjustments, 0) AS quick_adjustments, COALESCE(e.quick_adjustment_value, 0) AS quick_adjustment_value,
                       COALESCE(e.cash_differences, 0) AS cash_differences, COALESCE(e.cash_difference_value, 0) AS cash_difference_value,
                       COALESCE(e.events, 0) AS events
                FROM t FULL JOIN e ON e.cashier_id = t.cashier_id),
            store AS (SELECT sum(c.events)::numeric / NULLIF(sum(c.tickets), 0) AS rate FROM c)
            SELECT u.display_name AS cashier_name, c.tickets, c.line_voids, c.line_void_value, c.cancels, c.cancel_value, c.voids, c.void_value,
                   c.discounts, c.discount_value, c.price_overrides, c.price_override_value, c.no_sale_opens, c.expired_sales, c.quick_adjustments,
                   c.quick_adjustment_value, c.cash_differences, c.cash_difference_value, c.events,
                   round(100.0 * c.events / NULLIF(c.tickets, 0), 2) AS events_per_100, round(100 * store.rate, 2) AS store_events_per_100,
                   COALESCE(c.events > 0 AND (c.tickets = 0 OR c.events::numeric / c.tickets > store.rate * @threshold_factor), false) AS flagged
            FROM c CROSS JOIN store
            JOIN reporting.users u ON u.user_id = c.cashier_id
            ORDER BY flagged DESC, events_per_100 DESC NULLS FIRST
            """,
            "Un cajero se resalta cuando sus eventos por tiquete superan el promedio de la tienda por el factor configurado (reporting.antifraud_threshold_factor)."),

        new("ANTIFRAUD_EVENTS", "Eventos antifraude", Antifraud, "Detalle de cada evento sensible con su venta, caja, cajero y quién autorizó.",
            ReportingPermissions.AntifraudView, [From, To, Cashier],
            [
                new("occurred_at", "Fecha y hora", T.DateTime),
                new("business_date", "Jornada", T.Date),
                new("event_type", "Evento", T.Text),
                new("cashier_name", "Cajero", T.Text),
                new("sale_number", "Documento", T.Text),
                new("detail", "Detalle", T.Text),
                new("amount", "Valor", T.Money, Total: true),
                new("authorized_by_name", "Autorizó", T.Text),
            ],
            $"""
            SELECT e.occurred_at, e.business_date,
                   CASE e.event_type WHEN 'LINE_VOID' THEN 'LÍNEA ELIMINADA' WHEN 'SALE_CANCEL' THEN 'VENTA CANCELADA' WHEN 'SALE_VOID' THEN 'ANULACIÓN'
                        WHEN 'MANUAL_DISCOUNT' THEN 'DESCUENTO MANUAL' WHEN 'PRICE_OVERRIDE' THEN 'PRECIO CAMBIADO'
                        WHEN 'NO_SALE_DRAWER' THEN 'CAJÓN SIN VENTA' WHEN 'EXPIRED_LOT_SALE' THEN 'LOTE VENCIDO VENDIDO'
                        WHEN 'QUICK_ADJUSTMENT' THEN 'AJUSTE RÁPIDO' ELSE 'DIFERENCIA DE CAJA' END AS event_type,
                   u.display_name AS cashier_name, e.sale_number, e.detail, e.amount, a.display_name AS authorized_by_name
            FROM reporting.antifraud_events e
            JOIN reporting.users u ON u.user_id = e.cashier_id
            LEFT JOIN reporting.users a ON a.user_id = e.authorized_by
            WHERE {N("e")} AND e.business_date BETWEEN @from AND @to AND (@cashier_id IS NULL OR e.cashier_id = @cashier_id)
            ORDER BY e.occurred_at
            """),

        // ------------------------------------------------------------------------------------------------ Auditoría (Fase 10, audit.log.view)
        new("AUDIT_PRICE_COST_CHANGES", "Cambios de precios, costos e impuestos", AuditGroup,
            "Cada precio nuevo o modificado (antes, después y variación %), cambios del último costo de un proveedor e impuestos agregados o quitados a un producto, con quién y cuándo.",
            AuditPermissions.LogView, DateRange,
            [
                new("occurred_at", "Fecha y hora", T.DateTime),
                new("kind", "Tipo", T.Text),
                new("sku", "SKU", T.Text),
                new("product_name", "Producto", T.Text),
                new("detail", "Detalle", T.Text),
                new("before_value", "Antes", T.Money),
                new("after_value", "Después", T.Money),
                new("variation", "Variación %", T.Percent),
                new("user_name", "Usuario", T.Text),
            ],
            $"""
            WITH x AS (
                SELECT a.occurred_at, a.occurred_local, CASE a.action WHEN 'PRODUCT_PRICE_SCHEDULED' THEN 'PRECIO PROGRAMADO' ELSE 'PRECIO' END AS kind,
                       a.entity_id AS product_id, a.summary AS detail, (a.old_values ->> 'price')::numeric AS before_value,
                       (a.new_values ->> 'price')::numeric AS after_value, a.user_display_name
                FROM reporting.audit_log a
                WHERE {AuditCompany} AND a.action IN ('PRODUCT_PRICE_CHANGED', 'PRODUCT_PRICE_SCHEDULED')
                UNION ALL
                SELECT a.occurred_at, a.occurred_local, 'COSTO PROVEEDOR', sp.product_id, 'Proveedor ' || sp.supplier_name,
                       (a.old_values ->> 'last_cost')::numeric, (a.new_values ->> 'last_cost')::numeric, a.user_display_name
                FROM reporting.audit_log a JOIN reporting.supplier_products sp ON sp.supplier_product_id = a.entity_id
                WHERE {AuditCompany} AND a.action = 'SUPPLIER_PRODUCT_UPDATED' AND a.new_values ? 'last_cost'
                UNION ALL
                SELECT a.occurred_at, a.occurred_local, 'IMPUESTO',
                       COALESCE(a.new_values ->> 'product_id', a.old_values ->> 'product_id')::uuid,
                       CASE WHEN a.action = 'PRODUCT_TAX_CREATED' THEN 'Agregó ' ELSE 'Quitó ' END || COALESCE(t.tax_name, 'impuesto'),
                       NULL::numeric, NULL::numeric, a.user_display_name
                FROM reporting.audit_log a
                LEFT JOIN reporting.taxes t ON t.tax_id = COALESCE(a.new_values ->> 'tax_id', a.old_values ->> 'tax_id')::uuid
                WHERE {AuditCompany} AND a.action IN ('PRODUCT_TAX_CREATED', 'PRODUCT_TAX_DELETED'))
            SELECT x.occurred_at, x.kind, p.sku, p.product_name, x.detail, x.before_value, x.after_value,
                   round(100 * (x.after_value - x.before_value) / NULLIF(x.before_value, 0), 2) AS variation, x.user_display_name AS user_name
            FROM x LEFT JOIN reporting.products p ON p.product_id = x.product_id
            WHERE x.occurred_local::date BETWEEN @from AND @to
            ORDER BY x.occurred_at
            """,
            "Fecha y hora local del cambio. El primer precio de un producto no tiene 'antes'; el detalle indica la lista y si quedó programado."),

        new("AUDIT_SECURITY", "Seguridad: ingresos, bloqueos, contraseñas, roles y autorizaciones", AuditGroup,
            "Ingresos correctos y fallidos, bloqueos, cambios y restablecimientos de contraseña o PIN, sesiones cerradas, cambios de usuarios, roles y permisos y autorizaciones de supervisor.",
            AuditPermissions.LogView, DateRange, AuditDetailColumns(),
            AuditDetailSql("a.module = 'identity' AND a.action <> 'LOGOUT'")),

        new("AUDIT_SENSITIVE_EVENTS", "Eventos sensibles", AuditGroup,
            "Eventos de severidad ADVERTENCIA o CRÍTICA de todos los módulos, con usuario, caja y quién autorizó.",
            AuditPermissions.LogView, DateRange, AuditDetailColumns(),
            AuditDetailSql("a.severity IN ('WARNING', 'CRITICAL')")),

        new("AUDIT_BY_USER", "Actividad por usuario", AuditGroup, "Acciones por usuario y módulo en el período, con las advertencias y los críticos.",
            AuditPermissions.LogView, DateRange,
            [
                new("user_name", "Usuario", T.Text),
                new("module", "Módulo", T.Text),
                new("events", "Acciones", T.Count, Total: true),
                new("warnings", "Advertencias", T.Count, Total: true),
                new("criticals", "Críticos", T.Count, Total: true),
                new("first_at", "Primera", T.DateTime),
                new("last_at", "Última", T.DateTime),
            ],
            $"""
            SELECT COALESCE(a.user_display_name, '(sistema)') AS user_name, a.module, count(*) AS events,
                   count(*) FILTER (WHERE a.severity = 'WARNING') AS warnings, count(*) FILTER (WHERE a.severity = 'CRITICAL') AS criticals,
                   min(a.occurred_at) AS first_at, max(a.occurred_at) AS last_at
            FROM reporting.audit_log a
            WHERE {AuditCompany} AND a.occurred_local::date BETWEEN @from AND @to
            GROUP BY 1, 2
            ORDER BY 1, events DESC
            """),

        new("AUDIT_AFTER_HOURS", "Actividad fuera de horario", AuditGroup,
            "Acciones de usuarios fuera del horario de la tienda (configurable: reporting.after_hours_start y reporting.after_hours_end; por defecto 22:00–06:00).",
            AuditPermissions.LogView, DateRange, AuditDetailColumns(),
            AuditDetailSql(
                """
                a.user_id IS NOT NULL AND CASE WHEN @after_hours_start > @after_hours_end
                    THEN extract(hour FROM a.occurred_local) >= @after_hours_start OR extract(hour FROM a.occurred_local) < @after_hours_end
                    ELSE extract(hour FROM a.occurred_local) >= @after_hours_start AND extract(hour FROM a.occurred_local) < @after_hours_end END
                """)),

        new("AUDIT_EXPORTS", "Exportaciones de información", AuditGroup,
            "Reportes exportados, datos de clientes exportados y constancias de integridad emitidas.",
            AuditPermissions.LogView, DateRange, AuditDetailColumns(),
            AuditDetailSql("a.action IN ('REPORT_EXPORTED', 'CUSTOMER_DATA_EXPORTED', 'INTEGRITY_CERTIFICATE_ISSUED')")),

        new("AUDIT_INTEGRITY", "Integridad de la bitácora", AuditGroup,
            "Verificaciones (diarias, semanales y manuales), su resultado, el último sello verificado, los incidentes y su reconocimiento.",
            AuditPermissions.LogView, DateRange,
            [
                new("started_at", "Fecha y hora", T.DateTime),
                new("kind", "Tipo", T.Text),
                new("is_valid", "Sin hallazgos", T.Boolean),
                new("seals_checked", "Sellos", T.Count, Total: true),
                new("rows_checked", "Filas recalculadas", T.Count, Total: true),
                new("findings_count", "Hallazgos", T.Count, Total: true),
                new("last_seal_no", "Último sello", T.Count),
                new("last_seal_code", "Código del sello", T.Text),
                new("incident_summary", "Incidente", T.Text),
                new("acknowledged_by_name", "Reconocido por", T.Text),
                new("acknowledgement_note", "Nota", T.Text),
            ],
            """
            SELECT v.started_at, CASE v.kind WHEN 'INCREMENTAL' THEN 'Diaria incremental' WHEN 'FULL' THEN 'Semanal completa' ELSE 'Manual' END AS kind,
                   v.is_valid, v.seals_checked, v.rows_checked, v.findings_count, v.last_seal_no, v.last_seal_code, v.incident_summary,
                   v.acknowledged_by_name, v.acknowledgement_note
            FROM reporting.audit_verifications v
            WHERE (v.company_id = @company_id OR v.company_id IS NULL) AND v.started_local::date BETWEEN @from AND @to
            ORDER BY v.started_at
            """),
    ];

    public static ReportDefinition? Find(string code) =>
        All.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));

    private static ReportColumn[] AuditDetailColumns() =>
    [
        new("occurred_at", "Fecha y hora", T.DateTime),
        new("severity", "Severidad", T.Text),
        new("module", "Módulo", T.Text),
        new("action_name", "Acción", T.Text),
        new("user_name", "Usuario", T.Text),
        new("entity_label", "Registro", T.Text),
        new("summary", "Detalle", T.Text),
        new("authorized_by_name", "Autorizó", T.Text),
        new("ip_address", "IP", T.Text),
    ];

    /// <summary>Detalle de la bitácora con un filtro (Fase 10): fecha local del evento, empresa del nodo.</summary>
    private static string AuditDetailSql(string filter) =>
        $"""
        SELECT a.occurred_at, CASE a.severity WHEN 'CRITICAL' THEN 'CRÍTICA' WHEN 'WARNING' THEN 'ADVERTENCIA' ELSE 'INFORMATIVA' END AS severity,
               a.module, a.action_name, COALESCE(a.user_display_name, '(sistema)') AS user_name, a.entity_label, a.summary, a.authorized_by_name, a.ip_address
        FROM reporting.audit_log a
        WHERE {AuditCompany} AND a.occurred_local::date BETWEEN @from AND @to AND ({filter})
        ORDER BY a.occurred_at, a.seq
        """;

    /// <summary>Datos de este nodo (D9-13): empresa y sucursal de la sesión.</summary>
    private static string N(string alias) => $"{alias}.company_id = @company_id AND {alias}.branch_id = @branch_id";

    private static ReportColumn[] GroupedSalesColumns(string key, string label) =>
    [
        new(key, label, T.Text),
        new("tickets", "Tiquetes", T.Count, Total: true),
        new("units", "Unidades", T.Quantity, Total: true),
        new("total", "Ventas", T.Money, Total: true),
        new("average_ticket", "Ticket promedio", T.Money, Ratio: new RatioTotal("total", "tickets", 1m)),
        new("voided", "Anuladas", T.Count, Total: true),
        new("voided_total", "Valor anulado", T.Money, Total: true),
    ];

    /// <summary>Ventas agrupadas por una etiqueta de la venta (cajero, caja). Unidades = cantidad de las líneas activas.</summary>
    private static string GroupedSalesSql(string label, string key) =>
        $"""
        WITH u AS (
            SELECT l.sale_id, sum(l.quantity) AS units FROM reporting.sale_lines l WHERE {LinesSold} GROUP BY l.sale_id)
        SELECT {label} AS {key}, count(*) FILTER (WHERE s.status = 'COMPLETED') AS tickets, COALESCE(sum(u.units), 0) AS units,
               COALESCE(sum(s.total) FILTER (WHERE s.status = 'COMPLETED'), 0) AS total,
               round(sum(s.total) FILTER (WHERE s.status = 'COMPLETED') / NULLIF(count(*) FILTER (WHERE s.status = 'COMPLETED'), 0), 2) AS average_ticket,
               count(*) FILTER (WHERE s.status = 'VOIDED') AS voided, COALESCE(sum(s.total) FILTER (WHERE s.status = 'VOIDED'), 0) AS voided_total
        FROM reporting.sales s
        LEFT JOIN u ON u.sale_id = s.sale_id AND s.status = 'COMPLETED'
        WHERE {SalesInRange} AND s.status IN ('COMPLETED', 'VOIDED')
        GROUP BY 1
        ORDER BY total DESC
        """;

    /// <summary>Mezcla de ventas por una llave de la línea (producto, categoría, marca), neta de las devoluciones del período.</summary>
    private static string MixSql(string soldKey, string returnedKey, string labels, string join) =>
        $"""
        WITH sold AS (
            SELECT {soldKey} AS k, count(DISTINCT l.sale_id) AS tickets, sum(l.base_quantity) AS quantity, sum(l.gross) AS gross,
                   sum(l.promotion_discount + l.line_discount + l.global_discount_share) AS discounts, sum(l.total) AS total
            FROM reporting.sale_lines l WHERE {LinesSold} GROUP BY 1),
        ret AS (
            SELECT {returnedKey} AS k, sum(rl.base_quantity) AS quantity, sum(rl.credit_amount) AS returned
            FROM reporting.return_lines rl WHERE {LinesReturned} GROUP BY 1),
        x AS (
            SELECT COALESCE(sold.k, ret.k) AS k, COALESCE(sold.tickets, 0) AS tickets, COALESCE(sold.quantity, 0) AS quantity_sold,
                   COALESCE(ret.quantity, 0) AS quantity_returned, COALESCE(sold.gross, 0) AS gross, COALESCE(sold.discounts, 0) AS discounts,
                   COALESCE(sold.total, 0) AS total, COALESCE(ret.returned, 0) AS returned
            FROM sold FULL JOIN ret ON ret.k = sold.k)
        SELECT {labels}, x.tickets, x.quantity_sold, x.quantity_returned, x.gross, x.discounts, x.total, x.returned, x.total - x.returned AS net_sales
        FROM x {join}
        ORDER BY net_sales DESC
        """;

    /// <summary>Utilidad por una llave: venta sin impuestos y costo de lo vendido, menos lo devuelto en el período (D9-06).</summary>
    private static string ProfitSql(string soldKey, string returnedKey, string returnedJoin, string labels, string join, string orderBy) =>
        $"""
        WITH sold AS (
            SELECT {soldKey} AS k, sum(l.tax_base) AS net, sum(l.cost_total) AS cost
            FROM reporting.sale_lines l WHERE {LinesSold} GROUP BY 1),
        ret AS (
            SELECT {returnedKey} AS k, sum(rl.tax_base_share) AS net, sum(rl.cost_total) AS cost
            FROM reporting.return_lines rl {returnedJoin} WHERE {LinesReturned} GROUP BY 1),
        x AS (
            SELECT COALESCE(sold.k, ret.k) AS k, COALESCE(sold.net, 0) - COALESCE(ret.net, 0) AS net_sales,
                   COALESCE(sold.cost, 0) - COALESCE(ret.cost, 0) AS cost
            FROM sold FULL JOIN ret ON ret.k = sold.k)
        SELECT {labels}, round(x.net_sales, 2) AS net_sales, round(x.cost, 2) AS cost, round(x.net_sales - x.cost, 2) AS profit,
               round(100 * (x.net_sales - x.cost) / NULLIF(x.net_sales, 0), 2) AS margin
        FROM x {join}
        ORDER BY {orderBy}
        """;
}
