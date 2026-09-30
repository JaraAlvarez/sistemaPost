using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Billing.Application;

internal static class BillingGuards
{
    public static readonly Error SetupRequired = Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
}

// ─────────────────────────────── Documentos ───────────────────────────────

public sealed record GetFiscalDocumentQuery(Guid DocumentId) : IQuery<FiscalDocumentDto>;

internal sealed class GetFiscalDocumentHandler(IFiscalDocumentStore store) : IQueryHandler<GetFiscalDocumentQuery, FiscalDocumentDto>
{
    public async Task<Result<FiscalDocumentDto>> Handle(GetFiscalDocumentQuery request, CancellationToken cancellationToken) =>
        await store.GetAsync(request.DocumentId, cancellationToken) is { } document ? document.ToDto() : BillingErrors.NotFound;
}

/// <summary>Documentos de la sucursal con filtros: fechas, estado, tipo, origen y texto (número de origen, número fiscal o identificación).</summary>
public sealed record ListFiscalDocumentsQuery(
    DateOnly? From, DateOnly? To, string? Status, int? Limit, string? Type = null, string? Source = null, string? Search = null) : IQuery<IReadOnlyList<FiscalDocumentDto>>;

internal sealed class ListFiscalDocumentsHandler(IFiscalDocumentStore store, IInstallationContext installation)
    : IQueryHandler<ListFiscalDocumentsQuery, IReadOnlyList<FiscalDocumentDto>>
{
    public async Task<Result<IReadOnlyList<FiscalDocumentDto>>> Handle(ListFiscalDocumentsQuery request, CancellationToken cancellationToken)
    {
        if (installation.BranchId is not { } branch)
        {
            return BillingGuards.SetupRequired;
        }

        var status = BillingMapping.Parse<FiscalStatus>(request.Status);
        var type = BillingMapping.Parse<FiscalDocumentType>(request.Type);
        var source = BillingMapping.Parse<FiscalSource>(request.Source);
        if ((request.Status is not null && status is null) || (request.Type is not null && type is null) || (request.Source is not null && source is null))
        {
            return Error.Validation("BILLING.INVALID_FILTER", "Estado, tipo u origen desconocido.");
        }

        var filter = new DocumentFilter(
            branch, request.From, request.To, status, Math.Clamp(request.Limit ?? 200, 1, 1000), type, source,
            string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim());
        return (await store.ListAsync(filter, cancellationToken)).Select(d => d.ToDto()).ToList();
    }
}

/// <summary>Reintento manual de un documento electrónico pendiente, con error, en contingencia o rechazado.</summary>
public sealed record RetryFiscalDocumentCommand(Guid DocumentId) : ICommand<FiscalDocumentDto>, IAllowedWhenRestricted;

internal sealed class RetryFiscalDocumentHandler(
    IFiscalDocumentStore store, IOutbox outbox, FiscalQueueSignal signal, IAuditWriter audit, IActorContext actor, IIdGenerator ids, IClock clock)
    : ICommandHandler<RetryFiscalDocumentCommand, FiscalDocumentDto>
{
    public async Task<Result<FiscalDocumentDto>> Handle(RetryFiscalDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(request.DocumentId, cancellationToken);
        if (document is null)
        {
            return BillingErrors.NotFound;
        }

        var previous = document.Status;
        var retried = document.Retry(clock.UtcNow, actor.ActorId!.Value, ids.NewId);
        if (retried.IsFailure)
        {
            return retried.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_DOCUMENT_RETRIED", nameof(FiscalDocument), document.Id, document.AuditLabel,
                $"Reintento manual de {document.DocumentType.Db()} {document.SourceNumber} (estaba {previous.Db()})."),
            cancellationToken);
        outbox.Enqueue(BillingService.PendingMessage, new { DocumentId = document.Id }, OutboxDestination.Local);
        signal.Notify();
        return document.ToDto();
    }
}

/// <summary>Corrige los datos del adquirente de un documento aún no aceptado y lo deja listo para reenviar (D11B-10).</summary>
public sealed record CorrectFiscalBuyerCommand(
    Guid DocumentId,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? Email,
    string PersonType,
    string TaxRegime,
    IReadOnlyList<string>? Responsibilities,
    string? Address,
    string? MunicipalityCode,
    string? Phone) : ICommand<FiscalDocumentDto>;

