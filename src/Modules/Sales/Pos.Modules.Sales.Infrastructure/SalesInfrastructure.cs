using System.Data.Common;
using System.Globalization;
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
            ORDER BY s.opened_at DESC
            LIMIT @limit
            """,
            new
            {
                branchId = filter.BranchId, from = SalesDb.Text(filter.From), to = SalesDb.Text(filter.To), status = filter.Status, number = filter.Number,
                terminal = filter.PosTerminalId, session = filter.CashSessionId, limit = filter.Limit,
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
    }
}
