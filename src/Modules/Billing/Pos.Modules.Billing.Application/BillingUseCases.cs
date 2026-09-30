using System.Text.Json;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Billing.Application;

// ─────────────────────────────── Puertos ───────────────────────────────

/// <summary>Documentos fiscales (EF Core).</summary>
public interface IFiscalDocumentStore
{
    void Add(FiscalDocument document);

    Task<FiscalDocument?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<FiscalDocument?> GetBySourceAsync(Guid sourceId, string source, CancellationToken cancellationToken);

    Task<IReadOnlyList<FiscalDocument>> ListAsync(DocumentFilter filter, CancellationToken cancellationToken);

    /// <summary>Ids de los documentos que la cola debe enviar ya, en orden de llegada.</summary>
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

public sealed record DocumentFilter(
    Guid BranchId, DateOnly? From, DateOnly? To, FiscalStatus? Status, int Limit, FiscalDocumentType? Type = null, FiscalSource? Source = null, string? Search = null);

/// <summary>Rangos de numeración (EF Core).</summary>
public interface IFiscalRangeStore
{
    void Add(FiscalNumberingRange range);

    Task<FiscalNumberingRange?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<FiscalNumberingRange>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Configuración del proveedor por empresa (EF Core).</summary>
public interface IBillingSettingsStore
{
    void Add(BillingProviderSettings settings);

    Task<BillingProviderSettings?> GetAsync(Guid companyId, CancellationToken cancellationToken);
}

/// <summary>Cifra y descifra las credenciales del proveedor (DPAPI de la máquina, como los secretos de los backups).</summary>
public interface IFiscalCredentialProtector
{
    byte[] Protect(FiscalCredentials credentials);

    /// <summary>Descifra; <c>null</c> si no se puede (p. ej. se restauró la BD en otro equipo: hay que volver a ingresarlas).</summary>
    FiscalCredentials? Unprotect(byte[] protectedBytes);
}

/// <summary>Lee los documentos de origen GUARDADOS (venta, cambio, compra) y los datos del emisor para armar el modelo fiscal neutro.</summary>
public interface IFiscalSourceReader
{
    Task<FiscalIssuerInfo?> GetIssuerAsync(Guid branchId, Guid? posTerminalId, CancellationToken cancellationToken);

    Task<FiscalSaleSnapshot?> GetSaleAsync(Guid saleId, CancellationToken cancellationToken);

    Task<FiscalReturnSnapshot?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken);

    Task<FiscalPurchaseSnapshot?> GetPurchaseAsync(Guid purchaseId, CancellationToken cancellationToken);

    Task<FiscalParty?> GetSupplierAsync(Guid supplierId, CancellationToken cancellationToken);

    /// <summary>Código DIAN de cada tipo de identificación (CC → 13, NIT → 31…).</summary>
    Task<IReadOnlyDictionary<string, string>> GetIdentificationFiscalCodesAsync(CancellationToken cancellationToken);
}

public sealed record FiscalIssuerInfo(FiscalParty Issuer, FiscalEstablishment Establishment);

/// <summary>Consultas de conciliación y alertas (Dapper).</summary>
public interface IFiscalReadModel
{
    Task<IReadOnlyList<FiscalReconciliationDayDto>> ReconcileAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<FiscalAttention> AttentionAsync(Guid companyId, DateTimeOffset overdueBefore, CancellationToken cancellationToken);
}

public sealed record FiscalAttention(int PendingOverdue, int Rejected, int WithoutRange, DateTimeOffset? OldestPendingAt);

/// <summary>Espera, con lecturas confirmadas, a que un documento tenga sus datos fiscales (tiquete, D11B-03).</summary>
public interface IFiscalDataWaiter
{
    Task<FiscalDocumentInfo?> WaitAsync(Guid documentId, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Aviso a la cola de envío para que no espere al siguiente ciclo (venta nueva, reintento, tiquete esperando). Varias señales
/// seguidas equivalen a una.
/// </summary>
public sealed class FiscalQueueSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Ya estaba avisada.
        }
    }