internal sealed class CorrectFiscalBuyerHandler(
    IFiscalDocumentStore store, IOutbox outbox, FiscalQueueSignal signal, IAuditWriter audit, IActorContext actor, IIdGenerator ids, IClock clock)
    : ICommandHandler<CorrectFiscalBuyerCommand, FiscalDocumentDto>
{
    public async Task<Result<FiscalDocumentDto>> Handle(CorrectFiscalBuyerCommand request, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(request.DocumentId, cancellationToken);
        if (document is null)
        {
            return BillingErrors.NotFound;
        }

        var before = $"{document.BuyerIdentificationType} {document.BuyerIdentification}";
        var corrected = document.CorrectBuyer(
            new FiscalBuyerCorrection(
                request.Name ?? string.Empty, request.IdentificationType ?? string.Empty, request.IdentificationNumber ?? string.Empty, request.Email,
                new FiscalBuyerData(
                    request.PersonType?.Trim().ToUpperInvariant() ?? string.Empty, Clean(request.CheckDigit), request.TaxRegime?.Trim() ?? string.Empty,
                    [.. (request.Responsibilities ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())], Clean(request.Address),
                    Clean(request.MunicipalityCode), Clean(request.Phone))),
            clock.UtcNow, actor.ActorId!.Value, ids.NewId);
        if (corrected.IsFailure)
        {
            return corrected.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_BUYER_CORRECTED", nameof(FiscalDocument), document.Id, document.AuditLabel,
                $"Datos del adquirente de {document.SourceNumber} corregidos para reenviar el documento.",
                OldValues: new Dictionary<string, object?> { ["Identificación"] = before },
                NewValues: new Dictionary<string, object?> { ["Identificación"] = $"{document.BuyerIdentificationType} {document.BuyerIdentification}" },
                Severity: AuditSeverity.Warning),
            cancellationToken);
        outbox.Enqueue(BillingService.PendingMessage, new { DocumentId = document.Id }, OutboxDestination.Local);
        signal.Notify();
        return document.ToDto();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// ─────────────────────────────── Configuración del proveedor ───────────────────────────────

public sealed record GetBillingSettingsQuery : IQuery<BillingSettingsDto>;

internal sealed class GetBillingSettingsHandler(
    IBillingSettingsStore store, IFiscalProvider provider, ISettingsReader settings, IInstallationContext installation)
    : IQueryHandler<GetBillingSettingsQuery, BillingSettingsDto>
{
    public async Task<Result<BillingSettingsDto>> Handle(GetBillingSettingsQuery request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        var configured = await store.GetAsync(companyId, cancellationToken);
        var mode = await BillingService.ModeAsync(store, settings, companyId, installation.BranchId, cancellationToken);
        return new BillingSettingsDto(
            configured?.Provider ?? provider.Name, provider.Name, mode.Db(), (configured?.Environment ?? FiscalEnvironment.Sandbox).Db(), configured?.HasCredentials ?? false,
            configured?.CredentialsUpdatedAt, configured?.LastSyncAt, configured?.LastSyncError);
    }
}

/// <summary>Modo de emisión (OFF, ON_REQUEST, EVERY_SALE) y ambiente (SANDBOX, PRODUCTION). Encender exige credenciales.</summary>
public sealed record UpdateBillingSettingsCommand(string Mode, string Environment) : ICommand<BillingSettingsDto>;

internal sealed class UpdateBillingSettingsHandler(
    IBillingSettingsStore store, IFiscalProvider provider, ISettingsReader settings, IInstallationContext installation, IAuditWriter audit)
    : ICommandHandler<UpdateBillingSettingsCommand, BillingSettingsDto>
{
    public async Task<Result<BillingSettingsDto>> Handle(UpdateBillingSettingsCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        if (BillingMapping.Parse<BillingMode>(request.Mode) is not { } mode || BillingMapping.Parse<FiscalEnvironment>(request.Environment) is not { } environment)
        {
            return Error.Validation("BILLING.INVALID_SETTINGS", "Modo (OFF, ON_REQUEST, EVERY_SALE) o ambiente (SANDBOX, PRODUCTION) desconocido.");
        }

        var configured = await store.GetAsync(companyId, cancellationToken);
        if (configured is null)
        {
            configured = BillingProviderSettings.Create(companyId, provider.Name);
            store.Add(configured);
        }

        var before = (Mode: configured.Mode, Environment: configured.Environment);
        var changed = configured.Configure(mode, environment, provider.Name);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_SETTINGS_CHANGED", "BillingProviderSettings", null, $"Facturación electrónica ({provider.Name})",
                $"Facturación electrónica: modo {before.Mode.Db()} → {mode.Db()}, ambiente {before.Environment.Db()} → {environment.Db()}.",
                OldValues: new Dictionary<string, object?> { ["Modo"] = before.Mode.Db(), ["Ambiente"] = before.Environment.Db() },
                NewValues: new Dictionary<string, object?> { ["Modo"] = mode.Db(), ["Ambiente"] = environment.Db() },
                Severity: mode == before.Mode ? AuditSeverity.Info : AuditSeverity.Warning),
            cancellationToken);
        return await new GetBillingSettingsHandler(store, provider, settings, installation).Handle(new GetBillingSettingsQuery(), cancellationToken);
    }
}

