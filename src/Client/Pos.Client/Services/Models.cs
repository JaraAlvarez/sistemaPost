using System.Text.Json;

namespace Pos.Client.Services;

// Modelos de la interfaz: solo los campos que las pantallas usan (el JSON trae más; se ignoran). No se comparten ensamblados con el
// servidor: la interfaz depende del contrato HTTP, no de las capas internas (doc 03).

public sealed record Notice(string Code, string Severity, string Text);

public sealed record LicenseSummary(string State, int? DaysLeft, IReadOnlyList<Notice> Notices);

public sealed record MeInfo(
    Guid UserId,
    string Username,
    string DisplayName,
    Guid SessionId,
    string SessionKind,
    Guid BranchId,
    Guid? PosTerminalId,
    bool MustChangePassword,
    IReadOnlyList<string> Permissions,
    int OpenIntegrityIncidents,
    int BackupAlerts,
    LicenseSummary? License);

public sealed record LoginResult(string Token, DateTimeOffset ExpiresAt, int IdleTimeoutSeconds, MeInfo User);

public sealed record SetupStatus(bool IsCompleted, string Edition, Guid NodeId, Guid? CompanyId, Guid? BranchId, bool OwnerPending);

public sealed record DeviceCredential(Guid DeviceId, string DeviceSecret, string DeviceKind, Guid? PosTerminalId);

public sealed record SaleLine(
    Guid Id, int LineNo, Guid ProductId, string Sku, string Name, decimal Quantity, decimal UnitPrice, decimal Gross, string? PromotionName,
    decimal PromotionDiscount, decimal LineDiscount, decimal Total, string Status);

public sealed record SalePayment(Guid Id, Guid PaymentMethodId, string MethodCode, decimal Tendered, decimal Applied, decimal Change, string? Reference);

public sealed record SaleCustomer(Guid? PartyId, string Name, string IdentificationType, string Identification);

public sealed record Sale(
    Guid Id, string? Number, string Status, SaleCustomer Customer, string? HoldLabel, decimal Gross, decimal PromotionTotal, decimal DiscountTotal,
    decimal Subtotal, decimal TaxTotal, decimal RoundingAdjustment, decimal Total, decimal AmountDue, decimal PaidTotal, decimal ChangeTotal,
    IReadOnlyList<SaleLine> Lines, IReadOnlyList<SalePayment> Payments, IReadOnlyList<string> Warnings)
{
    public IEnumerable<SaleLine> ActiveLines => Lines.Where(l => l.Status == "ACTIVE");
}

public sealed record SaleSummary(Guid Id, string? Number, string Status, decimal Total, DateTimeOffset OpenedAt, string? HoldLabel, string CustomerName);

public sealed record Receipt(Sale Sale, JsonElement Ticket, string TicketText, bool OpenDrawer);

public sealed record PaymentMethod(Guid Id, string Code, string Name, string Kind, bool RequiresReference, bool AffectsCashDrawer, int SortOrder, string Status);

public sealed record Denomination(Guid Id, decimal Value, string Kind, int SortOrder, string Status);

public sealed record CashMethodTotal(Guid PaymentMethodId, string Code, string Name, bool AffectsCashDrawer, decimal? Expected, decimal? Counted, decimal? Difference, int Transactions);

public sealed record CashSession(
    Guid Id, string Number, Guid PosTerminalId, string TerminalCode, string CashierName, DateOnly BusinessDate, DateTimeOffset OpenedAt, decimal OpeningFloat,
    string Status, bool BlindCount, bool ExpectedHidden, decimal? ExpectedTotal, decimal? CountedTotal, decimal? Difference, IReadOnlyList<CashMethodTotal> Totals);

public sealed record CashReport(string Kind, string Number, decimal ExpectedTotal, decimal? CountedTotal, decimal? Difference, long? SealNo, string? SealCode, string Text);

public sealed record ReportParameter(string Name, string Label, string Type, bool Required, string? DefaultValue);

public sealed record ReportColumn(string Key, string Label, string Type, bool HasTotal);

public sealed record ReportInfo(string Code, string Name, string Group, string Description, IReadOnlyList<ReportParameter> Parameters, IReadOnlyList<ReportColumn> Columns);

public sealed record ReportResult(
    string Code, string Name, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<Dictionary<string, JsonElement>> Rows, Dictionary<string, JsonElement> Totals,
    int TotalRows, int Page, int PageSize, bool Truncated, string? Note);
