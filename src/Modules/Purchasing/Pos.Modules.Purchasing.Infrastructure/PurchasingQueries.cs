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
