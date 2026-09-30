using System.Text.Json;
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

/// <summary>Documentos fiscales (EF Core).</summary>
public interface IFiscalDocumentStore
{
    void Add(FiscalDocument document);

    Task<FiscalDocument?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<FiscalDocument?> GetBySourceAsync(Guid sourceId, string source, CancellationToken cancellationToken);

    Task<IReadOnlyList<FiscalDocument>> ListAsync(DocumentFilter filter, CancellationToken cancellationToken);
}

public sealed record DocumentFilter(Guid BranchId, DateOnly? From, DateOnly? To, FiscalStatus? Status, int Limit);

/// <summary>Resultado de enviar un documento al proveedor de facturación electrónica.</summary>
public sealed record FiscalSubmission(FiscalStatus Status, string? FiscalNumber, string? Cufe, string? QrData, string? ProviderCode, string? Detail);

/// <summary>
/// Proveedor de facturación electrónica (D7-12). La Fase 11-B agrega el adaptador de Factus; hasta entonces el proveedor
/// nulo deja los documentos pendientes. Cambiar de proveedor es escribir otro adaptador.
/// </summary>
public interface IFiscalProvider
{
    string Name { get; }

    Task<FiscalSubmission> SubmitAsync(FiscalDocument document, CancellationToken cancellationToken);
}

/// <summary>Sin proveedor configurado: el documento sigue pendiente y se registra el intento.</summary>
public sealed class NullFiscalProvider : IFiscalProvider
{
    public string Name => "NONE";

    public Task<FiscalSubmission> SubmitAsync(FiscalDocument document, CancellationToken cancellationToken) =>
        Task.FromResult(new FiscalSubmission(FiscalStatus.Pending, null, null, null, "NOT_CONFIGURED",
            "No hay proveedor de facturación electrónica configurado (Fase 11-B)."));
}

/// <summary>Configuraciones de facturación. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class BillingSettings
{
    /// <summary>Fase 7 (pregunta 1): hoy solo comprobante interno; la facturación electrónica se activa en la 11-B.</summary>
    public static readonly SettingDefinition<bool> ElectronicEnabled = new(
        "billing.electronic_enabled", false, SettingScope.Company | SettingScope.Branch,
        "Emitir documento equivalente electrónico POS con el proveedor (Factus, Fase 11-B). Si no, cada venta lleva un comprobante interno.");

    public static IEnumerable<SettingDefinition> All => [ElectronicEnabled];
}

public sealed class BillingSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => BillingSettings.All;
}

public sealed class BillingPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => BillingPermissions.All;
}

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

    public static FiscalDocumentInfo ToInfo(this FiscalDocument d) => new(d.Id, d.DocumentType.Db(), d.Status.Db(), d.FiscalNumber, d.Cufe, d.QrData);

    public static FiscalDocumentDto ToDto(this FiscalDocument d) => new(
        d.Id, d.Source.Db(), d.SourceId, d.SourceNumber, d.DocumentType.Db(), d.Status.Db(), d.Provider, d.FiscalNumber, d.Cufe, d.Attempts, d.BusinessDate,
        d.BuyerName, d.BuyerIdentification, d.Subtotal, d.TaxTotal, d.Total, d.IssuedAt,
        [.. d.Events.OrderBy(e => e.OccurredAt).Select(e => new FiscalDocumentEventDto(e.EventType, e.Detail, e.ProviderCode, e.OccurredAt))]);

    public static FiscalSource Source(string source) => source switch
    {
        "SALE" => FiscalSource.Sale,
        "SALE_VOID" => FiscalSource.SaleVoid,
        "CUSTOMER_RETURN" => FiscalSource.CustomerReturn,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Origen de documento desconocido."),
    };
}

