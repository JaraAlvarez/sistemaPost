using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Billing.Application;

/// <summary>Conexión con el proveedor: ambiente y credenciales descifradas (solo en memoria, solo para la llamada).</summary>
public sealed class FiscalConnectionFactory(IBillingSettingsStore store, IFiscalCredentialProtector protector, IInstallationContext installation)
{
    public async Task<FiscalConnection?> GetAsync(CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId || await store.GetAsync(companyId, cancellationToken) is not { HasCredentials: true } settings)
        {
            return null;
        }

        return protector.Unprotect(settings.Credentials!) is { } credentials ? new FiscalConnection(settings.Environment, credentials) : null;
    }
}

/// <summary>
/// Envía UN documento de la cola (D11B-02/04/05/09/10): lo reclama, elige el rango vigente de su sucursal/caja, arma el modelo fiscal
/// desde el documento guardado, llama al proveedor y registra la respuesta. Sin rango → queda pendiente con alerta CRÍTICA; rechazo →
/// REJECTED con alerta CRÍTICA; sin conexión → contingencia; error → reintento con espera creciente. Una nota crédito espera a que
/// su factura esté aceptada. Cada documento se guarda por separado (la cola sigue aunque uno falle).
/// </summary>
public sealed class FiscalDocumentProcessor(
    IFiscalDocumentStore documents,
    IFiscalRangeStore ranges,
    IFiscalSourceReader sources,
    IFiscalProvider provider,
    ISettingsReader settings,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    IIdGenerator ids,
    IClock clock)
{
    /// <summary>Tiempo durante el cual un documento reclamado no lo toma nadie más (si el proceso se cae, se retoma después).</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    /// <summary>Espera antes de volver a buscar rango o la aceptación de la factura relacionada.</summary>
    public static readonly TimeSpan DeferDelay = TimeSpan.FromMinutes(15);

    private IReadOnlyDictionary<string, string>? _codes;

    /// <summary>Procesa el documento; devuelve el resultado del proveedor o <c>null</c> si no se envió.</summary>
    public async Task<FiscalOutcome?> ProcessAsync(Guid documentId, FiscalConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var document = await documents.GetAsync(documentId, cancellationToken);
        var now = clock.UtcNow;
        var claimed = document?.Claim(now, ClaimLease);
        if (document is null || claimed!.IsFailure)
        {
            return null;
        }

        // Rango vigente de la sucursal/caja (RN-FE-05).
        var range = FiscalNumberingRange.Select(await ranges.ListAsync(cancellationToken), document.BranchId, document.PosTerminalId, document.DocumentType, clock.Today);
        if (range is null)
        {
            var detail = $"No hay rango de numeración vigente de {document.DocumentType.Db()} para la sucursal: sincronice y asigne los rangos del proveedor.";
            if (document.Defer("NO_RANGE", detail, now, now + DeferDelay, ids.NewId))
            {
                await audit.WriteAsync(
                    new AuditEntry("billing", "FISCAL_RANGE_MISSING", nameof(FiscalDocument), document.Id, document.AuditLabel, detail, Severity: AuditSeverity.Critical),
                    cancellationToken);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }

        document.UseRange(range.Id);
        var draft = await BuildAsync(document, range, cancellationToken);
        if (draft.IsFailure)
        {
            if (draft.Error.Code == FiscalDraftBuilder.RelatedNotAccepted.Code)
            {
                document.Defer("WAITING_INVOICE", draft.Error.Message, now, now + TimeSpan.FromMinutes(1), ids.NewId);
            }
            else
            {
                document.RecordResult(
                    new FiscalProviderResult(FiscalOutcome.Failed, Code: draft.Error.Code, Message: draft.Error.Message), provider.Name, now,
                    now + FiscalRetryPolicy.Delay(document.Attempts), ids.NewId);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }

        // El reclamo queda guardado ANTES de llamar al proveedor: una anulación concurrente emite nota crédito en vez de cancelar.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        FiscalProviderResult result;
        try
        {
            result = claimed.Value && document.ReferenceCode is { } reference
                ? await provider.GetStatusAsync(connection, document.DocumentType, reference, document.ProviderDocumentId, cancellationToken)
                : new FiscalProviderResult(FiscalOutcome.NotFound);
            if (result.Outcome is FiscalOutcome.NotFound or FiscalOutcome.Failed or FiscalOutcome.NotConfigured)
            {
                result = await SubmitAsync(connection, draft.Value, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un adaptador no debe lanzar; si lo hace, el documento se reintenta (el mensaje nunca lleva credenciales).
            result = new FiscalProviderResult(FiscalOutcome.Failed, Code: "EXCEPTION", Message: $"Falla inesperada del adaptador: {ex.GetType().Name}.");
        }

        now = clock.UtcNow;
        var maxMinutes = result.Outcome == FiscalOutcome.Unavailable ? 15 : 60;
        var status = document.RecordResult(result, provider.Name, now, now + FiscalRetryPolicy.Delay(document.Attempts, maxMinutes), ids.NewId);
        if (status == FiscalStatus.Rejected)
        {
            await audit.WriteAsync(
                new AuditEntry("billing", "FISCAL_DOCUMENT_REJECTED", nameof(FiscalDocument), document.Id, document.AuditLabel,
                    $"{document.DocumentType.Db()} {document.SourceNumber} rechazado: {document.RejectionMessage}", Severity: AuditSeverity.Critical),
                cancellationToken);
        }

        if (status == FiscalStatus.Accepted && result.Consecutive is { } consecutive)
        {
            var percent = await settings.GetAsync(BillingSettings.RangeAlertPercent, new SettingContext(document.CompanyId), cancellationToken);
            if (range.Advance(consecutive, percent))
            {
                await audit.WriteAsync(
                    new AuditEntry("billing", "FISCAL_RANGE_ALERT", nameof(FiscalNumberingRange), range.Id, range.AuditLabel,
                        $"El rango {range.Prefix} va en {range.UsagePercent:N2} % de uso: quedan {range.Remaining} números.", Severity: AuditSeverity.Warning),
                    cancellationToken);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result.Outcome;
    }

    private Task<FiscalProviderResult> SubmitAsync(FiscalConnection connection, object draft, CancellationToken cancellationToken) => draft switch
    {
        FiscalInvoiceDraft invoice => provider.SubmitInvoiceAsync(connection, invoice, cancellationToken),
        FiscalCreditNoteDraft credit => provider.SubmitCreditNoteAsync(connection, credit, cancellationToken),
        FiscalSupportDocumentDraft support => provider.SubmitSupportDocumentAsync(connection, support, cancellationToken),
        _ => throw new InvalidOperationException("Borrador fiscal desconocido."),
    };

    /// <summary>Arma el borrador neutro del documento desde su origen guardado.</summary>
    public async Task<Result<object>> BuildAsync(FiscalDocument document, FiscalNumberingRange range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(range);
        var issuer = await sources.GetIssuerAsync(document.BranchId, document.PosTerminalId, cancellationToken);
        if (issuer is null)
        {
            return Error.NotFound("BILLING.ISSUER_NOT_FOUND", "No se encontraron los datos de la empresa y la sucursal emisora.");
        }

        var numbering = new FiscalNumbering(range.Id, range.ProviderRangeId, range.Prefix, range.ResolutionNumber);
        var header = FiscalDraftBuilder.Header(document, numbering, issuer.Issuer, issuer.Establishment);
        if (document.Source == FiscalSource.Purchase)
        {
            var purchase = await sources.GetPurchaseAsync(document.SourceId, cancellationToken);
            return purchase is null
                ? Error.NotFound("BILLING.SOURCE_NOT_FOUND", "No se encontró la compra del documento soporte.")
                : FiscalDraftBuilder.SupportDocument(header, purchase with { Supplier = Party(document, purchase.Supplier, await CodesAsync(cancellationToken)) });
        }

        FiscalDocument? invoice = null;
        var saleId = document.SourceId;
        if (document.Source == FiscalSource.CustomerReturn)
        {
            invoice = document.RelatedDocumentId is { } relatedId ? await documents.GetAsync(relatedId, cancellationToken) : null;
            saleId = invoice?.SourceId ?? Guid.Empty;
        }
        else if (document.Source == FiscalSource.SaleVoid)
        {
            invoice = document.RelatedDocumentId is { } relatedId ? await documents.GetAsync(relatedId, cancellationToken) : null;
        }

        var sale = await sources.GetSaleAsync(saleId, cancellationToken);
        if (sale is null)
        {
            return Error.NotFound("BILLING.SOURCE_NOT_FOUND", "No se encontró la venta del documento.");
        }

        var customer = Party(document, sale.Customer, await CodesAsync(cancellationToken));
        if (document.Source == FiscalSource.Sale)
        {
            var built = FiscalDraftBuilder.Invoice(header, customer, sale);
            return built.IsFailure ? built.Error : built.Value;
        }

        var reference = FiscalDraftBuilder.Reference(invoice);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        if (document.Source == FiscalSource.SaleVoid)
        {
            var voided = FiscalDraftBuilder.VoidCreditNote(header, customer, reference.Value, sale);
            return voided.IsFailure ? voided.Error : voided.Value;
        }

        var customerReturn = await sources.GetReturnAsync(document.SourceId, cancellationToken);
        if (customerReturn is null)
        {
            return Error.NotFound("BILLING.SOURCE_NOT_FOUND", "No se encontró el cambio o la garantía del documento.");
        }

        var returned = FiscalDraftBuilder.ReturnCreditNote(header, customer, reference.Value, sale, customerReturn);
        return returned.IsFailure ? returned.Error : returned.Value;
    }

    private async Task<IReadOnlyDictionary<string, string>> CodesAsync(CancellationToken cancellationToken) =>
        _codes ??= await sources.GetIdentificationFiscalCodesAsync(cancellationToken);

    private static FiscalParty Party(FiscalDocument document, FiscalParty original, IReadOnlyDictionary<string, string> codes) =>
        FiscalDraftBuilder.Buyer(document, original, type => codes.TryGetValue(type, out var code) ? code : null);
}

/// <summary>Sincroniza los rangos de numeración desde el proveedor (D11B-05): crea los nuevos, actualiza los conocidos y desactiva los retirados.</summary>
public sealed class FiscalRangeService(
    IBillingSettingsStore settingsStore,
    IFiscalRangeStore ranges,
    FiscalConnectionFactory connections,
    IFiscalProvider provider,
    ISettingsReader settings,
    IInstallationContext installation,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    IIdGenerator ids,
    IClock clock)
{
    public static readonly Error NotConfigured = Error.BusinessRule(
        "BILLING.PROVIDER_NOT_CONFIGURED", "Configure las credenciales del proveedor de facturación electrónica.");

    public async Task<Result<IReadOnlyList<FiscalRangeDto>>> SyncAsync(CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
        }

        var connection = await connections.GetAsync(cancellationToken);
        var configured = await settingsStore.GetAsync(companyId, cancellationToken);
        if (connection is null || configured is null)
        {
            return NotConfigured;
        }

        var now = clock.UtcNow;
        var result = await provider.GetNumberingRangesAsync(connection, cancellationToken);
        if (result.Outcome != FiscalOutcome.Accepted)
        {
            var message = result.Message ?? $"El proveedor no entregó los rangos ({result.Outcome}).";
            configured.RecordSync(now, message);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Error.BusinessRule("BILLING.RANGE_SYNC_FAILED", $"No se pudieron sincronizar los rangos: {message}");
        }

        var all = (await ranges.ListAsync(cancellationToken)).ToList();
        var existing = all.Where(r => r.Provider == provider.Name).ToDictionary(r => r.ProviderRangeId, StringComparer.Ordinal);
        var created = 0;
        foreach (var data in result.Ranges)
        {
            if (existing.Remove(data.ProviderRangeId, out var known))
            {
                known.Refresh(data, now);
            }
            else
            {
                var range = FiscalNumberingRange.Create(ids.NewId(), companyId, provider.Name, data, now);
                ranges.Add(range);
                all.Add(range);
                created++;
            }
        }

        foreach (var retired in existing.Values.Where(r => r.IsActive))
        {
            retired.Refresh(
                new FiscalProviderRange(retired.ProviderRangeId, retired.DocumentType, retired.Prefix, retired.RangeFrom, retired.RangeTo, retired.CurrentNumber,
                    retired.ResolutionNumber, retired.ValidFrom, retired.ValidTo, IsActive: false),
                now);
        }

        configured.RecordSync(now, null);
        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_RANGES_SYNCED", "FiscalNumberingRange", null, provider.Name,
                $"Rangos sincronizados con {provider.Name}: {result.Ranges.Count} informados, {created} nuevos, {existing.Count} retirados."),
            cancellationToken);

        var company = new SettingContext(companyId);
        var percent = await settings.GetAsync(BillingSettings.RangeAlertPercent, company, cancellationToken);
        var days = await settings.GetAsync(BillingSettings.RangeAlertDays, company, cancellationToken);
        foreach (var alert in all.Where(r => r.BranchId is not null && r.NeedsAlert(clock.Today, percent, days)))
        {
            await audit.WriteAsync(
                new AuditEntry("billing", "FISCAL_RANGE_ALERT", nameof(FiscalNumberingRange), alert.Id, alert.AuditLabel,
                    $"Rango {alert.Prefix}: {alert.UsagePercent:N2} % usado, vence {alert.ValidTo?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "sin fecha"}.",
                    Severity: AuditSeverity.Warning),
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return all.Select(r => r.ToDto(clock.Today, percent, days)).OrderBy(r => r.DocumentType, StringComparer.Ordinal).ThenBy(r => r.Prefix, StringComparer.Ordinal).ToList();
    }
}

/// <summary>Límite de ritmo propio (D11B-09): ventana deslizante de un minuto (⚙️ 60 envíos por minuto por NIT).</summary>
public sealed class FiscalRateLimiter(TimeProvider time)
{
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly Lock _gate = new();

    public bool TryAcquire(int perMinute)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromMinutes(1))
            {
                _sent.Dequeue();
            }

            if (_sent.Count >= Math.Max(1, perMinute))
            {
                return false;
            }

            _sent.Enqueue(now);
            return true;
        }
    }
}

/// <summary>Resultado de una pasada de la cola.</summary>
public sealed record FiscalQueueRun(int Processed, int Sent, int Accepted, string? StoppedBecause);