/// <summary>Credenciales del proveedor (D11B-08): se cifran con DPAPI al recibirlas y nunca se devuelven ni se registran.</summary>
public sealed record SetProviderCredentialsCommand(string Username, string Password, string ClientId, string ClientSecret) : ICommand<BillingSettingsDto>
{
    public override string ToString() => "SetProviderCredentialsCommand { *** }";
}

internal sealed class SetProviderCredentialsHandler(
    IBillingSettingsStore store, IFiscalProvider provider, IFiscalCredentialProtector protector, ISettingsReader settings, IInstallationContext installation,
    IAuditWriter audit, IClock clock)
    : ICommandHandler<SetProviderCredentialsCommand, BillingSettingsDto>
{
    public async Task<Result<BillingSettingsDto>> Handle(SetProviderCredentialsCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.ClientSecret) || request.Username.Length > 200 || request.Password.Length > 200
            || request.ClientId.Length > 200 || request.ClientSecret.Length > 500)
        {
            return BillingErrors.InvalidCredentials;
        }

        var configured = await store.GetAsync(companyId, cancellationToken);
        if (configured is null)
        {
            configured = BillingProviderSettings.Create(companyId, provider.Name);
            store.Add(configured);
        }

        configured.SetCredentials(
            protector.Protect(new FiscalCredentials(request.Username.Trim(), request.Password, request.ClientId.Trim(), request.ClientSecret.Trim())), clock.UtcNow);
        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_CREDENTIALS_CHANGED", "BillingProviderSettings", null, $"Facturación electrónica ({provider.Name})",
                $"Credenciales del proveedor {provider.Name} actualizadas (cifradas; no se registran).", Severity: AuditSeverity.Critical),
            cancellationToken);
        return await new GetBillingSettingsHandler(store, provider, settings, installation).Handle(new GetBillingSettingsQuery(), cancellationToken);
    }
}

// ─────────────────────────────── Rangos ───────────────────────────────

public sealed record ListFiscalRangesQuery : IQuery<IReadOnlyList<FiscalRangeDto>>;

internal sealed class ListFiscalRangesHandler(IFiscalRangeStore ranges, ISettingsReader settings, IInstallationContext installation, IClock clock)
    : IQueryHandler<ListFiscalRangesQuery, IReadOnlyList<FiscalRangeDto>>
{
    public async Task<Result<IReadOnlyList<FiscalRangeDto>>> Handle(ListFiscalRangesQuery request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        var context = new SettingContext(companyId);
        var percent = await settings.GetAsync(BillingSettings.RangeAlertPercent, context, cancellationToken);
        var days = await settings.GetAsync(BillingSettings.RangeAlertDays, context, cancellationToken);
        return (await ranges.ListAsync(cancellationToken))
            .Select(r => r.ToDto(clock.Today, percent, days))
            .OrderBy(r => r.DocumentType, StringComparer.Ordinal).ThenBy(r => r.Prefix, StringComparer.Ordinal).ThenBy(r => r.From)
            .ToList();
    }
}

/// <summary>
/// Asigna un rango a una sucursal (y opcionalmente a una caja) o lo desasigna (sin sucursal). El rango que ocupaba ese lugar para
/// el mismo tipo de documento se desasigna.
/// </summary>
public sealed record AssignFiscalRangeCommand(Guid RangeId, Guid? BranchId, Guid? PosTerminalId) : ICommand<FiscalRangeDto>;