    /// <summary>Espera un aviso o el tiempo indicado; devuelve si hubo aviso.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

// ─────────────────────────────── Configuración y permisos ───────────────────────────────

/// <summary>Configuraciones de facturación. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class BillingSettings
{
    /// <summary>
    /// Fase 7 (pregunta 1). Desde la Fase 11-B el modo se configura en <c>billing.provider_settings</c> (D11B-01); esta clave solo se
    /// respeta si la empresa aún no tiene configuración del proveedor (verdadero = EVERY_SALE).
    /// </summary>
    public static readonly SettingDefinition<bool> ElectronicEnabled = new(
        "billing.electronic_enabled", false, SettingScope.Company | SettingScope.Branch,
        "Emitir documento equivalente electrónico POS con el proveedor (Factus, Fase 11-B). Si no, cada venta lleva un comprobante interno.");

    public static readonly SettingDefinition<int> RatePerMinute = new(
        "billing.rate_per_minute", 60, SettingScope.Company,
        "Máximo de documentos enviados al proveedor por minuto (Factus permite 80 por NIT; D11B-09).",
        v => v is >= 1 and <= 600 ? null : "Entre 1 y 600 envíos por minuto.", BillingPermissions.SettingsManage);

    public static readonly SettingDefinition<int> PendingAlertHours = new(
        "billing.pending_alert_hours", 24, SettingScope.Company,
        "Alertar si hay documentos electrónicos pendientes o en contingencia hace más de estas horas (D11B-04).",
        v => v is >= 1 and <= 720 ? null : "Entre 1 y 720 horas.", BillingPermissions.SettingsManage);

    public static readonly SettingDefinition<int> TicketWaitSeconds = new(
        "billing.ticket_wait_seconds", 3, SettingScope.Company | SettingScope.Branch | SettingScope.Terminal,
        "Segundos que la caja espera el número fiscal, el CUFE y el QR antes de imprimir el tiquete (D11B-03).",
        v => v is >= 0 and <= 10 ? null : "Entre 0 y 10 segundos.", BillingPermissions.SettingsManage);

    public static readonly SettingDefinition<int> RangeAlertPercent = new(
        "billing.range_alert_percent", 90, SettingScope.Company,
        "Alertar cuando un rango de numeración supere este porcentaje de uso (D11B-05).",
        v => v is >= 50 and <= 99 ? null : "Entre 50 y 99 %.", BillingPermissions.SettingsManage);

    public static readonly SettingDefinition<int> RangeAlertDays = new(
        "billing.range_alert_days", 30, SettingScope.Company,
        "Alertar cuando falten estos días o menos para el vencimiento de un rango de numeración (D11B-05).",
        v => v is >= 1 and <= 180 ? null : "Entre 1 y 180 días.", BillingPermissions.SettingsManage);

    public static IEnumerable<SettingDefinition> All => [ElectronicEnabled, RatePerMinute, PendingAlertHours, TicketWaitSeconds, RangeAlertPercent, RangeAlertDays];
}

public sealed class BillingSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => BillingSettings.All;
}

public sealed class BillingPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => BillingPermissions.All;
}

// ─────────────────────────────── Mapeo ───────────────────────────────

