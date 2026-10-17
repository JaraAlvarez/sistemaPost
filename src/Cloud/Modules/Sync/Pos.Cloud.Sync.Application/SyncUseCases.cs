using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.SharedKernel.Results;
using Pos.Sync.Contracts;

namespace Pos.Cloud.Sync.Application;

// ------------------------------------------------------------------------------------------------ Datos del portal

public sealed record SyncOrganizationDto(Guid Id, string LegalName, string AccountName);

public sealed record SyncInstallationStatusDto(
    Guid InstallationId, string? BranchName, DateTimeOffset? LastBatchAt, string? LastVia, int Sales, int CashSessions, int StockRows, int Products);

public sealed record SalesDayDto(DateOnly BusinessDate, string Branch, int Tickets, int Voided, decimal Total);

public sealed record SaleRowDto(string? Number, string Branch, string? Terminal, string? Cashier, string Status, decimal Total, DateTimeOffset? CompletedAt, string? Customer);

public sealed record CashSessionRowDto(
    string? Number, string Branch, string? Terminal, string? Cashier, DateOnly? BusinessDate, decimal? Expected, decimal? Counted, decimal? Difference,
    DateTimeOffset? ClosedAt);

public sealed record StockRowDto(string Branch, string? Warehouse, string? Sku, string? Name, decimal Quantity, decimal TotalValue, DateTimeOffset? LastMovementAt);

/// <summary>Resultado de cargar un paquete. <c>Unverified</c>: la instalación aún no registraba su clave de firma (revisión S2).</summary>
public sealed record UploadResultDto(string OrganizationName, string? BranchName, int Items, int Applied, int Ignored, bool Duplicate, bool Unverified = false);

// ------------------------------------------------------------------------------------------------ Puertos

public interface ISyncRepository
{
    /// <summary>
    /// Aplica el lote de forma idempotente (D16-05): cada documento queda con su versión más reciente (acotada a la hora del servidor más una
    /// tolerancia, S2); un lote repetido no se reaplica. <paramref name="signatureStatus"/>: VERIFIED/UNVERIFIED para paquetes, nulo en línea.
    /// </summary>
    Task<SyncAck> ApplyAsync(
        SyncBatch batch, PosInstallationIdentity installation, string via, Guid? uploadedBy, CancellationToken cancellationToken, string? signatureStatus = null);

    /// <summary>Clave pública de firma registrada para la instalación (nula si nunca sincronizó en línea con una versión que firma).</summary>
    Task<string?> GetSigningKeyAsync(Guid installationId, CancellationToken cancellationToken);