/// <summary>
/// Emisión en la transacción de la venta (D7-12): comprobante interno hoy; con la facturación electrónica activa, documento
/// pendiente y un mensaje LOCAL del outbox para que el trabajador lo envíe cuando haya conexión.
/// </summary>
public sealed class BillingService(
    IFiscalDocumentStore store, ISettingsReader settings, IOutbox outbox, IActorContext actor, IInstallationContext installation, IIdGenerator ids, IClock clock)
    : IBillingService
{
    public const string PendingMessage = "billing.document_pending.v1";

    public async Task<Result<FiscalDocumentInfo>> IssueAsync(FiscalIssueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var companyId = installation.CompanyId!.Value;
        var electronic = await settings.GetAsync(BillingSettings.ElectronicEnabled, new SettingContext(companyId, request.BranchId), cancellationToken);
        Guid? related = request.RelatedSourceId is { } relatedSource ? (await store.GetBySourceAsync(relatedSource, "SALE", cancellationToken))?.Id : null;
        var document = FiscalDocument.Issue(
            ids.NewId(), companyId,
            new FiscalIssue(
                BillingMapping.Source(request.Source), request.SourceId, request.SourceNumber, request.BranchId, request.PosTerminalId, request.BusinessDate,
                request.BuyerName, request.BuyerIdentificationType, request.BuyerIdentification, request.BuyerEmail, request.Subtotal, request.TaxTotal, request.Total,
                related),
            electronic, clock.UtcNow, actor.ActorId, ids.NewId);
        store.Add(document);
        if (document.Status == FiscalStatus.Pending)
        {
            outbox.Enqueue(PendingMessage, new { DocumentId = document.Id }, OutboxDestination.Local);
        }

        return document.ToInfo();
    }

    public async Task<Result<FiscalDocumentInfo?>> VoidForSourceAsync(Guid sourceId, string reason, CancellationToken cancellationToken = default)
    {
        var document = await store.GetBySourceAsync(sourceId, "SALE", cancellationToken);
        if (document is null)
        {
            return Result.Success<FiscalDocumentInfo?>(null);
        }

        if (document.Void(reason, clock.UtcNow, actor.ActorId, ids.NewId))
        {
            return document.ToInfo();
        }

        // Documento electrónico ya aceptado: la anulación es una nota crédito (se envía en segundo plano, Fase 11-B).
        var credit = FiscalDocument.Issue(
            ids.NewId(), document.CompanyId,
            new FiscalIssue(
                FiscalSource.SaleVoid, sourceId, document.SourceNumber, document.BranchId, document.PosTerminalId, clock.Today, document.BuyerName,
                document.BuyerIdentificationType, document.BuyerIdentification, document.BuyerEmail, document.Subtotal, document.TaxTotal, document.Total, document.Id),
            electronic: true, clock.UtcNow, actor.ActorId, ids.NewId);
        store.Add(credit);
        outbox.Enqueue(PendingMessage, new { DocumentId = credit.Id }, OutboxDestination.Local);
        return credit.ToInfo();
    }

    public async Task<FiscalDocumentInfo?> GetForSourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        (await store.GetBySourceAsync(sourceId, "SALE", cancellationToken))?.ToInfo();
}

/// <summary>
/// Envía un documento pendiente al proveedor (mensaje LOCAL del outbox, idempotente: un documento ya aceptado no se reenvía).
/// Sin proveedor configurado, el documento sigue pendiente con su intento registrado.
/// </summary>
public sealed class FiscalSubmissionService(IFiscalDocumentStore store, IFiscalProvider provider, IUnitOfWork unitOfWork, IIdGenerator ids, IClock clock)
{
    public async Task SubmitAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(documentId, cancellationToken);
        if (document is null || document.Status is not (FiscalStatus.Pending or FiscalStatus.Error))
        {
            return;
        }

        var result = await provider.SubmitAsync(document, cancellationToken);
        var now = clock.UtcNow;
        document.RecordAttempt(
            result.Status, result.FiscalNumber, result.Cufe, result.QrData, result.ProviderCode, result.Detail, provider.Name, now,
            now.AddMinutes(Math.Min(60, 5 * (document.Attempts + 1))), ids.NewId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public static Guid DocumentIdOf(JsonElement payload) => payload.GetProperty("documentId").GetGuid();
}

public sealed record GetFiscalDocumentQuery(Guid DocumentId) : IQuery<FiscalDocumentDto>;

internal sealed class GetFiscalDocumentHandler(IFiscalDocumentStore store) : IQueryHandler<GetFiscalDocumentQuery, FiscalDocumentDto>
{
    public async Task<Result<FiscalDocumentDto>> Handle(GetFiscalDocumentQuery request, CancellationToken cancellationToken) =>
        await store.GetAsync(request.DocumentId, cancellationToken) is { } document ? document.ToDto() : BillingErrors.NotFound;
}

public sealed record ListFiscalDocumentsQuery(DateOnly? From, DateOnly? To, FiscalStatus? Status, int? Limit) : IQuery<IReadOnlyList<FiscalDocumentDto>>;

internal sealed class ListFiscalDocumentsHandler(IFiscalDocumentStore store, IInstallationContext installation)
    : IQueryHandler<ListFiscalDocumentsQuery, IReadOnlyList<FiscalDocumentDto>>
{
    public async Task<Result<IReadOnlyList<FiscalDocumentDto>>> Handle(ListFiscalDocumentsQuery request, CancellationToken cancellationToken) =>
        installation.BranchId is { } branch
            ? (await store.ListAsync(new DocumentFilter(branch, request.From, request.To, request.Status, Math.Clamp(request.Limit ?? 200, 1, 1000)), cancellationToken))
                .Select(d => d.ToDto()).ToList()
            : Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
}

/// <summary>Reintento manual de un documento electrónico pendiente, con error o rechazado.</summary>
public sealed record RetryFiscalDocumentCommand(Guid DocumentId) : ICommand<FiscalDocumentDto>, IAllowedWhenRestricted;

internal sealed class RetryFiscalDocumentHandler(IFiscalDocumentStore store, IOutbox outbox, IActorContext actor, IIdGenerator ids, IClock clock)
    : ICommandHandler<RetryFiscalDocumentCommand, FiscalDocumentDto>
{
    public async Task<Result<FiscalDocumentDto>> Handle(RetryFiscalDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(request.DocumentId, cancellationToken);
        if (document is null)
        {
            return BillingErrors.NotFound;
        }

        var retried = document.Retry(clock.UtcNow, actor.ActorId!.Value, ids.NewId);
        if (retried.IsFailure)
        {
            return retried.Error;
        }

        outbox.Enqueue(BillingService.PendingMessage, new { DocumentId = document.Id }, OutboxDestination.Local);
        return document.ToDto();
    }
}