public static class BillingMapping
{
    public static string Db<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }

    /// <summary>Lee un valor en MAYÚSCULAS_CON_GUION (o en PascalCase) al enum; <c>null</c> si no existe.</summary>
    public static TEnum? Parse<TEnum>(string? value)
        where TEnum : struct, Enum =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Enum.GetValues<TEnum>().Cast<TEnum?>().FirstOrDefault(v =>
                string.Equals(v!.Value.Db(), value.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(v.Value.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    public static FiscalDocumentInfo ToInfo(this FiscalDocument d) => new(d.Id, d.DocumentType.Db(), d.Status.Db(), d.FiscalNumber, d.Cufe, d.QrData);

    public static FiscalDocumentDto ToDto(this FiscalDocument d) => new(
        d.Id, d.Source.Db(), d.SourceId, d.SourceNumber, d.DocumentType.Db(), d.Status.Db(), d.Provider, d.FiscalNumber, d.Cufe, d.Attempts, d.BusinessDate,
        d.BuyerName, d.BuyerIdentification, d.Subtotal, d.TaxTotal, d.Total, d.IssuedAt,
        [.. d.Events.OrderBy(e => e.OccurredAt).Select(e => new FiscalDocumentEventDto(e.EventType, e.Detail, e.ProviderCode, e.OccurredAt))],
        d.ReferenceCode, d.BuyerIdentificationType, d.BuyerEmail, d.QrData, d.PdfUrl, d.NumberingRangeId, d.ProviderDocumentId, d.ProviderStatus,
        d.RejectionMessage, d.ValidatedAt, d.NextAttemptAt, d.RelatedDocumentId, d.BranchId, d.PosTerminalId);

    public static FiscalRangeDto ToDto(this FiscalNumberingRange r, DateOnly today, int alertPercent, int alertDays) => new(
        r.Id, r.Provider, r.ProviderRangeId, r.DocumentType.Db(), r.Prefix, r.RangeFrom, r.RangeTo, r.CurrentNumber, r.Remaining, r.UsagePercent,
        r.ResolutionNumber, r.ValidFrom, r.ValidTo, r.IsActive, r.IsUsableOn(today), r.NeedsAlert(today, alertPercent, alertDays), r.BranchId,
        r.PosTerminalId, r.SyncedAt);

    public static FiscalSource Source(string source) => source switch
    {
        "SALE" => FiscalSource.Sale,
        "SALE_VOID" => FiscalSource.SaleVoid,
        "CUSTOMER_RETURN" => FiscalSource.CustomerReturn,
        "PURCHASE" => FiscalSource.Purchase,
        "PURCHASE_VOID" => FiscalSource.PurchaseVoid,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Origen de documento desconocido."),
    };
}

// ─────────────────────────────── Emisión (en la transacción del origen) ───────────────────────────────

/// <summary>
/// Emisión en la transacción de la venta (D7-12, D11B-02): en modo OFF comprobante interno; con la facturación electrónica,
/// documento PENDING y un mensaje LOCAL del outbox que despierta la cola cuando la transacción se confirma. La venta nunca espera.
/// </summary>
public sealed class BillingService(
    IFiscalDocumentStore store,
    IBillingSettingsStore providerSettings,
    IFiscalSourceReader sources,
    IFiscalDataWaiter waiter,
    FiscalQueueSignal signal,
    ISettingsReader settings,
    IOutbox outbox,
    IActorContext actor,
    IInstallationContext installation,
    IIdGenerator ids,
    IClock clock)
    : IBillingService
{
    public const string PendingMessage = "billing.document_pending.v1";

    /// <summary>Modo vigente de la empresa (la clave antigua solo cuenta si aún no hay configuración del proveedor).</summary>
    public static async Task<BillingMode> ModeAsync(
        IBillingSettingsStore store, ISettingsReader settings, Guid companyId, Guid? branchId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        if (await store.GetAsync(companyId, cancellationToken) is { } configured)
        {
            return configured.Mode;
        }

        return await settings.GetAsync(BillingSettings.ElectronicEnabled, new SettingContext(companyId, branchId), cancellationToken)
            ? BillingMode.EverySale
            : BillingMode.Off;
    }

    public async Task<Result<FiscalDocumentInfo>> IssueAsync(FiscalIssueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var companyId = installation.CompanyId!.Value;
        var source = BillingMapping.Source(request.Source);
        if (source is FiscalSource.Purchase or FiscalSource.PurchaseVoid)
        {
            return Error.Validation(
                "BILLING.INVALID_SOURCE", "El documento soporte de una compra y su nota de ajuste se emiten con IssueSupportDocumentAsync y VoidSupportDocumentAsync.");
        }

        FiscalDocument? related = request.RelatedSourceId is { } relatedSource ? await store.GetBySourceAsync(relatedSource, "SALE", cancellationToken) : null;
        bool electronic;
        if (source == FiscalSource.Sale)
        {
            var mode = await ModeAsync(providerSettings, settings, companyId, request.BranchId, cancellationToken);
            electronic = BillingProviderSettings.IsElectronicSale(mode, request.InvoiceRequested);
        }
        else
        {
            // Una nota crédito solo es electrónica si corrige una factura electrónica vigente (D11B-07).
            electronic = related is { IsElectronic: true, Status: not (FiscalStatus.Cancelled or FiscalStatus.Voided) };
        }

        var document = FiscalDocument.Issue(
            ids.NewId(), companyId,
            new FiscalIssue(
                source, request.SourceId, request.SourceNumber, request.BranchId, request.PosTerminalId, request.BusinessDate,
                request.BuyerName, request.BuyerIdentificationType, request.BuyerIdentification, request.BuyerEmail, request.Subtotal, request.TaxTotal, request.Total,
                related?.Id),
            electronic, clock.UtcNow, actor.ActorId, ids.NewId);
        store.Add(document);
        Enqueue(document);
        return document.ToInfo();
    }

    public async Task<Result<FiscalDocumentInfo?>> IssueSupportDocumentAsync(FiscalSupportDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var companyId = installation.CompanyId!.Value;
        if (await ModeAsync(providerSettings, settings, companyId, request.BranchId, cancellationToken) == BillingMode.Off)
        {
            return Result.Success<FiscalDocumentInfo?>(null);
        }

        if (await store.GetBySourceAsync(request.PurchaseId, "PURCHASE", cancellationToken) is { } existing)
        {
            return existing.ToInfo();
        }

        var supplier = await sources.GetSupplierAsync(request.SupplierId, cancellationToken);
        if (supplier is null)
        {
            return Error.NotFound("BILLING.SUPPLIER_NOT_FOUND", "No se encontraron los datos del proveedor para el documento soporte.");
        }

        var document = FiscalDocument.Issue(
            ids.NewId(), companyId,
            new FiscalIssue(
                FiscalSource.Purchase, request.PurchaseId, request.PurchaseNumber, request.BranchId, null, request.BusinessDate, Truncate(supplier.Name, 200),
                supplier.IdentificationType, supplier.IdentificationNumber, supplier.Email, request.Subtotal, request.TaxTotal, request.Total),
            electronic: true, clock.UtcNow, actor.ActorId, ids.NewId);
        store.Add(document);
        Enqueue(document);
        return document.ToInfo();
    }

    public async Task<Result<FiscalDocumentInfo?>> VoidForSourceAsync(Guid sourceId, string reason, CancellationToken cancellationToken = default)
    {
        var document = await store.GetBySourceAsync(sourceId, "SALE", cancellationToken);
        if (document is null)
        {
            return Result.Success<FiscalDocumentInfo?>(null);
        }

        // Interno → anulado; factura aún no aceptada → cancelada antes de enviarla (D11B-07).
        if (document.Void(reason, clock.UtcNow, actor.ActorId, ids.NewId))
        {
            return document.ToInfo();
        }

        // Factura aceptada o en envío: la anulación es una nota crédito (se envía en segundo plano cuando la factura esté aceptada).
        var credit = FiscalDocument.Issue(
            ids.NewId(), document.CompanyId,
            new FiscalIssue(
                FiscalSource.SaleVoid, sourceId, document.SourceNumber, document.BranchId, document.PosTerminalId, clock.Today, document.BuyerName,
                document.BuyerIdentificationType, document.BuyerIdentification, document.BuyerEmail, document.Subtotal, document.TaxTotal, document.Total, document.Id),
            electronic: true, clock.UtcNow, actor.ActorId, ids.NewId);
        store.Add(credit);
        Enqueue(credit);
        return credit.ToInfo();
    }

    public async Task<Result<FiscalDocumentInfo?>> VoidSupportDocumentAsync(Guid purchaseId, string reason, CancellationToken cancellationToken = default)
    {
        var support = await store.GetBySourceAsync(purchaseId, "PURCHASE", cancellationToken);
        if (support is null)
        {
            return Result.Success<FiscalDocumentInfo?>(null);
        }

        // Aún no aceptado (pendiente, con error, en contingencia o rechazado): se cancela antes de enviarlo; no hay nada que ajustar.
        if (support.Void(reason, clock.UtcNow, actor.ActorId, ids.NewId))
        {
            return support.ToInfo();
        }

        if (await store.GetBySourceAsync(purchaseId, "PURCHASE_VOID", cancellationToken) is { } existing)
        {
            return existing.ToInfo();
        }

        // Aceptado (o en envío): nota de ajuste de anulación; sale cuando el documento soporte esté aceptado.
        var note = FiscalDocument.Issue(
            ids.NewId(), support.CompanyId,
            new FiscalIssue(
                FiscalSource.PurchaseVoid, purchaseId, support.SourceNumber, support.BranchId, null, clock.Today, support.BuyerName,
                support.BuyerIdentificationType, support.BuyerIdentification, support.BuyerEmail, support.Subtotal, support.TaxTotal, support.Total, support.Id),
            electronic: true, clock.UtcNow, actor.ActorId, ids.NewId);
        store.Add(note);
        Enqueue(note);
        return note.ToInfo();
    }

    public async Task<FiscalDocumentInfo?> GetForSourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        (await store.GetBySourceAsync(sourceId, "SALE", cancellationToken))?.ToInfo();

    public async Task<FiscalDocumentInfo?> GetBySourceAsync(Guid sourceId, string source, CancellationToken cancellationToken = default) =>
        (await store.GetBySourceAsync(sourceId, source, cancellationToken))?.ToInfo();

    public async Task<FiscalDocumentInfo?> WaitForFiscalDataAsync(Guid documentId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(installation.CompanyId is { } company
            ? await settings.GetAsync(BillingSettings.TicketWaitSeconds, new SettingContext(company, installation.BranchId), cancellationToken)
            : 3);
        signal.Notify();
        return await waiter.WaitAsync(documentId, wait < TimeSpan.Zero ? TimeSpan.Zero : wait, cancellationToken);
    }

    private void Enqueue(FiscalDocument document)
    {
        if (document.Status == FiscalStatus.Pending)
        {
            outbox.Enqueue(PendingMessage, new { DocumentId = document.Id }, OutboxDestination.Local);
        }
    }

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}

/// <summary>Payload del mensaje LOCAL del outbox.</summary>
public static class FiscalOutboxPayload
{
    public static Guid DocumentIdOf(JsonElement payload) => payload.GetProperty("documentId").GetGuid();
}