internal sealed class AssignFiscalRangeHandler(
    IFiscalRangeStore ranges, ISettingsReader settings, IInstallationContext installation, IAuditWriter audit, IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<AssignFiscalRangeCommand, FiscalRangeDto>
{
    public async Task<Result<FiscalRangeDto>> Handle(AssignFiscalRangeCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        var range = await ranges.GetAsync(request.RangeId, cancellationToken);
        if (range is null)
        {
            return BillingErrors.RangeNotFound;
        }

        if (request.BranchId is not null && !range.IsActive)
        {
            return BillingErrors.RangeNotUsable;
        }

        if (request.PosTerminalId is not null && request.BranchId is null)
        {
            return BillingErrors.InvalidAssignment;
        }

        // Libera el lugar primero (índice único por sucursal, caja y tipo) y luego asigna.
        var displaced = (await ranges.ListAsync(cancellationToken))
            .Where(r => r.Id != range.Id && r.IsActive && request.BranchId is not null && r.BranchId == request.BranchId
                        && r.PosTerminalId == request.PosTerminalId && r.DocumentType == range.DocumentType)
            .ToList();
        foreach (var other in displaced)
        {
            other.Assign(null, null);
        }

        if (displaced.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var before = range.BranchId is null ? "sin asignar" : $"sucursal {range.BranchId}{(range.PosTerminalId is null ? string.Empty : $", caja {range.PosTerminalId}")}";
        var assigned = range.Assign(request.BranchId, request.PosTerminalId);
        if (assigned.IsFailure)
        {
            return assigned.Error;
        }

        var after = range.BranchId is null ? "sin asignar" : $"sucursal {range.BranchId}{(range.PosTerminalId is null ? string.Empty : $", caja {range.PosTerminalId}")}";
        await audit.WriteAsync(
            new AuditEntry("billing", "FISCAL_RANGE_ASSIGNED", nameof(FiscalNumberingRange), range.Id, range.AuditLabel,
                $"Rango {range.Prefix} {range.RangeFrom}-{range.RangeTo}: {before} → {after}{(displaced.Count > 0 ? $" (reemplaza {displaced.Count})" : string.Empty)}.",
                Severity: AuditSeverity.Warning),
            cancellationToken);

        var context = new SettingContext(companyId);
        return range.ToDto(
            clock.Today, await settings.GetAsync(BillingSettings.RangeAlertPercent, context, cancellationToken),
            await settings.GetAsync(BillingSettings.RangeAlertDays, context, cancellationToken));
    }
}

// ─────────────────────────────── Conciliación y alertas ───────────────────────────────

/// <summary>Resumen por día de la sucursal: ventas vs. documentos aceptados, pendientes, rechazados y en contingencia (máximo 92 días).</summary>
public sealed record GetFiscalReconciliationQuery(DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<FiscalReconciliationDayDto>>;

internal sealed class GetFiscalReconciliationHandler(IFiscalReadModel readModel, IInstallationContext installation, IClock clock)
    : IQueryHandler<GetFiscalReconciliationQuery, IReadOnlyList<FiscalReconciliationDayDto>>
{
    public async Task<Result<IReadOnlyList<FiscalReconciliationDayDto>>> Handle(GetFiscalReconciliationQuery request, CancellationToken cancellationToken)
    {
        if (installation.BranchId is not { } branch)
        {
            return BillingGuards.SetupRequired;
        }

        var to = request.To ?? clock.Today;
        var from = request.From ?? to.AddDays(-6);
        if (from > to || to.DayNumber - from.DayNumber > 92)
        {
            return Error.Validation("BILLING.INVALID_PERIOD", "El periodo debe ser válido y de máximo 92 días.");
        }

        return Result.Success(await readModel.ReconcileAsync(branch, from, to, cancellationToken));
    }
}

public sealed record GetFiscalAlertsQuery : IQuery<FiscalAlertsDto>;

internal sealed class GetFiscalAlertsHandler(
    IFiscalReadModel readModel, IFiscalRangeStore ranges, IBillingSettingsStore store, ISettingsReader settings, IInstallationContext installation, IClock clock)
    : IQueryHandler<GetFiscalAlertsQuery, FiscalAlertsDto>
{
    public async Task<Result<FiscalAlertsDto>> Handle(GetFiscalAlertsQuery request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BillingGuards.SetupRequired;
        }

        var context = new SettingContext(companyId);
        var hours = await settings.GetAsync(BillingSettings.PendingAlertHours, context, cancellationToken);
        var percent = await settings.GetAsync(BillingSettings.RangeAlertPercent, context, cancellationToken);
        var days = await settings.GetAsync(BillingSettings.RangeAlertDays, context, cancellationToken);
        var attention = await readModel.AttentionAsync(companyId, clock.UtcNow.AddHours(-hours), cancellationToken);
        var today = clock.Today;
        var rangeAlerts = (await ranges.ListAsync(cancellationToken))
            .Where(r => r.BranchId is not null && r.NeedsAlert(today, percent, days))
            .Select(r => new FiscalRangeAlertDto(
                r.Id, r.Prefix, r.DocumentType.Db(), r.UsagePercent, r.ValidTo,
                !r.IsUsableOn(today) ? "El rango está agotado o vencido: la facturación queda pendiente."
                : r.UsagePercent >= percent ? $"Quedan {r.Remaining} números ({r.UsagePercent:N2} % usado)."
                : $"Vence en {r.DaysToExpire(today)} días."))
            .ToList();
        var mode = await BillingService.ModeAsync(store, settings, companyId, installation.BranchId, cancellationToken);
        return new FiscalAlertsDto(mode.Db(), attention.PendingOverdue, attention.Rejected, attention.WithoutRange, rangeAlerts, attention.OldestPendingAt);
    }
}