    /// <summary>Registra o cambia la clave pública de firma de la instalación (la anterior queda en <c>previous_key</c>).</summary>
    Task RegisterSigningKeyAsync(PosInstallationIdentity installation, string publicKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncOrganizationDto>> OrganizationsAsync(Guid? accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncInstallationStatusDto>> StatusAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesDayDto>> SalesByDayAsync(Guid organizationId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<IReadOnlyList<SaleRowDto>> RecentSalesAsync(Guid organizationId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<CashSessionRowDto>> CashSessionsAsync(Guid organizationId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<StockRowDto>> StockAsync(Guid organizationId, string? search, int limit, CancellationToken cancellationToken);
}

/// <summary>A qué empresas accede el usuario del portal (D16-06): el equipo del propietario a todas; el cliente, solo a las de su cuenta.</summary>
public interface ISyncAccess
{
    /// <summary><c>Allowed</c> = puede ver datos; <c>AccountId</c> nulo = todas las cuentas, si no, solo la del cliente.</summary>
    Task<(bool Allowed, Guid? AccountId)> ScopeAsync(CancellationToken cancellationToken);

    Task<bool> CanAccessAsync(Guid organizationId, CancellationToken cancellationToken);
}

/// <summary>Abre los paquetes <c>.possync</c> con la clave privada de sincronización de la nube.</summary>
public interface ISyncPackageOpener
{
    bool IsConfigured { get; }

    /// <summary>Abre el paquete con límites (S3) y verifica la firma de la tienda si viene firmado (S2).</summary>
    OpenedSyncPackage Open(byte[] package);
}

public static class SyncErrors
{
    public static readonly Error Unauthorized = Error.Unauthorized(
        "SYNC.UNAUTHORIZED", "La tienda no se pudo identificar: token de licencia inválido, licencia inactiva o equipo distinto al activado.");
    public static readonly Error Forbidden = Error.Forbidden("SYNC.FORBIDDEN", "No tiene acceso a los datos de esa empresa.");
    public static readonly Error NoPrivateKey = Error.BusinessRule("SYNC.NO_PRIVATE_KEY", "La nube no tiene configurada su clave de sincronización (Sync:PrivateKeyPath).");
    public static readonly Error UnknownInstallation = Error.BusinessRule("SYNC.UNKNOWN_INSTALLATION", "El paquete es de una instalación que la nube no conoce.");
    public static readonly Error TooLarge = Error.Validation("SYNC.BATCH_TOO_LARGE", "El lote supera el máximo de datos por envío.");
    public static readonly Error InvalidSigningKey = Error.Validation(
        "SYNC.INVALID_SIGNING_KEY", "La clave de firma de la tienda o su prueba de posesión no son válidas.");
    public static readonly Error WrongSigner = Error.Validation(
        "SYNC.WRONG_SIGNER", "El paquete no está firmado con la clave registrada de esa tienda: puede haber sido fabricado o alterado.");

    public static Error InvalidPackage(string message) => Error.Validation("SYNC.INVALID_PACKAGE", message);
}

// ------------------------------------------------------------------------------------------------ API del POS

/// <summary>
/// Lote enviado por una tienda en línea (autenticado con el token de licencia y la huella, D16-03). Desde la revisión S2 trae además la clave
/// pública de firma de la instalación y una prueba de posesión: la nube la registra para verificar los paquetes <c>.possync</c>.
/// </summary>
public sealed record ReceiveBatchCommand(SyncBatch Batch, string Token, string Fingerprint, string? SigningKey = null, string? SigningKeyProof = null)
    : ICommand<SyncAck>;

internal sealed class ReceiveBatchHandler(IPosInstallationAuthenticator authenticator, ISyncRepository repository) : ICommandHandler<ReceiveBatchCommand, SyncAck>
{
    public const int MaxItems = SyncLimits.MaxOnlineItems;

    public async Task<Result<SyncAck>> Handle(ReceiveBatchCommand command, CancellationToken cancellationToken)
    {
        if (command.Batch.Items is null || command.Batch.Items.Count > MaxItems)
        {
            return SyncErrors.TooLarge;
        }

        var installation = await authenticator.AuthenticateAsync(command.Token, command.Fingerprint, cancellationToken);
        if (installation is null || installation.InstallationId != command.Batch.InstallationId)
        {
            return SyncErrors.Unauthorized;
        }

        if (!string.IsNullOrWhiteSpace(command.SigningKey))
        {
            var key = command.SigningKey.Trim();
            if (!SyncSigning.VerifyKeyProof(key, command.SigningKeyProof, command.Batch.InstallationId, command.Batch.BatchId))
            {
                return SyncErrors.InvalidSigningKey;
            }

            if (await repository.GetSigningKeyAsync(installation.InstallationId, cancellationToken) != key)
            {
                await repository.RegisterSigningKeyAsync(installation, key, cancellationToken);
            }
        }

        return await repository.ApplyAsync(command.Batch, installation, "ONLINE", null, cancellationToken);
    }
}

// ------------------------------------------------------------------------------------------------ Portal

[RequiresPermission(CloudPermissions.SyncPackageUpload)]
public sealed record UploadPackageCommand(byte[] Package) : ICommand<UploadResultDto>;

internal sealed class UploadPackageHandler(
    ISyncPackageOpener opener, IPosInstallationAuthenticator authenticator, ISyncAccess access, ISyncRepository repository, IPortalUserContext user)
    : ICommandHandler<UploadPackageCommand, UploadResultDto>
{
    public async Task<Result<UploadResultDto>> Handle(UploadPackageCommand command, CancellationToken cancellationToken)
    {
        if (!opener.IsConfigured)
        {
            return SyncErrors.NoPrivateKey;
        }

        OpenedSyncPackage opened;
        try
        {
            opened = opener.Open(command.Package);
        }
        catch (SyncPackageException ex)
        {
            return SyncErrors.InvalidPackage(ex.Message);
        }

        var batch = opened.Batch;
        if (await authenticator.FindAsync(batch.InstallationId, cancellationToken) is not { } installation)
        {
            return SyncErrors.UnknownInstallation;
        }

        if (!await access.CanAccessAsync(installation.OrganizationId, cancellationToken))
        {
            return SyncErrors.Forbidden;
        }

        // Con clave registrada, el paquete DEBE venir firmado con ella. Sin clave (tienda que nunca sincronizó en línea con una versión que
        // firma) se acepta por compatibilidad y queda marcado UNVERIFIED: la firma que traiga no prueba nada porque nadie la conoce.
        var registered = await repository.GetSigningKeyAsync(installation.InstallationId, cancellationToken);
        if (registered is not null && opened.SignerPublicKey != registered)
        {
            return SyncErrors.WrongSigner;
        }

        var status = registered is null ? "UNVERIFIED" : "VERIFIED";
        var ack = await repository.ApplyAsync(batch, installation, "FILE", user.UserId, cancellationToken, status);
        return new UploadResultDto(installation.OrganizationName, installation.BranchName, ack.Received, ack.Applied, ack.Ignored, ack.Duplicate,
            registered is null);
    }
}

[RequiresPermission(CloudPermissions.SyncDataView)]
public sealed record ListSyncOrganizationsQuery : IQuery<IReadOnlyList<SyncOrganizationDto>>;

internal sealed class ListSyncOrganizationsHandler(ISyncAccess access, ISyncRepository repository)
    : IQueryHandler<ListSyncOrganizationsQuery, IReadOnlyList<SyncOrganizationDto>>
{
    public async Task<Result<IReadOnlyList<SyncOrganizationDto>>> Handle(ListSyncOrganizationsQuery query, CancellationToken cancellationToken)
    {
        var (allowed, account) = await access.ScopeAsync(cancellationToken);
        return allowed ? Result.Success(await repository.OrganizationsAsync(account, cancellationToken)) : SyncErrors.Forbidden;
    }
}

/// <summary>Todo lo que el portal muestra de una empresa (una sola consulta por pantalla).</summary>
public sealed record StoreDataDto(
    IReadOnlyList<SyncInstallationStatusDto> Installations,
    IReadOnlyList<SalesDayDto> SalesByDay,
    IReadOnlyList<SaleRowDto> RecentSales,
    IReadOnlyList<CashSessionRowDto> CashSessions);

[RequiresPermission(CloudPermissions.SyncDataView)]
public sealed record GetStoreDataQuery(Guid OrganizationId, DateOnly From, DateOnly To) : IQuery<StoreDataDto>;

internal sealed class GetStoreDataHandler(ISyncAccess access, ISyncRepository repository) : IQueryHandler<GetStoreDataQuery, StoreDataDto>
{
    public async Task<Result<StoreDataDto>> Handle(GetStoreDataQuery query, CancellationToken cancellationToken)
    {
        if (!await access.CanAccessAsync(query.OrganizationId, cancellationToken))
        {
            return SyncErrors.Forbidden;
        }

        return new StoreDataDto(
            await repository.StatusAsync(query.OrganizationId, cancellationToken),
            await repository.SalesByDayAsync(query.OrganizationId, query.From, query.To, cancellationToken),
            await repository.RecentSalesAsync(query.OrganizationId, 50, cancellationToken),
            await repository.CashSessionsAsync(query.OrganizationId, 50, cancellationToken));
    }
}

[RequiresPermission(CloudPermissions.SyncDataView)]
public sealed record SearchStoreStockQuery(Guid OrganizationId, string? Search) : IQuery<IReadOnlyList<StockRowDto>>;

internal sealed class SearchStoreStockHandler(ISyncAccess access, ISyncRepository repository) : IQueryHandler<SearchStoreStockQuery, IReadOnlyList<StockRowDto>>
{
    public async Task<Result<IReadOnlyList<StockRowDto>>> Handle(SearchStoreStockQuery query, CancellationToken cancellationToken) =>
        await access.CanAccessAsync(query.OrganizationId, cancellationToken)
            ? Result.Success(await repository.StockAsync(query.OrganizationId, query.Search, 200, cancellationToken))
            : SyncErrors.Forbidden;
}
