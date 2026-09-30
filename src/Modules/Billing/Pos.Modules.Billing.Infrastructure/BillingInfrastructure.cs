using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure.Outbox;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Security;
using Pos.Modules.Billing.Application;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.Domain;
using Pos.Modules.Billing.Infrastructure.Factus;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Billing.Infrastructure;

internal sealed class BillingModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<FiscalBuyerData>();
        modelBuilder.Entity<FiscalDocument>(b =>
        {
            b.ToTable("fiscal_documents", "billing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Source).HasUpperSnakeConversion();
            b.Property(x => x.DocumentType).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.BuyerFiscal).HasColumnType("jsonb").HasConversion(new BuyerFiscalConverter());
            b.Ignore(x => x.IsElectronic);
            b.Ignore(x => x.IsOpen);
            b.HasMany(x => x.Events).WithOne().HasForeignKey("FiscalDocumentId").IsRequired().OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Events).HasField("_events");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<FiscalDocumentEvent>(b =>
        {
            b.ToTable("fiscal_document_events", "billing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<FiscalNumberingRange>(b =>
        {
            b.ToTable("fiscal_numbering_ranges", "billing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.DocumentType).HasUpperSnakeConversion();
            b.Ignore(x => x.UsagePercent);
            b.Ignore(x => x.Remaining);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<BillingProviderSettings>(b =>
        {
            b.ToTable("provider_settings", "billing");
            b.HasKey(x => x.CompanyId);
            b.Property(x => x.CompanyId).ValueGeneratedNever();
            b.Property(x => x.Environment).HasUpperSnakeConversion();
            b.Property(x => x.Mode).HasUpperSnakeConversion();
            b.Ignore(x => x.HasCredentials);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });
    }
}

/// <summary>Datos fiscales corregidos del adquirente ↔ jsonb.</summary>
internal sealed class BuyerFiscalConverter()
    : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<FiscalBuyerData?, string?>(
        v => v == null ? null : JsonSerializer.Serialize(v, Options),
        v => v == null ? null : JsonSerializer.Deserialize<FiscalBuyerData>(v, Options))
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

internal static class BillingDb
{
    public static async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(PosDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static string Text(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class FiscalDocumentStore(PosDbContext context) : IFiscalDocumentStore
{
    private static readonly FiscalStatus[] Queued = [FiscalStatus.Pending, FiscalStatus.Error, FiscalStatus.Contingency, FiscalStatus.Submitting];

    public void Add(FiscalDocument document) => context.Add(document);

    public Task<FiscalDocument?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        context.ChangeTracker.Entries<FiscalDocument>().Select(e => e.Entity).FirstOrDefault(d => d.Id == id) is { } tracked
            ? Task.FromResult<FiscalDocument?>(tracked)
            : context.Set<FiscalDocument>().Include(d => d.Events).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<FiscalDocument?> GetBySourceAsync(Guid sourceId, string source, CancellationToken cancellationToken)
    {
        var kind = BillingMapping.Source(source);

        // Primero lo que el caso de uso ya agregó y no se ha guardado (la emisión y la anulación ocurren en la misma transacción).
        return context.ChangeTracker.Entries<FiscalDocument>().Select(e => e.Entity).FirstOrDefault(d => d.SourceId == sourceId && d.Source == kind)
            ?? await context.Set<FiscalDocument>().Include(d => d.Events).SingleOrDefaultAsync(d => d.SourceId == sourceId && d.Source == kind, cancellationToken);
    }

    public async Task<IReadOnlyList<FiscalDocument>> ListAsync(DocumentFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var search = filter.Search;
        return await context.Set<FiscalDocument>().Include(d => d.Events).AsSplitQuery()
            .Where(d => d.BranchId == filter.BranchId)
            .Where(d => filter.From == null || d.BusinessDate >= filter.From)
            .Where(d => filter.To == null || d.BusinessDate <= filter.To)
            .Where(d => filter.Status == null || d.Status == filter.Status)
            .Where(d => filter.Type == null || d.DocumentType == filter.Type)
            .Where(d => filter.Source == null || d.Source == filter.Source)
            .Where(d => search == null || d.SourceNumber.Contains(search) || d.BuyerIdentification.Contains(search)
                        || (d.FiscalNumber != null && d.FiscalNumber.Contains(search)) || d.BuyerName.Contains(search))
            .OrderByDescending(d => d.IssuedAt)
            .Take(filter.Limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await context.Set<FiscalDocument>().AsNoTracking()
            .Where(d => Queued.Contains(d.Status) && d.NextAttemptAt != null && d.NextAttemptAt <= now)
            .OrderBy(d => d.IssuedAt).ThenBy(d => d.Id)
            .Select(d => d.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
}

internal sealed class FiscalRangeStore(PosDbContext context) : IFiscalRangeStore
{
    public void Add(FiscalNumberingRange range) => context.Add(range);

    public Task<FiscalNumberingRange?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<FiscalNumberingRange>().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<FiscalNumberingRange>> ListAsync(CancellationToken cancellationToken) =>
        await context.Set<FiscalNumberingRange>().OrderBy(r => r.RangeFrom).ToListAsync(cancellationToken);
}

internal sealed class BillingSettingsStore(PosDbContext context) : IBillingSettingsStore
{
    public void Add(BillingProviderSettings settings) => context.Add(settings);

    public async Task<BillingProviderSettings?> GetAsync(Guid companyId, CancellationToken cancellationToken) =>
        context.ChangeTracker.Entries<BillingProviderSettings>().Select(e => e.Entity).FirstOrDefault(s => s.CompanyId == companyId)
        ?? await context.Set<BillingProviderSettings>().SingleOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);
}

/// <summary>
/// Credenciales del proveedor cifradas con DPAPI de la máquina y una entropía propia (mismo mecanismo que el secreto de los destinos
/// de backup, Fase 11). Fuera de Windows (desarrollo) se guardan sin cifrar, igual que los backups.
/// </summary>
internal sealed class DpapiFiscalCredentialProtector : IFiscalCredentialProtector
{
    // Identificador interno estable (anterior al nombre BusinessPost): cambiarlo haría ilegibles las credenciales ya cifradas.
    public const string Purpose = "PosSupermercado.Billing.ProviderCredentials.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public byte[] Protect(FiscalCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials, Json);
        return OperatingSystem.IsWindows() ? ProtectedSecret.ProtectBytes(plain, Purpose) : plain;
    }

    public FiscalCredentials? Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        try
        {
            var plain = OperatingSystem.IsWindows() ? ProtectedSecret.UnprotectBytes(protectedBytes, Purpose) : protectedBytes;
            return JsonSerializer.Deserialize<FiscalCredentials>(plain, Json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Lee el documento de origen GUARDADO con Dapper, en la conexión y la transacción del contexto (D11B-06): venta con líneas,
/// impuestos por tarifa, pagos y snapshot fiscal del comprador; cambio o garantía; compra con su proveedor; emisor y sucursal.
/// </summary>
internal sealed class FiscalSourceReader(PosDbContext context) : IFiscalSourceReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<FiscalIssuerInfo?> GetIssuerAsync(Guid branchId, Guid? posTerminalId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await BillingDb.OpenAsync(context, cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<IssuerRow>(new CommandDefinition(
            """
            SELECT c.identification_type AS IdentificationType, it.fiscal_code AS FiscalCode, c.identification_number AS IdentificationNumber,
                   c.check_digit AS CheckDigit, c.legal_name AS LegalName, c.person_type AS PersonType, c.tax_regime AS TaxRegime,
                   c.address AS CompanyAddress, c.municipality_code AS CompanyMunicipality, c.email AS Email, c.phone AS CompanyPhone,
                   b.id AS BranchId, b.code AS BranchCode, b.name AS BranchName, b.address AS BranchAddress, b.municipality_code AS BranchMunicipality,
                   b.phone AS BranchPhone, t.id AS TerminalId, t.code AS TerminalCode
            FROM org.branches b
            JOIN org.companies c ON c.id = b.company_id
            JOIN ref.identification_types it ON it.code = c.identification_type
            LEFT JOIN org.pos_terminals t ON t.id = @terminal AND t.branch_id = b.id
            WHERE b.id = @branch
            """,
            new { branch = branchId, terminal = posTerminalId }, transaction, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new FiscalIssuerInfo(
                new FiscalParty(
                    row.IdentificationType, row.FiscalCode, row.IdentificationNumber, row.CheckDigit, row.LegalName, row.PersonType, row.TaxRegime, [],
                    row.CompanyAddress, row.CompanyMunicipality, row.Email, row.CompanyPhone),
                new FiscalEstablishment(
                    row.BranchId, row.BranchCode, row.BranchName, row.BranchAddress, row.BranchMunicipality, row.BranchPhone ?? row.CompanyPhone, row.Email,
                    row.TerminalId, row.TerminalCode));
    }

    public async Task<FiscalSaleSnapshot?> GetSaleAsync(Guid saleId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await BillingDb.OpenAsync(context, cancellationToken);
        var sale = await connection.QuerySingleOrDefaultAsync<SaleRow>(new CommandDefinition(
            """
            SELECT s.id AS Id, s.number AS Number, s.business_date AS BusinessDate, COALESCE(s.completed_at, s.opened_at) AS CompletedAt,
                   s.customer_name AS CustomerName, s.customer_identification_type AS IdentificationType, COALESCE(it.fiscal_code, '13') AS FiscalCode,
                   s.customer_identification AS Identification, s.customer_email AS Email, s.customer_fiscal::text AS Fiscal,
                   s.rounding_adjustment AS RoundingAdjustment, s.total AS Total, s.void_reason AS VoidReason
            FROM sales.sales s
            LEFT JOIN ref.identification_types it ON it.code = s.customer_identification_type
            WHERE s.id = @saleId
            """,
            new { saleId }, transaction, cancellationToken: cancellationToken));
        if (sale is null)
        {
            return null;
        }

        var lines = (await connection.QueryAsync<LineRow>(new CommandDefinition(
            """
            SELECT id AS Id, line_no AS LineNo, sku AS Code, name AS Name,
                   CASE WHEN packaging_id IS NULL THEN base_unit_code ELSE 'UND' END AS UnitCode, quantity AS Quantity, unit_price AS UnitPrice,
                   price_includes_tax AS PriceIncludesTax, gross AS Gross, promotion_discount + line_discount + global_discount_share AS Discount,
                   tax_base AS TaxBase, total AS Total
            FROM sales.sale_lines
            WHERE sale_id = @saleId AND status = 'ACTIVE'
            ORDER BY line_no
            """,
            new { saleId }, transaction, cancellationToken: cancellationToken))).ToList();
        var taxes = (await connection.QueryAsync<TaxRow>(new CommandDefinition(
            """
            SELECT t.sale_line_id AS LineId, t.code AS Code, t.kind AS Kind, t.rate AS Rate, t.fixed_amount AS FixedAmount, t.tax_base AS TaxBase,
                   t.amount AS Amount, COALESCE(x.is_exempt, false) AS IsExempt, COALESCE(x.is_excluded, false) AS IsExcluded
            FROM sales.sale_line_taxes t
            JOIN sales.sale_lines l ON l.id = t.sale_line_id
            LEFT JOIN catalog.taxes x ON x.id = t.tax_id
            WHERE l.sale_id = @saleId
            ORDER BY t.code
            """,
            new { saleId }, transaction, cancellationToken: cancellationToken))).ToLookup(t => t.LineId);
        var payments = await connection.QueryAsync<PaymentRow>(new CommandDefinition(
            """
            SELECT p.method_code AS MethodCode, p.method_kind AS MethodKind, m.dian_code AS DianCode, p.applied AS Amount, p.reference AS Reference
            FROM sales.sale_payments p
            LEFT JOIN cash.payment_methods m ON m.id = p.payment_method_id
            WHERE p.sale_id = @saleId
            ORDER BY p.line_no
            """,
            new { saleId }, transaction, cancellationToken: cancellationToken));

        var fiscal = sale.Fiscal is null ? null : JsonSerializer.Deserialize<FiscalJson>(sale.Fiscal, Json);
        var finalConsumer = sale.Identification == FiscalParty.FinalConsumerIdentification;
        var customer = new FiscalParty(
            sale.IdentificationType, sale.FiscalCode, sale.Identification, fiscal?.CheckDigit, sale.CustomerName,
            fiscal?.PersonType ?? "NATURAL", fiscal?.TaxRegime ?? "49", fiscal?.Responsibilities ?? (finalConsumer ? ["R-99-PN"] : []),
            fiscal?.Address, fiscal?.MunicipalityCode, sale.Email, fiscal?.Phone);
        return new FiscalSaleSnapshot(
            sale.Id, sale.Number ?? string.Empty, sale.BusinessDate, BillingDb.Utc(sale.CompletedAt), customer,
            [.. lines.Select(l => l.ToSource([.. taxes[l.Id].Select(t => t.ToTax())]))],
            [.. payments.Select(p => new FiscalPayment(p.MethodCode, p.MethodKind, p.DianCode, p.Amount, p.Reference))],
            sale.RoundingAdjustment, sale.Total, sale.VoidReason);
    }

    public async Task<FiscalReturnSnapshot?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await BillingDb.OpenAsync(context, cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ReturnRow>(new CommandDefinition(
            """
            SELECT r.id AS Id, r.number AS Number, r.kind AS Kind, r.reason AS Reason, r.business_date AS BusinessDate, r.credit_total AS CreditTotal,
                   m.code AS MethodCode, m.kind AS MethodKind, m.dian_code AS DianCode
            FROM sales.customer_returns r
            LEFT JOIN cash.payment_methods m ON m.id = r.refund_payment_method_id
            WHERE r.id = @returnId
            """,
            new { returnId }, transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var lines = await connection.QueryAsync<ReturnLineRow>(new CommandDefinition(
            "SELECT sale_line_id AS SaleLineId, quantity AS Quantity, credit_amount AS CreditAmount FROM sales.customer_return_lines WHERE customer_return_id = @returnId",
            new { returnId }, transaction, cancellationToken: cancellationToken));

        // Reintegro por garantía: con el medio con que salió el dinero; cambio de mercancía: el crédito se usa en la venta nueva.
        FiscalPayment payment = row.MethodCode is not null
            ? new FiscalPayment(row.MethodCode, row.MethodKind!, row.DianCode, row.CreditTotal, null)
            : new FiscalPayment("CAMBIO", "EXCHANGE_CREDIT", null, row.CreditTotal, null);
        return new FiscalReturnSnapshot(
            row.Id, row.Number ?? string.Empty, row.Kind, row.Reason, row.BusinessDate,
            [.. lines.Select(l => new FiscalReturnLine(l.SaleLineId, l.Quantity, l.CreditAmount))], [payment]);
    }

    public async Task<FiscalPurchaseSnapshot?> GetPurchaseAsync(Guid purchaseId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await BillingDb.OpenAsync(context, cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<PurchaseRow>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.number AS Number, p.supplier_invoice_number AS SupplierInvoiceNumber, p.invoice_date AS InvoiceDate,
                   p.supplier_id AS SupplierId, p.payment_mode AS PaymentMode, m.code AS MethodCode, m.kind AS MethodKind, m.dian_code AS DianCode,
                   p.payment_reference AS Reference, p.void_reason AS VoidReason
            FROM purchasing.purchases p
            LEFT JOIN cash.payment_methods m ON m.id = p.payment_method_id
            WHERE p.id = @purchaseId
            """,
            new { purchaseId }, transaction, cancellationToken: cancellationToken));
        if (row is null || await GetSupplierAsync(row.SupplierId, cancellationToken) is not { } supplier)
        {
            return null;
        }

        var lines = (await connection.QueryAsync<PurchaseLineRow>(new CommandDefinition(
            """
            SELECT l.id AS Id, l.line_number AS LineNo, pr.sku AS Code, pr.name AS Name,
                   CASE WHEN l.packaging_id IS NULL THEN pr.base_unit_code ELSE 'UND' END AS UnitCode, l.quantity AS Quantity, l.unit_cost AS UnitCost,
                   l.gross_amount AS Gross, l.discount_amount AS Discount, l.tax_amount AS TaxAmount, l.line_total AS Total
            FROM purchasing.purchase_lines l
            JOIN catalog.products pr ON pr.id = l.product_id
            WHERE l.purchase_id = @purchaseId
            ORDER BY l.line_number
            """,
            new { purchaseId }, transaction, cancellationToken: cancellationToken))).ToList();
        var taxes = (await connection.QueryAsync<PurchaseTaxRow>(new CommandDefinition(
            """
            SELECT t.purchase_line_id AS LineId, t.tax_code AS Code, t.is_vat AS IsVat, t.rate AS Rate, t.fixed_amount AS FixedAmount, t.base AS TaxBase,
                   t.amount AS Amount
            FROM purchasing.purchase_line_taxes t
            JOIN purchasing.purchase_lines l ON l.id = t.purchase_line_id
            WHERE l.purchase_id = @purchaseId
            """,
            new { purchaseId }, transaction, cancellationToken: cancellationToken))).ToLookup(t => t.LineId);
        var total = lines.Sum(l => l.Total);
        FiscalPayment payment = row.PaymentMode == "CASH" && row.MethodCode is not null
            ? new FiscalPayment(row.MethodCode, row.MethodKind!, row.DianCode, total, row.Reference)
            : new FiscalPayment("CREDITO", "CREDIT", "ZZZ", total, null);
        return new FiscalPurchaseSnapshot(
            row.Id, row.Number, row.SupplierInvoiceNumber, row.InvoiceDate, supplier,
            [.. lines.Select(l => new FiscalSourceLine(
                l.Id, l.LineNo, l.Code, l.Name, l.UnitCode, l.Quantity, Math.Round(l.UnitCost, 2), false, l.Gross, l.Discount, l.Gross - l.Discount, l.Total,
                [.. taxes[l.Id].Select(t => new FiscalTax(t.Code, t.IsVat ? "VAT" : "OTHER", t.Rate, t.FixedAmount, t.TaxBase, t.Amount, false, false))]))],
            [payment], row.VoidReason);
    }

    public async Task<FiscalParty?> GetSupplierAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await BillingDb.OpenAsync(context, cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<PartyRow>(new CommandDefinition(
            """
            SELECT p.identification_type AS IdentificationType, it.fiscal_code AS FiscalCode, p.identification_number AS IdentificationNumber,
                   p.check_digit AS CheckDigit,
                   COALESCE(NULLIF(btrim(p.legal_name), ''), btrim(COALESCE(p.first_names, '') || ' ' || COALESCE(p.last_names, ''))) AS Name,
                   p.person_type AS PersonType, p.tax_regime AS TaxRegime, p.fiscal_responsibilities AS Responsibilities, p.address AS Address,
                   p.municipality_code AS MunicipalityCode, p.email AS Email, p.phone AS Phone
            FROM purchasing.suppliers s
            JOIN parties.parties p ON p.id = s.party_id
            JOIN ref.identification_types it ON it.code = p.identification_type
            WHERE s.id = @supplierId
            """,
            new { supplierId }, transaction, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new FiscalParty(
                row.IdentificationType, row.FiscalCode, row.IdentificationNumber, row.CheckDigit, row.Name, row.PersonType, row.TaxRegime,
                [.. (row.Responsibilities ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                row.Address, row.MunicipalityCode, row.Email, row.Phone);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetIdentificationFiscalCodesAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await BillingDb.OpenAsync(context, cancellationToken);
        return (await connection.QueryAsync<(string Code, string FiscalCode)>(new CommandDefinition(
                "SELECT code, fiscal_code FROM ref.identification_types", transaction: transaction, cancellationToken: cancellationToken)))
            .ToDictionary(r => r.Code, r => r.FiscalCode, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record IssuerRow(
        string IdentificationType, string FiscalCode, string IdentificationNumber, string? CheckDigit, string LegalName, string PersonType, string TaxRegime,
        string CompanyAddress, string CompanyMunicipality, string? Email, string? CompanyPhone, Guid BranchId, string BranchCode, string BranchName,
        string BranchAddress, string BranchMunicipality, string? BranchPhone, Guid? TerminalId, string? TerminalCode);

    private sealed record SaleRow(
        Guid Id, string? Number, DateOnly BusinessDate, DateTime CompletedAt, string CustomerName, string IdentificationType, string FiscalCode,
        string Identification, string? Email, string? Fiscal, decimal RoundingAdjustment, decimal Total, string? VoidReason);

    private sealed record LineRow(
        Guid Id, int LineNo, string Code, string Name, string UnitCode, decimal Quantity, decimal UnitPrice, bool PriceIncludesTax, decimal Gross,
        decimal Discount, decimal TaxBase, decimal Total)
    {
        public FiscalSourceLine ToSource(IReadOnlyList<FiscalTax> taxes) =>
            new(Id, LineNo, Code, Name, UnitCode, Quantity, UnitPrice, PriceIncludesTax, Gross, Discount, TaxBase, Total, taxes);
    }

    private sealed record TaxRow(
        Guid LineId, string Code, string Kind, decimal? Rate, decimal? FixedAmount, decimal TaxBase, decimal Amount, bool IsExempt, bool IsExcluded)
    {
        public FiscalTax ToTax() => new(Code, Kind, Rate, FixedAmount, Math.Round(TaxBase, 2), Amount, IsExempt, IsExcluded);
    }

    private sealed record PaymentRow(string MethodCode, string MethodKind, string? DianCode, decimal Amount, string? Reference);

    private sealed record ReturnRow(
        Guid Id, string? Number, string Kind, string Reason, DateOnly BusinessDate, decimal CreditTotal, string? MethodCode, string? MethodKind, string? DianCode);

    private sealed record ReturnLineRow(Guid SaleLineId, decimal Quantity, decimal CreditAmount);

    private sealed record PurchaseRow(
        Guid Id, string Number, string SupplierInvoiceNumber, DateOnly InvoiceDate, Guid SupplierId, string PaymentMode, string? MethodCode, string? MethodKind,
        string? DianCode, string? Reference, string? VoidReason);

    private sealed record PurchaseLineRow(
        Guid Id, int LineNo, string Code, string Name, string UnitCode, decimal Quantity, decimal UnitCost, decimal Gross, decimal Discount, decimal TaxAmount,
        decimal Total);

    private sealed record PurchaseTaxRow(Guid LineId, string Code, bool IsVat, decimal? Rate, decimal? FixedAmount, decimal TaxBase, decimal Amount);

    private sealed record PartyRow(
        string IdentificationType, string FiscalCode, string IdentificationNumber, string? CheckDigit, string Name, string PersonType, string TaxRegime,
        string? Responsibilities, string? Address, string? MunicipalityCode, string? Email, string? Phone);

    private sealed record FiscalJson(
        string? PersonType, string? CheckDigit, string? TaxRegime, IReadOnlyList<string>? Responsibilities, string? Address, string? MunicipalityCode, string? Phone);
}

/// <summary>Conciliación diaria y alertas (Dapper, solo lectura).</summary>
internal sealed class FiscalReadModel(PosDbContext context) : IFiscalReadModel
{
    public async Task<IReadOnlyList<FiscalReconciliationDayDto>> ReconcileAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await BillingDb.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<ReconciliationRow>(new CommandDefinition(
            """
            WITH days AS (
                SELECT d::date AS business_date FROM generate_series(@from::date, @to::date, interval '1 day') d
            ), s AS (
                SELECT s.business_date,
                       count(*) FILTER (WHERE s.status = 'COMPLETED') AS completed,
                       count(*) FILTER (WHERE s.status = 'VOIDED') AS voided,
                       COALESCE(sum(s.total) FILTER (WHERE s.status = 'COMPLETED'), 0) AS total,
                       count(*) FILTER (WHERE NOT EXISTS (
                           SELECT 1 FROM billing.fiscal_documents f WHERE f.source = 'SALE' AND f.source_id = s.id)) AS without_document
                FROM sales.sales s
                WHERE s.branch_id = @branch AND s.status IN ('COMPLETED', 'VOIDED') AND s.business_date BETWEEN @from::date AND @to::date
                GROUP BY s.business_date
            ), f AS (
                SELECT f.business_date,
                       count(*) FILTER (WHERE f.document_type = 'INTERNAL_RECEIPT') AS internal,
                       count(*) FILTER (WHERE f.source = 'SALE' AND f.status = 'ACCEPTED') AS accepted,
                       count(*) FILTER (WHERE f.source = 'SALE' AND f.status IN ('PENDING', 'SUBMITTING')) AS pending,
                       count(*) FILTER (WHERE f.source = 'SALE' AND f.status = 'CONTINGENCY') AS contingency,
                       count(*) FILTER (WHERE f.source = 'SALE' AND f.status = 'REJECTED') AS rejected,
                       count(*) FILTER (WHERE f.source = 'SALE' AND f.status = 'ERROR') AS error,
                       count(*) FILTER (WHERE f.source = 'SALE' AND f.status = 'CANCELLED') AS cancelled,
                       count(*) FILTER (WHERE f.document_type = 'CREDIT_NOTE') AS credit_notes,
                       COALESCE(sum(f.total) FILTER (WHERE f.source = 'SALE' AND f.status = 'ACCEPTED'), 0) AS accepted_total
                FROM billing.fiscal_documents f
                WHERE f.branch_id = @branch AND f.business_date BETWEEN @from::date AND @to::date
                GROUP BY f.business_date
            )
            SELECT d.business_date AS BusinessDate, COALESCE(s.completed, 0)::int AS SalesCompleted, COALESCE(s.voided, 0)::int AS SalesVoided,
                   COALESCE(s.total, 0) AS SalesTotal, COALESCE(f.internal, 0)::int AS InternalReceipts, COALESCE(f.accepted, 0)::int AS Accepted,
                   COALESCE(f.pending, 0)::int AS Pending, COALESCE(f.contingency, 0)::int AS Contingency, COALESCE(f.rejected, 0)::int AS Rejected,
                   COALESCE(f.error, 0)::int AS Error, COALESCE(f.cancelled, 0)::int AS Cancelled, COALESCE(f.credit_notes, 0)::int AS CreditNotes,
                   COALESCE(s.without_document, 0)::int AS SalesWithoutDocument, COALESCE(f.accepted_total, 0) AS AcceptedTotal
            FROM days d
            LEFT JOIN s ON s.business_date = d.business_date
            LEFT JOIN f ON f.business_date = d.business_date
            ORDER BY d.business_date
            """,
            new { branch = branchId, from = BillingDb.Text(from), to = BillingDb.Text(to) }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new FiscalReconciliationDayDto(
            r.BusinessDate, r.SalesCompleted, r.SalesVoided, r.SalesTotal, r.InternalReceipts, r.Accepted, r.Pending, r.Contingency, r.Rejected,
            r.Error, r.Cancelled, r.CreditNotes, r.SalesWithoutDocument, r.AcceptedTotal))];
    }

    public async Task<FiscalAttention> AttentionAsync(Guid companyId, DateTimeOffset overdueBefore, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await BillingDb.OpenAsync(context, cancellationToken);
        var row = await connection.QuerySingleAsync<AttentionRow>(new CommandDefinition(
            """
            SELECT count(*) FILTER (WHERE d.status IN ('PENDING', 'ERROR', 'CONTINGENCY', 'SUBMITTING') AND d.issued_at < @before)::int AS PendingOverdue,
                   count(*) FILTER (WHERE d.status = 'REJECTED')::int AS Rejected,
                   count(*) FILTER (WHERE d.status = 'PENDING' AND d.numbering_range_id IS NULL AND EXISTS (
                       SELECT 1 FROM billing.fiscal_document_events e WHERE e.fiscal_document_id = d.id AND e.event_type = 'NO_RANGE'))::int AS WithoutRange,
                   min(d.issued_at) FILTER (WHERE d.status IN ('PENDING', 'ERROR', 'CONTINGENCY', 'SUBMITTING')) AS OldestPendingAt
            FROM billing.fiscal_documents d
            WHERE d.company_id = @company
              AND d.status IN ('PENDING', 'ERROR', 'CONTINGENCY', 'SUBMITTING', 'REJECTED')
            """,
            new { company = companyId, before = overdueBefore.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        return new FiscalAttention(row.PendingOverdue, row.Rejected, row.WithoutRange, row.OldestPendingAt is { } oldest ? BillingDb.Utc(oldest) : null);
    }

    private sealed record ReconciliationRow(
        DateOnly BusinessDate, int SalesCompleted, int SalesVoided, decimal SalesTotal, int InternalReceipts, int Accepted, int Pending, int Contingency,
        int Rejected, int Error, int Cancelled, int CreditNotes, int SalesWithoutDocument, decimal AcceptedTotal);

    private sealed record AttentionRow(int PendingOverdue, int Rejected, int WithoutRange, DateTime? OldestPendingAt);
}

/// <summary>
/// Espera del tiquete (D11B-03): consulta el documento con lecturas CONFIRMADAS (conexión propia) cada 100 ms hasta que tenga número
/// fiscal y CUFE, quede en un estado final o se acabe el tiempo.
/// </summary>
internal sealed class FiscalDataWaiter(NpgsqlDataSource dataSource, TimeProvider time) : IFiscalDataWaiter
{
    public async Task<FiscalDocumentInfo?> WaitAsync(Guid documentId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (true)
        {
            FiscalDocumentInfo? info;
            await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
            {
                info = await connection.QuerySingleOrDefaultAsync<FiscalDocumentInfo>(new CommandDefinition(
                    """
                    SELECT id AS Id, document_type AS DocumentType, status AS Status, fiscal_number AS FiscalNumber, cufe AS Cufe, qr_data AS QrData
                    FROM billing.fiscal_documents WHERE id = @documentId
                    """,
                    new { documentId }, cancellationToken: cancellationToken));
            }

            if (info is null || info.FiscalNumber is not null || info.Status is not ("PENDING" or "SUBMITTING" or "ERROR" or "CONTINGENCY")
                || time.GetUtcNow() >= deadline)
            {
                return info;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), time, cancellationToken);
        }
    }
}

/// <summary>Mensaje LOCAL del outbox: la transacción del documento ya se confirmó, se despierta la cola (D7-12).</summary>
internal sealed class FiscalDocumentPendingHandler(FiscalQueueSignal signal) : IOutboxMessageHandler
{
    public string MessageType => BillingService.PendingMessage;

    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        signal.Notify();
        return Task.CompletedTask;
    }
}

/// <summary>Configuración <c>Pos:Billing</c>.</summary>
public sealed class BillingOptions
{
    /// <summary>Adaptador: NONE (por defecto: facturación electrónica apagada), FAKE (solo fuera de producción) o FACTUS (Factus API v2).</summary>
    public string Provider { get; set; } = "NONE";

    /// <summary>Si el proceso en segundo plano envía la cola (las pruebas lo apagan para dirigirla a mano).</summary>
    public bool Worker { get; set; } = true;

    /// <summary>Ciclo de la cola si nadie la despierta (⚙️ 5 s).</summary>
    public int QueueIntervalSeconds { get; set; } = 5;

    /// <summary>Resincronización automática de los rangos (⚙️ cada 24 h).</summary>
    public int RangeSyncHours { get; set; } = 24;
}

/// <summary>
/// Cola de envío (D11B-02/04/09): una pasada a la vez; toma los documentos vencidos en orden de llegada, respeta el ritmo por minuto,
/// procesa cada uno en su propio alcance (uno que falle no detiene a los demás) y se detiene al primer "sin conexión" (contingencia:
/// se reintenta en el siguiente ciclo, sin martillar al proveedor).
/// </summary>
public sealed partial class FiscalQueueRunner(IServiceScopeFactory scopes, FiscalRateLimiter limiter, ILogger<FiscalQueueRunner> logger) : IDisposable
{
    private const int Batch = 20;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<FiscalQueueRun> ProcessDueAsync(int maxDocuments, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return new FiscalQueueRun(0, 0, 0, "BUSY");
        }

        try
        {
            FiscalConnection? connection;
            int rate;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var installation = scope.ServiceProvider.GetRequiredService<IInstallationContext>();
                if (installation.CompanyId is not { } companyId)
                {
                    return new FiscalQueueRun(0, 0, 0, "SETUP_REQUIRED");
                }

                connection = await scope.ServiceProvider.GetRequiredService<FiscalConnectionFactory>().GetAsync(cancellationToken);
                rate = await scope.ServiceProvider.GetRequiredService<ISettingsReader>()
                    .GetAsync(BillingSettings.RatePerMinute, new SettingContext(companyId), cancellationToken);
            }

            if (connection is null)
            {
                return new FiscalQueueRun(0, 0, 0, "NOT_CONFIGURED");
            }

            int processed = 0, sent = 0, accepted = 0;
            var seen = new HashSet<Guid>();
            while (processed < maxDocuments)
            {
                IReadOnlyList<Guid> due;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    due = await scope.ServiceProvider.GetRequiredService<IFiscalDocumentStore>()
                        .ListDueAsync(scope.ServiceProvider.GetRequiredService<IClock>().UtcNow, Math.Min(Batch, maxDocuments - processed), cancellationToken);
                }

                var fresh = due.Where(seen.Add).ToList();
                if (fresh.Count == 0)
                {
                    break;
                }

                foreach (var id in fresh)
                {
                    if (!limiter.TryAcquire(rate))
                    {
                        return new FiscalQueueRun(processed, sent, accepted, "RATE_LIMIT");
                    }

                    var outcome = await ProcessOneAsync(id, connection, cancellationToken);
                    processed++;
                    sent += outcome is null ? 0 : 1;
                    accepted += outcome == FiscalOutcome.Accepted ? 1 : 0;
                    if (outcome == FiscalOutcome.Unavailable)
                    {
                        return new FiscalQueueRun(processed, sent, accepted, "CONTINGENCY");
                    }
                }
            }

            return new FiscalQueueRun(processed, sent, accepted, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<FiscalOutcome?> ProcessOneAsync(Guid id, FiscalConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<FiscalDocumentProcessor>().ProcessAsync(id, connection, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Concurrencia (una anulación o corrección simultánea) o falla de BD: el documento se retoma en el siguiente ciclo.
            LogDocumentFailed(logger, id, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo procesar el documento fiscal {DocumentId}; se reintentará")]
    private static partial void LogDocumentFailed(ILogger logger, Guid documentId, Exception exception);
}

/// <summary>Proceso en segundo plano: envía la cola al despertar (venta nueva, reintento, tiquete) o cada ⚙️ 5 s y resincroniza los rangos cada 24 h.</summary>
internal sealed partial class FiscalQueueWorker(
    FiscalQueueRunner runner,
    FiscalQueueSignal signal,
    IServiceScopeFactory scopes,
    DatabaseReadiness readiness,
    IInstallationContext installation,
    BillingOptions options,
    TimeProvider time,
    ILogger<FiscalQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (options.Worker && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(options.QueueIntervalSeconds, 1, 3600)), stoppingToken);
                if (!readiness.IsReady || !installation.IsSetupCompleted)
                {
                    continue;
                }

                await runner.ProcessDueAsync(maxDocuments: 500, stoppingToken);
                await SyncRangesIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    private async Task SyncRangesIfDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (installation.CompanyId is not { } companyId
            || await scope.ServiceProvider.GetRequiredService<IBillingSettingsStore>().GetAsync(companyId, cancellationToken) is not
                { Mode: not BillingMode.Off, HasCredentials: true } configured
            || (configured.LastSyncAt is { } last && time.GetUtcNow() - last < TimeSpan.FromHours(Math.Max(1, options.RangeSyncHours))))
        {
            return;
        }

        await scope.ServiceProvider.GetRequiredService<FiscalRangeService>().SyncAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el ciclo de la cola de facturación electrónica.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

public static class BillingInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        // La configuración se lee al resolver (no al registrar): así la ven también los ajustes que agregan las pruebas.
        services.AddSingleton(sp => Read(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IModelContributor, BillingModelContributor>();
        services.AddScoped<IFiscalDocumentStore, FiscalDocumentStore>();
        services.AddScoped<IFiscalRangeStore, FiscalRangeStore>();
        services.AddScoped<IBillingSettingsStore, BillingSettingsStore>();
        services.AddScoped<IFiscalSourceReader, FiscalSourceReader>();
        services.AddScoped<IFiscalReadModel, FiscalReadModel>();
        services.AddSingleton<IFiscalCredentialProtector, DpapiFiscalCredentialProtector>();
        services.AddSingleton<IFiscalDataWaiter, FiscalDataWaiter>();
        services.AddScoped<IOutboxMessageHandler, FiscalDocumentPendingHandler>();
        services.AddSingleton<FiscalQueueSignal>();
        services.AddSingleton(sp => new FiscalRateLimiter(sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<FiscalQueueRunner>();
        services.AddScoped<FiscalConnectionFactory>();
        services.AddScoped<FiscalDocumentProcessor>();
        services.AddScoped<FiscalRangeService>();

        // Adaptador del proveedor: el nulo por defecto (fase apagada); el simulado solo fuera de producción; Factus con Provider = FACTUS.
        services.AddSingleton<NullFiscalProvider>();
        services.AddSingleton<FakeFiscalProvider>();
        services.AddFactusApi();
        services.Replace(ServiceDescriptor.Singleton(sp => FactusConnection(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>())));
        services.AddSingleton<FactusFiscalProvider>();
        services.AddSingleton<IFiscalProvider>(sp => sp.GetRequiredService<BillingOptions>().Provider switch
        {
            "FAKE" when !sp.GetRequiredService<IHostEnvironment>().IsProduction() => sp.GetRequiredService<FakeFiscalProvider>(),
            FactusFiscalProvider.ProviderName => sp.GetRequiredService<FactusFiscalProvider>(),
            _ => sp.GetRequiredService<NullFiscalProvider>(),
        });
        services.AddHostedService<FiscalQueueWorker>();
    }

    /// <summary>
    /// <c>Pos:Billing:Factus</c>: <c>TimeoutSeconds</c> (⚙️ 60) y <c>BaseUrl</c> — URL explícita solo FUERA de producción (Factus simulado
    /// en las pruebas); en producción la URL sale siempre del ambiente de la empresa (sandbox o producción de Factus).
    /// </summary>
    private static FactusConnectionOptions FactusConnection(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("Pos:Billing:Factus");
        return new FactusConnectionOptions
        {
            BaseUrl = !environment.IsProduction() && Uri.TryCreate(section["BaseUrl"], UriKind.Absolute, out var url) ? url : null,
            RequestTimeout = TimeSpan.FromSeconds(int.TryParse(section["TimeoutSeconds"], out var seconds) ? Math.Clamp(seconds, 1, 300) : 60),
        };
    }

    private static BillingOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Pos:Billing");
        return new BillingOptions
        {
            Provider = section["Provider"] is { Length: > 0 } provider ? provider.Trim().ToUpperInvariant() : "NONE",
            Worker = !bool.TryParse(section["Worker"], out var worker) || worker,
            QueueIntervalSeconds = int.TryParse(section["QueueIntervalSeconds"], out var interval) ? interval : 5,
            RangeSyncHours = int.TryParse(section["RangeSyncHours"], out var hours) ? hours : 24,
        };
    }
}
