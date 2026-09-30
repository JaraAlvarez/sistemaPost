using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Purchasing.Application;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Text;

namespace Pos.Modules.Purchasing.Infrastructure;

/// <summary>
/// Consultas de pantalla con SQL directo. Para mostrar el nombre del proveedor cruza con parties.parties: es un modelo de
/// lectura; las escrituras nunca tocan otros esquemas. Usa la conexión de la petición (ve lo que el comando ya cambió).
/// </summary>
internal sealed class PurchasingQueries(PosDbContext context, IInstallationContext installation) : IPurchasingQueries
{
    private const string SupplierName =
        "CASE WHEN p.person_type = 'LEGAL' THEN p.legal_name ELSE p.first_names || ' ' || p.last_names END";

    public async Task<IReadOnlyList<SupplierDto>> ListSuppliersAsync(string? search, bool includeInactive, CancellationToken cancellationToken) =>
        await QuerySuppliersAsync(null, search, includeInactive, cancellationToken);

    public async Task<SupplierDto?> GetSupplierAsync(Guid supplierId, CancellationToken cancellationToken) =>
        (await QuerySuppliersAsync(supplierId, null, includeInactive: true, cancellationToken)).SingleOrDefault();

    public async Task<IReadOnlyDictionary<Guid, string>> SupplierNamesAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(Guid Id, string Name)>(new CommandDefinition(
            $"SELECT s.id, {SupplierName} FROM purchasing.suppliers s JOIN parties.parties p ON p.id = s.party_id WHERE s.id = ANY(@ids)",
            new { ids = supplierIds.ToArray() }, transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }

    public async Task<IReadOnlyList<PurchasingDocumentDto>> ListDocumentsAsync(DocumentFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var (table, date, total, reference) = (filter.Kind ?? string.Empty).ToUpperInvariant() switch
        {
            "ORDER" => ("purchasing.purchase_orders", "d.order_date", "d.total", "NULL::text"),
            "RETURN" => ("purchasing.supplier_returns", "d.business_date", "d.credit_total", "d.settlement_reference"),
            "PAYMENT" => ("purchasing.payable_payments", "d.payment_date", "d.amount", "d.reference"),
            _ => ("purchasing.purchases", "d.invoice_date", "d.total", "d.supplier_invoice_number"),
        };
        var kind = (filter.Kind ?? string.Empty).ToUpperInvariant() is "ORDER" or "RETURN" or "PAYMENT" ? filter.Kind!.ToUpperInvariant() : "PURCHASE";
        var support = kind == "PURCHASE" && filter.RequiresSupportDocument is not null ? "AND d.requires_support_document = @support" : string.Empty;
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            $"""
            SELECT d.id AS Id, d.number AS Number, d.supplier_id AS SupplierId, {SupplierName} AS SupplierName, d.status AS Status, {date} AS Date,
                   {total} AS Total, {reference} AS Reference, d.created_at AS CreatedAt
            FROM {table} d
            JOIN purchasing.suppliers s ON s.id = d.supplier_id
            JOIN parties.parties p ON p.id = s.party_id
            WHERE d.branch_id = @branchId
              AND (@supplierId::uuid IS NULL OR d.supplier_id = @supplierId)
              AND (@status::text IS NULL OR d.status = @status)
              {support}
            ORDER BY d.created_at DESC
            LIMIT {Math.Clamp(filter.Limit, 1, 2000)}
            """,
            new { branchId = filter.BranchId, filter.SupplierId, status = filter.Status?.ToUpperInvariant(), support = filter.RequiresSupportDocument },
            transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new PurchasingDocumentDto(
            r.Id, kind, r.Number, r.SupplierId, r.SupplierName, r.Status, DateOnly.FromDateTime(r.Date), r.Total, r.Reference,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc))))];
    }

    public async Task<IReadOnlyList<PayableDto>> ListPayablesAsync(Guid? supplierId, bool openOnly, DateOnly asOf, bool withEntries, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<PayableRow>(new CommandDefinition(
            $"""
            SELECT a.id AS Id, a.supplier_id AS SupplierId, {SupplierName} AS SupplierName, a.purchase_id AS PurchaseId, a.document_number AS DocumentNumber,
                   a.issue_date AS IssueDate, a.due_date AS DueDate, a.original_amount AS OriginalAmount, a.balance AS Balance, a.status AS Status
            FROM purchasing.accounts_payable a
            JOIN purchasing.suppliers s ON s.id = a.supplier_id
            JOIN parties.parties p ON p.id = s.party_id
            WHERE a.company_id = @companyId
              AND (@supplierId::uuid IS NULL OR a.supplier_id = @supplierId)
              AND (NOT @openOnly OR (a.status = 'OPEN' AND a.balance > 0))
            ORDER BY a.due_date, a.document_number
            LIMIT 5000
            """,
            new { companyId = installation.CompanyId, supplierId, openOnly }, transaction, cancellationToken: cancellationToken))).ToList();

        var entries = new Dictionary<Guid, List<PayableEntryDto>>();
        if (withEntries && rows.Count > 0)
        {
            var entryRows = await connection.QueryAsync<EntryRow>(new CommandDefinition(
                """
                SELECT id AS Id, account_id AS AccountId, entry_type AS EntryType, amount AS Amount, balance_after AS BalanceAfter, source_type AS SourceType,
                       source_id AS SourceId, source_number AS SourceNumber, occurred_at AS OccurredAt
                FROM purchasing.payable_entries WHERE account_id = ANY(@ids) ORDER BY occurred_at, id
                """,
                new { ids = rows.Select(r => r.Id).ToArray() }, transaction, cancellationToken: cancellationToken));
            foreach (var e in entryRows)
            {
                if (!entries.TryGetValue(e.AccountId, out var list))
                {
                    entries[e.AccountId] = list = [];
                }

                list.Add(new PayableEntryDto(e.Id, e.EntryType, e.Amount, e.BalanceAfter, e.SourceType, e.SourceId, e.SourceNumber,
                    new DateTimeOffset(DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc))));
            }
        }

        return [.. rows.Select(r =>
        {
            var due = DateOnly.FromDateTime(r.DueDate);
            var open = r.Status == "OPEN" && r.Balance > 0m;
            return new PayableDto(
                r.Id, r.SupplierId, r.SupplierName, r.PurchaseId, r.DocumentNumber, DateOnly.FromDateTime(r.IssueDate), due, r.OriginalAmount, r.Balance, r.Status,
                open ? Math.Max(0, asOf.DayNumber - due.DayNumber) : 0, open ? Aging.Bucket(due, asOf) : Aging.Current,
                entries.TryGetValue(r.Id, out var list) ? list : []);
        })];
    }

    public async Task<SupplierActivity> GetSupplierActivityAsync(Guid supplierId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<ActivityRow>(new CommandDefinition(
            """
            SELECT
                COALESCE((SELECT SUM(p.total) FROM purchasing.purchases p
                          WHERE p.supplier_id = @supplierId AND p.status = 'POSTED' AND p.invoice_date BETWEEN @from AND @to), 0) AS PurchasedTotal,
                (SELECT count(*) FROM purchasing.purchases p
                 WHERE p.supplier_id = @supplierId AND p.status = 'POSTED' AND p.invoice_date BETWEEN @from AND @to)::int AS PurchaseCount,
                last.invoice_date AS LastPurchaseDate,
                last.total AS LastPurchaseTotal,
                COALESCE((SELECT SUM(r.credit_total) FROM purchasing.supplier_returns r
                          WHERE r.supplier_id = @supplierId AND r.status IN ('POSTED', 'SETTLED') AND r.business_date BETWEEN @from AND @to), 0) AS ReturnsTotal,
                (SELECT count(*) FROM purchasing.supplier_returns r
                 WHERE r.supplier_id = @supplierId AND r.status IN ('POSTED', 'SETTLED') AND r.business_date BETWEEN @from AND @to)::int AS ReturnCount,
                (SELECT count(*) FROM purchasing.supplier_products sp
                 JOIN catalog.products c ON c.id = sp.product_id
                 WHERE sp.supplier_id = @supplierId AND sp.deleted_at IS NULL AND c.deleted_at IS NULL AND c.status = 'ACTIVE')::int AS ActiveProducts
            FROM (SELECT 1) one
            LEFT JOIN LATERAL (
                SELECT p.invoice_date, p.total FROM purchasing.purchases p
                WHERE p.supplier_id = @supplierId AND p.status = 'POSTED'
                ORDER BY p.invoice_date DESC, p.posted_at DESC LIMIT 1) last ON true
            """,
            new { supplierId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) }, transaction, cancellationToken: cancellationToken));
        return new SupplierActivity(
            row.PurchasedTotal, row.PurchaseCount, row.LastPurchaseDate is { } last ? DateOnly.FromDateTime(last) : null, row.LastPurchaseTotal, row.ReturnsTotal,
            row.ReturnCount, row.ActiveProducts);
    }

    public async Task<IReadOnlyList<ProductSupplierDto>> ListProductSuppliersAsync(Guid productId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProductSupplierRow>(new CommandDefinition(
            $"""
            SELECT s.id AS SupplierId, s.code AS SupplierCode, {SupplierName} AS SupplierName, s.status AS SupplierStatus, sp.supplier_code AS SupplierProductCode,
                   sp.packaging_id AS PackagingId, sp.last_cost AS LastCost, sp.last_purchase_at AS LastPurchaseAt, sp.lead_time_days AS LeadTimeDays,
                   sp.is_preferred AS IsPreferred
            FROM purchasing.supplier_products sp
            JOIN purchasing.suppliers s ON s.id = sp.supplier_id
            JOIN parties.parties p ON p.id = s.party_id
            WHERE sp.company_id = @companyId AND sp.product_id = @productId AND sp.deleted_at IS NULL AND s.deleted_at IS NULL
            """,
            new { companyId = installation.CompanyId, productId }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new ProductSupplierDto(
            r.SupplierId, r.SupplierCode, r.SupplierName, r.SupplierStatus, r.SupplierProductCode, r.PackagingId, r.LastCost,
            r.LastPurchaseAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null, r.LeadTimeDays, r.IsPreferred))];
    }

    public async Task<IReadOnlyList<CostHistoryEntryDto>> ListCostHistoryAsync(
        Guid productId, Guid? supplierId, DateOnly? from, DateOnly? to, int limit, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<CostRow>(new CommandDefinition(
            $"""
            SELECT d.id AS PurchaseId, d.number AS PurchaseNumber, d.supplier_invoice_number AS SupplierInvoiceNumber, d.invoice_date AS InvoiceDate,
                   d.supplier_id AS SupplierId, {SupplierName} AS SupplierName, l.packaging_id AS PackagingId, l.factor AS Factor, l.quantity AS Quantity,
                   l.base_quantity AS BaseQuantity, l.unit_cost AS UnitCost, l.discount_amount AS DiscountAmount, l.net_unit_cost AS NetUnitCost
            FROM purchasing.purchase_lines l
            JOIN purchasing.purchases d ON d.id = l.purchase_id
            JOIN purchasing.suppliers s ON s.id = d.supplier_id
            JOIN parties.parties p ON p.id = s.party_id
            WHERE l.product_id = @productId AND d.company_id = @companyId AND d.status = 'POSTED'
              AND (@supplierId::uuid IS NULL OR d.supplier_id = @supplierId)
              AND (@from::date IS NULL OR d.invoice_date >= @from)
              AND (@to::date IS NULL OR d.invoice_date <= @to)
            ORDER BY d.invoice_date DESC, d.posted_at DESC, l.line_number
            LIMIT {Math.Clamp(limit, 1, 500)}
            """,
            new
            {
                productId, companyId = installation.CompanyId, supplierId, from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue),
            },
            transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new CostHistoryEntryDto(
            r.PurchaseId, r.PurchaseNumber, r.SupplierInvoiceNumber, DateOnly.FromDateTime(r.InvoiceDate), r.SupplierId, r.SupplierName, r.PackagingId, r.Factor,
            r.Quantity, r.BaseQuantity, r.UnitCost, r.DiscountAmount, r.NetUnitCost))];
    }

    public async Task<IReadOnlyList<BankDto>> ListBanksAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string Code, string Name)>(new CommandDefinition(
            "SELECT code, name FROM ref.banks WHERE (@includeInactive OR is_active) ORDER BY sort_order, name",
            new { includeInactive }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new BankDto(r.Code, r.Name))];
    }

    private async Task<IReadOnlyList<SupplierDto>> QuerySuppliersAsync(Guid? supplierId, string? search, bool includeInactive, CancellationToken cancellationToken)
    {
        var tokens = TextNormalization.ForSearch(search).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pattern = tokens.Length == 0 ? null : "%" + string.Join('%', tokens.Select(Escape)) + "%";
        var code = search?.Trim().ToUpperInvariant();
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<SupplierDto>(new CommandDefinition(
            $"""
            SELECT s.id AS Id, s.party_id AS PartyId, s.code AS Code, {SupplierName} AS Name,
                   p.identification_type || ' ' || p.identification_number || COALESCE('-' || p.check_digit, '') AS Identification,
                   s.payment_term_days AS PaymentTermDays, s.preferred_payment_method_id AS PreferredPaymentMethodId, s.credit_limit AS CreditLimit,
                   s.issues_invoices AS IssuesInvoices, s.notes AS Notes, s.status AS Status,
                   COALESCE((SELECT SUM(a.balance) FROM purchasing.accounts_payable a WHERE a.supplier_id = s.id AND a.status <> 'VOIDED'), 0) AS OpenBalance
            FROM purchasing.suppliers s
            JOIN parties.parties p ON p.id = s.party_id
            WHERE s.company_id = @companyId AND s.deleted_at IS NULL
              AND (@supplierId::uuid IS NULL OR s.id = @supplierId)
              AND (@includeInactive OR s.status <> 'INACTIVE')
              AND (@pattern::text IS NULL OR p.search_text LIKE @pattern OR s.code = @code OR p.identification_number = @code)
            ORDER BY 4
            LIMIT 2000
            """,
            new { companyId = installation.CompanyId, supplierId, includeInactive, pattern, code }, transaction, cancellationToken: cancellationToken));
        return [.. rows];
    }

    private async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    private static string Escape(string token) =>
        token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class ActivityRow
    {
        public decimal PurchasedTotal { get; set; }

        public int PurchaseCount { get; set; }

        public DateTime? LastPurchaseDate { get; set; }

        public decimal? LastPurchaseTotal { get; set; }

        public decimal ReturnsTotal { get; set; }

        public int ReturnCount { get; set; }

        public int ActiveProducts { get; set; }
    }

    private sealed class ProductSupplierRow
    {
        public Guid SupplierId { get; set; }

        public string SupplierCode { get; set; } = string.Empty;

        public string SupplierName { get; set; } = string.Empty;

        public string SupplierStatus { get; set; } = string.Empty;

        public string? SupplierProductCode { get; set; }

        public Guid? PackagingId { get; set; }

        public decimal? LastCost { get; set; }

        public DateTime? LastPurchaseAt { get; set; }

        public int? LeadTimeDays { get; set; }

        public bool IsPreferred { get; set; }
    }

    private sealed class CostRow
    {
        public Guid PurchaseId { get; set; }

        public string PurchaseNumber { get; set; } = string.Empty;

        public string SupplierInvoiceNumber { get; set; } = string.Empty;

        public DateTime InvoiceDate { get; set; }

        public Guid SupplierId { get; set; }

        public string SupplierName { get; set; } = string.Empty;

        public Guid? PackagingId { get; set; }

        public decimal Factor { get; set; }

        public decimal Quantity { get; set; }

        public decimal BaseQuantity { get; set; }

        public decimal UnitCost { get; set; }

        public decimal DiscountAmount { get; set; }

        public decimal NetUnitCost { get; set; }
    }

    private sealed class DocumentRow
    {
        public Guid Id { get; set; }

        public string Number { get; set; } = string.Empty;

        public Guid SupplierId { get; set; }

        public string SupplierName { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTime Date { get; set; }

        public decimal Total { get; set; }

        public string? Reference { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    private sealed class PayableRow
    {
        public Guid Id { get; set; }

        public Guid SupplierId { get; set; }

        public string SupplierName { get; set; } = string.Empty;

        public Guid PurchaseId { get; set; }

        public string DocumentNumber { get; set; } = string.Empty;

        public DateTime IssueDate { get; set; }

        public DateTime DueDate { get; set; }

        public decimal OriginalAmount { get; set; }

        public decimal Balance { get; set; }

        public string Status { get; set; } = string.Empty;
    }

    private sealed class EntryRow
    {
        public Guid Id { get; set; }

        public Guid AccountId { get; set; }

        public string EntryType { get; set; } = string.Empty;

        public decimal Amount { get; set; }

        public decimal BalanceAfter { get; set; }

        public string SourceType { get; set; } = string.Empty;

        public Guid SourceId { get; set; }

        public string? SourceNumber { get; set; }

        public DateTime OccurredAt { get; set; }
    }
}
