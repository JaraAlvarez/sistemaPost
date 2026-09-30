using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Infrastructure;

internal sealed class SalesModelContributor : IModelContributor
{

    public void Configure(ModelBuilder modelBuilder)
    {
        // El snapshot fiscal es un valor (jsonb), no una entidad.
        modelBuilder.Ignore<CustomerFiscal>();
        modelBuilder.Entity<Sale>(b =>
        {
            b.ToTable("sales", "sales");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.ReturnStatus).HasUpperSnakeConversion();
            b.HasMany(x => x.Lines).WithOne().HasForeignKey("SaleId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).HasField("_lines");
            b.HasMany(x => x.Payments).WithOne().HasForeignKey("SaleId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Payments).HasField("_payments");
            b.HasMany(x => x.Discounts).WithOne().HasForeignKey("SaleId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Discounts).HasField("_discounts");
            b.Ignore(x => x.ActiveLines);
            b.Property(x => x.CustomerFiscal).HasColumnType("jsonb").HasConversion(new FiscalConverter());
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<SaleLine>(b =>
        {
            b.ToTable("sale_lines", "sales");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Ignore(x => x.IsActive);
            b.HasMany(x => x.Taxes).WithOne().HasForeignKey("SaleLineId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Taxes).HasField("_taxes");
        });

        modelBuilder.Entity<SaleLineTax>(b =>
        {
            b.ToTable("sale_line_taxes", "sales");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<SalePayment>(b =>
        {
            b.ToTable("sale_payments", "sales");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CardLast4).HasColumnName("card_last4");
        });

        modelBuilder.Entity<SaleDiscount>(b =>
        {
            b.ToTable("sale_discounts", "sales");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Scope).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
        });

        modelBuilder.Entity<CustomerReturn>(b =>
        {
            b.ToTable("customer_returns", "sales");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasMany(x => x.Lines).WithOne().HasForeignKey("CustomerReturnId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).HasField("_lines");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<CustomerReturnLine>(b =>
        {
            b.ToTable("customer_return_lines", "sales");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Destination).HasUpperSnakeConversion();
        });
    }
}

/// <summary>Snapshot fiscal del comprador ↔ jsonb (D8-05).</summary>
internal sealed class FiscalConverter()
    : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<CustomerFiscal?, string?>(
        v => v == null ? null : JsonSerializer.Serialize(v, Options),
        v => v == null ? null : JsonSerializer.Deserialize<CustomerFiscal>(v, Options))
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

internal static class SalesDb
{
    public static async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(PosDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static string? Text(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

internal sealed class SalesStore(PosDbContext context) : ISalesStore
{
    public void Add(Sale sale) => context.Add(sale);

    public void Add(CustomerReturn customerReturn) => context.Add(customerReturn);

    public Task<Sale?> GetSaleAsync(Guid id, CancellationToken cancellationToken) =>
        Sales().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<Sale?> LockSaleAsync(Guid id, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await SalesDb.OpenAsync(context, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT id FROM sales.sales WHERE id = @id FOR UPDATE", new { id }, transaction, cancellationToken: cancellationToken));
        return await GetSaleAsync(id, cancellationToken);
    }

    public Task<Sale?> GetOpenSaleAsync(Guid posTerminalId, CancellationToken cancellationToken) =>
        Sales().FirstOrDefaultAsync(s => s.PosTerminalId == posTerminalId && s.Status == SaleStatus.Open, cancellationToken);

    public Task<int> CountHeldAsync(Guid posTerminalId, CancellationToken cancellationToken) =>
        context.Set<Sale>().CountAsync(s => s.PosTerminalId == posTerminalId && s.Status == SaleStatus.OnHold, cancellationToken);

    public Task<CustomerReturn?> GetReturnAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<CustomerReturn>().Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<bool> HasDraftReturnAsync(Guid originalSaleId, CancellationToken cancellationToken) =>
        context.Set<CustomerReturn>().AnyAsync(r => r.OriginalSaleId == originalSaleId && r.Status == ReturnStatus.Draft, cancellationToken);

    private IQueryable<Sale> Sales() =>
        context.Set<Sale>().Include(s => s.Lines).ThenInclude(l => l.Taxes).Include(s => s.Payments).Include(s => s.Discounts).AsSplitQuery();
}

/// <summary>Lecturas de pantalla y del tiquete con SQL directo (cruza con org e identity para mostrar nombres).</summary>
internal sealed class SalesReadModel(PosDbContext context) : ISalesReadModel
{
    public async Task<TicketHeader?> GetTicketHeaderAsync(Guid posTerminalId, Guid cashierId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await SalesDb.OpenAsync(context, cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TicketHeader>(new CommandDefinition(
            """
            SELECT c.legal_name AS LegalName, c.trade_name AS TradeName,
                   c.identification_number || COALESCE('-' || c.check_digit, '') AS Nit, c.address AS Address, COALESCE(b.phone, c.phone) AS Phone,
                   b.name AS BranchName, b.address AS BranchAddress, t.code AS TerminalCode,
                   (SELECT u.display_name FROM identity.users u WHERE u.id = @cashierId) AS CashierName
            FROM org.pos_terminals t
            JOIN org.branches b ON b.id = t.branch_id
            JOIN org.companies c ON c.id = b.company_id
            WHERE t.id = @posTerminalId
            """,
            new { posTerminalId, cashierId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SaleSummaryDto>> ListSalesAsync(SaleFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var (connection, transaction) = await SalesDb.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            $"""
            {SummarySelect}
            WHERE s.branch_id = @branchId
              AND (@from::date IS NULL OR s.business_date >= @from::date)
              AND (@to::date IS NULL OR s.business_date <= @to::date)
              AND (@status::text IS NULL OR s.status = @status)
              AND (@number::text IS NULL OR s.number = @number)
              AND (@terminal::uuid IS NULL OR s.pos_terminal_id = @terminal)
              AND (@session::uuid IS NULL OR s.cash_session_id = @session)
              AND (@customer::uuid IS NULL OR s.customer_id = @customer)
            ORDER BY s.opened_at DESC
            LIMIT @limit
            """,
            new
            {
                branchId = filter.BranchId, from = SalesDb.Text(filter.From), to = SalesDb.Text(filter.To), status = filter.Status, number = filter.Number,
                terminal = filter.PosTerminalId, session = filter.CashSessionId, limit = filter.Limit, customer = filter.CustomerId,
            },
            transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToDto())];
    }

    public async Task<IReadOnlyList<SaleSummaryDto>> ListHeldAsync(Guid posTerminalId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await SalesDb.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            $"{SummarySelect} WHERE s.pos_terminal_id = @posTerminalId AND s.status = 'ON_HOLD' ORDER BY s.held_at",
            new { posTerminalId }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToDto())];
    }

    private const string SummarySelect =
        """
        SELECT s.id AS Id, s.number AS Number, s.status AS Status, s.return_status AS ReturnStatus, s.business_date AS BusinessDate, t.code AS TerminalCode,
               u.display_name AS CashierName, s.customer_name AS CustomerName, s.total AS Total, s.opened_at AS OpenedAt, s.completed_at AS CompletedAt,
               s.hold_label AS HoldLabel
        FROM sales.sales s
        JOIN org.pos_terminals t ON t.id = s.pos_terminal_id
        JOIN identity.users u ON u.id = s.cashier_id
        """;

    private sealed class SummaryRow
    {
        public Guid Id { get; set; }

        public string? Number { get; set; }

        public string Status { get; set; } = string.Empty;

        public string ReturnStatus { get; set; } = string.Empty;

        public DateTime BusinessDate { get; set; }

        public string TerminalCode { get; set; } = string.Empty;

        public string CashierName { get; set; } = string.Empty;

        public string CustomerName { get; set; } = string.Empty;

        public decimal Total { get; set; }

        public DateTime OpenedAt { get; set; }

        public DateTime? CompletedAt { get; set; }

        public string? HoldLabel { get; set; }

        public SaleSummaryDto ToDto() => new(
            Id, Number, Status, ReturnStatus, DateOnly.FromDateTime(BusinessDate), TerminalCode, CashierName, CustomerName, Total, SalesDb.Utc(OpenedAt),
            CompletedAt is { } completed ? SalesDb.Utc(completed) : null, HoldLabel);
    }
}

/// <summary>
/// Historial del cliente calculado en línea desde las ventas y los cambios (D8-13), con el índice por cliente de la migración
/// 024. Total comprado = ventas completadas − créditos de cambios y reintegros (un cambio no se cuenta dos veces).
/// </summary>
internal sealed class CustomerSalesHistory(PosDbContext context) : ICustomerSalesHistory
{
    public async Task<IReadOnlyList<CustomerHistoryEntryDto>> GetHistoryAsync(
        Guid partyId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (connection, transaction) = await SalesDb.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<HistoryRow>(new CommandDefinition(
            """
            SELECT * FROM (
                SELECT 'SALE' AS Kind, s.id AS Id, s.number AS Number, s.business_date AS BusinessDate, s.completed_at AS At, b.name AS BranchName,
                       t.code AS TerminalCode, s.total AS Total, s.status AS Status,
                       (SELECT string_agg(DISTINCT p.method_code, ';') FROM sales.sale_payments p WHERE p.sale_id = s.id) AS Payments,
                       NULL::varchar AS OriginalSaleNumber
                FROM sales.sales s
                JOIN org.branches b ON b.id = s.branch_id
                JOIN org.pos_terminals t ON t.id = s.pos_terminal_id
                WHERE s.company_id = @companyId AND s.customer_id = @partyId AND s.status IN ('COMPLETED', 'VOIDED')
                UNION ALL
                SELECT r.kind, r.id, r.number, r.business_date, r.completed_at, b.name, t.code, r.credit_total, r.status, NULL, o.number
                FROM sales.customer_returns r
                JOIN sales.sales o ON o.id = r.original_sale_id
                JOIN org.branches b ON b.id = r.branch_id
                JOIN org.pos_terminals t ON t.id = r.pos_terminal_id
                WHERE o.company_id = @companyId AND o.customer_id = @partyId AND r.status = 'COMPLETED'
            ) h
            WHERE (@from::date IS NULL OR h.BusinessDate >= @from::date) AND (@to::date IS NULL OR h.BusinessDate <= @to::date)
            ORDER BY h.At DESC
            LIMIT @limit OFFSET @offset
            """,
            new
            {
                companyId = context.TenantCompanyId, partyId, from = SalesDb.Text(from), to = SalesDb.Text(to), limit = pageSize, offset = (page - 1) * pageSize,
            },
            transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new CustomerHistoryEntryDto(
            r.Kind, r.Id, r.Number, DateOnly.FromDateTime(r.BusinessDate), SalesDb.Utc(r.At), r.BranchName, r.TerminalCode, r.Total, r.Status,
            r.Payments?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [], r.OriginalSaleNumber))];
    }

    public async Task<CustomerSalesSummaryDto> GetSummaryAsync(Guid partyId, CancellationToken cancellationToken = default)
    {
        var (connection, transaction) = await SalesDb.OpenAsync(context, cancellationToken);
        var args = new { companyId = context.TenantCompanyId, partyId };
        var totals = await connection.QuerySingleAsync<SummaryRow>(new CommandDefinition(
            """
            SELECT COUNT(*) FILTER (WHERE s.status = 'COMPLETED')::int AS Purchases,
                   COALESCE(SUM(s.total) FILTER (WHERE s.status = 'COMPLETED'), 0) AS Gross,
                   COUNT(*) FILTER (WHERE s.status = 'VOIDED')::int AS Voided,
                   MIN(s.completed_at) FILTER (WHERE s.status = 'COMPLETED') AS FirstAt,
                   MAX(s.completed_at) FILTER (WHERE s.status = 'COMPLETED') AS LastAt,
                   (SELECT b.name FROM sales.sales x JOIN org.branches b ON b.id = x.branch_id
                    WHERE x.company_id = @companyId AND x.customer_id = @partyId AND x.status = 'COMPLETED'
                    GROUP BY b.name ORDER BY COUNT(*) DESC, b.name LIMIT 1) AS UsualBranch,
                   (SELECT COALESCE(SUM(r.credit_total), 0) FROM sales.customer_returns r JOIN sales.sales o ON o.id = r.original_sale_id
                    WHERE o.company_id = @companyId AND o.customer_id = @partyId AND r.status = 'COMPLETED') AS Returned,
                   (SELECT COUNT(*)::int FROM sales.customer_returns r JOIN sales.sales o ON o.id = r.original_sale_id
                    WHERE o.company_id = @companyId AND o.customer_id = @partyId AND r.status = 'COMPLETED' AND r.kind = 'EXCHANGE') AS Exchanges
            FROM sales.sales s
            WHERE s.company_id = @companyId AND s.customer_id = @partyId AND s.status IN ('COMPLETED', 'VOIDED')
            """,
            args, transaction, cancellationToken: cancellationToken));
        var top = await connection.QueryAsync<CustomerTopProductDto>(new CommandDefinition(
            """
            SELECT l.product_id AS ProductId, l.sku AS Sku, l.name AS Name, SUM(l.quantity - l.returned_quantity) AS Quantity,
                   ROUND(SUM(l.total * (l.quantity - l.returned_quantity) / l.quantity), 2) AS Total
            FROM sales.sale_lines l
            JOIN sales.sales s ON s.id = l.sale_id
            WHERE s.company_id = @companyId AND s.customer_id = @partyId AND s.status = 'COMPLETED' AND l.status = 'ACTIVE' AND l.quantity > l.returned_quantity
            GROUP BY l.product_id, l.sku, l.name
            ORDER BY SUM(l.quantity - l.returned_quantity) DESC, l.sku
            LIMIT 10
            """,
            args, transaction, cancellationToken: cancellationToken));
        return new CustomerSalesSummaryDto(
            totals.Purchases, totals.Gross - totals.Returned, totals.Purchases == 0 ? 0m : decimal.Round(totals.Gross / totals.Purchases, 2, MidpointRounding.AwayFromZero),
            totals.FirstAt is { } first ? SalesDb.Utc(first) : null, totals.LastAt is { } last ? SalesDb.Utc(last) : null, totals.UsualBranch, totals.Voided,
            totals.Exchanges, totals.Returned, [.. top]);
    }

    private sealed class HistoryRow
    {
        public string Kind { get; set; } = string.Empty;

        public Guid Id { get; set; }

        public string? Number { get; set; }

        public DateTime BusinessDate { get; set; }

        public DateTime At { get; set; }

        public string BranchName { get; set; } = string.Empty;

        public string TerminalCode { get; set; } = string.Empty;

        public decimal Total { get; set; }

        public string Status { get; set; } = string.Empty;

        public string? Payments { get; set; }

        public string? OriginalSaleNumber { get; set; }
    }

    private sealed class SummaryRow
    {
        public int Purchases { get; set; }

        public decimal Gross { get; set; }

        public int Voided { get; set; }

        public DateTime? FirstAt { get; set; }

        public DateTime? LastAt { get; set; }

        public string? UsualBranch { get; set; }

        public decimal Returned { get; set; }

        public int Exchanges { get; set; }
    }
}

/// <summary>D7-13: la caja pregunta por las ventas sin terminar antes de cerrar (RN-CSH-03).</summary>
internal sealed class OpenSalesProbe(PosDbContext context) : IOpenSalesProbe
{
    public async Task<IReadOnlyList<OpenSaleInfo>> GetOpenSalesAsync(Guid posTerminalId, CancellationToken cancellationToken = default) =>
        await context.Set<Sale>().AsNoTracking()
            .Where(s => s.PosTerminalId == posTerminalId && (s.Status == SaleStatus.Open || s.Status == SaleStatus.OnHold))
            .OrderBy(s => s.OpenedAt)
            .Select(s => new OpenSaleInfo(s.Id, s.Status == SaleStatus.Open ? "OPEN" : "ON_HOLD", s.HoldLabel, s.Total, s.OpenedAt))
            .ToListAsync(cancellationToken);
}

internal sealed class SalesConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_sales__terminal_open"] = SalesErrors.OpenSaleExists,
        ["ux_customer_returns__draft"] = Error.Conflict("SALES.EXCHANGE_IN_PROGRESS", "La venta ya tiene un cambio en curso: cóbrelo o cancélelo antes de iniciar otro."),
        ["ck_sale_lines__returned"] = SalesErrors.ExchangeQuantityExceeded,
    };
}

public static class SalesInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, SalesModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, SalesConstraintErrors>();
        services.AddScoped<ISalesStore, SalesStore>();
        services.AddScoped<ISalesReadModel, SalesReadModel>();
        services.AddScoped<IOpenSalesProbe, OpenSalesProbe>();
        services.AddScoped<ICustomerSalesHistory, CustomerSalesHistory>();
    }
}
