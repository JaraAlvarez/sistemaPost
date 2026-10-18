using System.Collections.Concurrent;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;
using Pos.Sync.Contracts;

namespace Pos.Cloud.Sync.Application;

// ------------------------------------------------------------------------------------------------ Datos del portal

public sealed record SyncOrganizationDto(Guid Id, string LegalName, string AccountName);

/// <summary>Estado de una sucursal. <c>Unverified</c>: paquetes cargados sin verificar la firma; <c>KeyRegistered</c>: ya tiene clave de firma.</summary>
public sealed record SyncInstallationStatusDto(
    Guid InstallationId, string? BranchName, DateTimeOffset? LastBatchAt, string? LastVia, int Sales, int CashSessions, int StockRows, int Products,
    int Unverified = 0, bool KeyRegistered = false);

public sealed record SalesDayDto(DateOnly BusinessDate, string Branch, int Tickets, int Voided, decimal Total);

public sealed record SaleRowDto(string? Number, string Branch, string? Terminal, string? Cashier, string Status, decimal Total, DateTimeOffset? CompletedAt, string? Customer);

public sealed record CashSessionRowDto(
    string? Number, string Branch, string? Terminal, string? Cashier, DateOnly? BusinessDate, decimal? Expected, decimal? Counted, decimal? Difference,
    DateTimeOffset? ClosedAt);

public sealed record StockRowDto(string Branch, string? Warehouse, string? Sku, string? Name, decimal Quantity, decimal TotalValue, DateTimeOffset? LastMovementAt);

/// <summary>Resultado de cargar un paquete. <c>Unverified</c>: la instalación aún no registraba su clave de firma (revisión S2).</summary>
public sealed record UploadResultDto(string OrganizationName, string? BranchName, int Items, int Applied, int Ignored, bool Duplicate, bool Unverified = false);

/// <summary>
/// Paquete aplicado sin verificar la firma (la instalación no tenía clave). <c>HasSignature</c>: venía firmado (se puede verificar cuando la tienda
/// registre su clave); <c>MatchesKey</c>: la clave registrada hoy es la que lo firmó.
/// </summary>
public sealed record UnverifiedBatchDto(Guid BatchId, Guid InstallationId, string? BranchName, DateTimeOffset ReceivedAt, int Items, bool HasSignature, bool MatchesKey);

/// <summary>Clave de firma registrada de una instalación: la vigente y la anterior (aceptada ⚙️ 7 días tras la rotación).</summary>
public sealed record SyncSigningKeyState(string? Current, string? Previous, DateTimeOffset? RotatedAt)
{
    /// <summary>Días en que se sigue aceptando la clave anterior (paquetes exportados antes de rotar).</summary>
    public const int PreviousKeyDays = 7;

    public bool HasKey => Current is not null;

    /// <summary>¿Un paquete firmado con <paramref name="signer"/> es de esta instalación?</summary>
    public bool Accepts(string? signer, DateTimeOffset now) =>
        signer is not null
        && (signer == Current || (signer == Previous && RotatedAt is { } rotated && now - rotated <= TimeSpan.FromDays(PreviousKeyDays)));
}

/// <summary>Lote cargado (para verificarlo o descartarlo).</summary>
public sealed record SyncBatchRecord(Guid BatchId, Guid InstallationId, Guid OrganizationId, string? SignatureStatus, string? SignerKey);

// ------------------------------------------------------------------------------------------------ Puertos

public interface ISyncRepository
{
    /// <summary>
    /// Aplica el lote de forma idempotente (D16-05): cada documento queda con su versión más reciente (acotada a la hora del servidor más una
    /// tolerancia, S2); un lote repetido no se reaplica. <paramref name="signatureStatus"/>: VERIFIED/UNVERIFIED para paquetes, nulo en línea;
    /// <paramref name="signerKey"/>: la clave que firmó el paquete.
    /// </summary>
    Task<SyncAck> ApplyAsync(
        SyncBatch batch, PosInstallationIdentity installation, string via, Guid? uploadedBy, CancellationToken cancellationToken, string? signatureStatus = null,
        string? signerKey = null);

    /// <summary>Clave de firma registrada para la instalación (sin clave: <c>Current</c> nulo).</summary>
    Task<SyncSigningKeyState> GetSigningKeyAsync(Guid installationId, CancellationToken cancellationToken);

    /// <summary>Registra o rota la clave de firma (la anterior queda en <c>previous_key</c>). Devuelve la clave que reemplazó, si había otra.</summary>
    Task<string?> RegisterSigningKeyAsync(PosInstallationIdentity installation, string publicKey, CancellationToken cancellationToken);

    /// <summary>Reinicia la clave (vigente y anterior en nulo): la próxima sincronización en línea registra una nueva.</summary>
    Task<bool> ResetSigningKeyAsync(Guid installationId, Guid? resetBy, CancellationToken cancellationToken);

    Task<IReadOnlyList<UnverifiedBatchDto>> UnverifiedBatchesAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<SyncBatchRecord?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>Empresa de la instalación (nula si la nube no la conoce).</summary>
    Task<Guid?> InstallationOrganizationAsync(Guid installationId, CancellationToken cancellationToken);

    /// <summary>Marca el lote como verificado (la firma coincide con la clave registrada).</summary>
    Task MarkVerifiedAsync(Guid batchId, Guid? reviewedBy, CancellationToken cancellationToken);

    /// <summary>
    /// Descarta el lote: los documentos que REEMPLAZÓ vuelven a su versión anterior y los que CREÓ se borran (solo los que ningún lote
    /// posterior reescribió). Devuelve cuántos documentos restauró o borró.
    /// </summary>
    Task<int> DiscardAsync(Guid batchId, Guid? reviewedBy, CancellationToken cancellationToken);

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

/// <summary>
/// Límite de cargas de paquetes por usuario del portal (abrir un paquete descifra y descomprime hasta 128 MiB): ⚙️ 10 cada 10 minutos
/// (<c>Sync:MaxUploadsPer10Minutes</c>).
/// En memoria: un solo servidor.
/// </summary>
public sealed class SyncUploadThrottle(IClock clock, int maxUploads = SyncUploadThrottle.DefaultMaxUploads)
{
    public const int DefaultMaxUploads = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _uploads = new();

    /// <summary>Registra el intento; <c>false</c> si el usuario ya llegó al máximo en la ventana.</summary>
    public bool TryAcquire(Guid userId)
    {
        var now = clock.UtcNow;
        var queue = _uploads.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= Math.Max(1, maxUploads))
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}

public static class SyncErrors
{
    public static readonly Error Unauthorized = Error.Unauthorized(
        "SYNC.UNAUTHORIZED", "La tienda no se pudo identificar: token de licencia inválido, licencia inactiva o equipo distinto al activado.");
    public static readonly Error Forbidden = Error.Forbidden("SYNC.FORBIDDEN", "No tiene acceso a los datos de esa empresa.");
    public static readonly Error NoPrivateKey = Error.BusinessRule("SYNC.NO_PRIVATE_KEY", "La nube no tiene configurada su clave de sincronización (Sync:PrivateKeyPath).");
    public static readonly Error UnknownInstallation = Error.BusinessRule("SYNC.UNKNOWN_INSTALLATION", "El paquete es de una instalación que la nube no conoce.");
    public static readonly Error ReleasedInstallation = Error.BusinessRule(
        "SYNC.INSTALLATION_RELEASED", "El paquete es de un equipo que ya fue liberado: sus datos no se cargan. Expórtelo desde el equipo activado.");
    public static readonly Error LicenseInactive = Error.BusinessRule(
        "SYNC.LICENSE_INACTIVE", "La licencia de esa tienda está vencida, revocada o reemplazada: sus paquetes no se cargan hasta que la licencia esté vigente.");
    public static readonly Error TooLarge = Error.Validation("SYNC.BATCH_TOO_LARGE", "El lote supera el máximo de datos por envío.");
    public static readonly Error ItemTooLarge = Error.Validation("SYNC.ITEM_TOO_LARGE", "Un dato del lote supera el tamaño máximo permitido.");
    public static readonly Error InvalidSigningKey = Error.Validation(
        "SYNC.INVALID_SIGNING_KEY", "La clave de firma de la tienda o su prueba de posesión no son válidas.");
    public static readonly Error WrongSigner = Error.Validation(
        "SYNC.WRONG_SIGNER",
        "El paquete no está firmado con la clave registrada de esa tienda: puede haber sido fabricado o alterado. No se cargó nada. Si es de la " +
        "tienda (p. ej. la reinstalaron o cambió de equipo), use \"Reiniciar clave\" en Tiendas (la tienda vuelve a registrar su clave al " +
        "sincronizar en línea) y vuelva a cargar el MISMO archivo.");
    public static readonly Error TooManyUploads = Error.Conflict(
        "SYNC.TOO_MANY_UPLOADS", "Demasiados paquetes cargados en poco tiempo. Espere unos minutos e intente de nuevo.");
    public static readonly Error BatchNotFound = Error.NotFound("SYNC.BATCH_NOT_FOUND", "El lote no existe.");
    public static readonly Error NotUnverified = Error.BusinessRule("SYNC.NOT_UNVERIFIED", "Solo se revisan los paquetes que quedaron sin verificar.");
    public static readonly Error CannotVerify = Error.BusinessRule(
        "SYNC.CANNOT_VERIFY",
        "No se puede verificar: la tienda aún no registró su clave, o el paquete no venía firmado o lo firmó otra clave. Descártelo si no lo reconoce.");

    public static Error InvalidPackage(string message) => Error.Validation("SYNC.INVALID_PACKAGE", message);

    /// <summary>Error de una instalación que no puede recibir paquetes según el motivo de <see cref="IPosInstallationAuthenticator.InactiveReasonAsync"/>.</summary>
    public static Error ForInactive(string? reason) => reason switch
    {
        PosInstallationInactiveReasons.Released => ReleasedInstallation,
        PosInstallationInactiveReasons.LicenseInactive => LicenseInactive,
        _ => UnknownInstallation,
    };
}

// ------------------------------------------------------------------------------------------------ API del POS

/// <summary>
/// Lote enviado por una tienda en línea (autenticado con el token de licencia y la huella, D16-03). Desde la revisión S2 trae además la clave
/// pública de firma de la instalación y una prueba de posesión: la nube la registra para verificar los paquetes <c>.possync</c>.
/// </summary>
public sealed record ReceiveBatchCommand(SyncBatch Batch, string Token, string Fingerprint, string? SigningKey = null, string? SigningKeyProof = null)
    : ICommand<SyncAck>;

internal sealed class ReceiveBatchHandler(IPosInstallationAuthenticator authenticator, ISyncRepository repository, IAuditWriter audit)
    : ICommandHandler<ReceiveBatchCommand, SyncAck>
{
    public const int MaxItems = SyncLimits.MaxOnlineItems;

    public async Task<Result<SyncAck>> Handle(ReceiveBatchCommand command, CancellationToken cancellationToken)
    {
        if (command.Batch.Items is null || command.Batch.Items.Count > MaxItems)
        {
            return SyncErrors.TooLarge;
        }

        if (command.Batch.Items.Any(i => SyncLimits.DataBytes(i.Data) > SyncLimits.MaxItemBytes))
        {
            return SyncErrors.ItemTooLarge;
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

            var state = await repository.GetSigningKeyAsync(installation.InstallationId, cancellationToken);
            if (state.Current != key)
            {
                var replaced = await repository.RegisterSigningKeyAsync(installation, key, cancellationToken);
                await audit.WriteAsync(
                    replaced is null
                        ? new AuditEntry("sync", "SYNC_KEY_REGISTERED", "Installation", installation.InstallationId, installation.BranchName,
                            $"La tienda {installation.OrganizationName} · {installation.BranchName ?? "—"} registró su clave de firma de paquetes ({Short(key)}).")
                        : new AuditEntry("sync", "SYNC_KEY_ROTATED", "Installation", installation.InstallationId, installation.BranchName,
                            $"La tienda {installation.OrganizationName} · {installation.BranchName ?? "—"} cambió su clave de firma de paquetes ({Short(replaced)} → " +
                            $"{Short(key)}); la anterior se acepta {SyncSigningKeyState.PreviousKeyDays} días más.",
                            Severity: AuditSeverity.Warning),
                    cancellationToken);
            }
        }

        return await repository.ApplyAsync(command.Batch, installation, "ONLINE", null, cancellationToken);
    }

    internal static string Short(string key) => key.Length > 8 ? key[..8] + "…" : key;
}

// ------------------------------------------------------------------------------------------------ Portal

[RequiresPermission(CloudPermissions.SyncPackageUpload)]
public sealed record UploadPackageCommand(byte[] Package) : ICommand<UploadResultDto>;

internal sealed class UploadPackageHandler(
    ISyncPackageOpener opener, IPosInstallationAuthenticator authenticator, ISyncAccess access, ISyncRepository repository, IPortalUserContext user,
    SyncUploadThrottle throttle, IClock clock)
    : ICommandHandler<UploadPackageCommand, UploadResultDto>
{
    public async Task<Result<UploadResultDto>> Handle(UploadPackageCommand command, CancellationToken cancellationToken)
    {
        if (!opener.IsConfigured)
        {
            return SyncErrors.NoPrivateKey;
        }

        if (user.UserId is { } userId && !throttle.TryAcquire(userId))
        {
            return SyncErrors.TooManyUploads;
        }

        // El encabezado (en claro, autenticado con el cifrado) dice de qué instalación es: el acceso se revisa ANTES de descifrar y
        // descomprimir todo. Abrir el paquete confirma después que el encabezado es el de su contenido.
        SyncPackage.Header header;
        try
        {
            header = SyncPackage.ReadHeader(command.Package);
        }
        catch (SyncPackageException ex)
        {
            return SyncErrors.InvalidPackage(ex.Message);
        }

        if (await authenticator.FindAsync(header.InstallationId, cancellationToken) is not { } installation)
        {
            return SyncErrors.ForInactive(await authenticator.InactiveReasonAsync(header.InstallationId, cancellationToken));
        }

        if (!await access.CanAccessAsync(installation.OrganizationId, cancellationToken))
        {
            return SyncErrors.Forbidden;
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

        // Con clave registrada, el paquete DEBE venir firmado con ella (o con la anterior, hasta 7 días después de rotar). Sin clave (tienda que
        // nunca sincronizó en línea con una versión que firma) se aplica por compatibilidad y queda UNVERIFIED: se puede verificar o descartar
        // cuando la tienda registre su clave.
        var key = await repository.GetSigningKeyAsync(installation.InstallationId, cancellationToken);
        if (key.HasKey && !key.Accepts(opened.SignerPublicKey, clock.UtcNow))
        {
            return SyncErrors.WrongSigner;
        }

        var status = key.HasKey ? "VERIFIED" : "UNVERIFIED";
        var ack = await repository.ApplyAsync(opened.Batch, installation, "FILE", user.UserId, cancellationToken, status, opened.SignerPublicKey);
        return new UploadResultDto(installation.OrganizationName, installation.BranchName, ack.Received, ack.Applied, ack.Ignored, ack.Duplicate, !key.HasKey);
    }
}

/// <summary>
/// Verificar un paquete que se cargó sin verificar: si la tienda ya registró su clave y es la que firmó el paquete, queda VERIFIED.
/// </summary>
[RequiresPermission(CloudPermissions.SyncPackageReview)]
public sealed record VerifySyncBatchCommand(Guid BatchId) : ICommand;

internal sealed class VerifySyncBatchHandler(ISyncRepository repository, ISyncAccess access, IPortalUserContext user, IAuditWriter audit, IClock clock)
    : ICommandHandler<VerifySyncBatchCommand>
{
    public async Task<Result> Handle(VerifySyncBatchCommand command, CancellationToken cancellationToken)
    {
        if (await repository.GetBatchAsync(command.BatchId, cancellationToken) is not { } batch)
        {
            return SyncErrors.BatchNotFound;
        }

        if (await SyncReview.CheckAsync(repository, access, batch.InstallationId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (batch.SignatureStatus != "UNVERIFIED")
        {
            return SyncErrors.NotUnverified;
        }

        var key = await repository.GetSigningKeyAsync(batch.InstallationId, cancellationToken);
        if (!key.HasKey || !key.Accepts(batch.SignerKey, clock.UtcNow))
        {
            return SyncErrors.CannotVerify;
        }

        await repository.MarkVerifiedAsync(batch.BatchId, user.UserId, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("sync", "SYNC_BATCH_VERIFIED", "SyncBatch", batch.BatchId, null,
                $"{user.DisplayName ?? "Un usuario"} verificó el paquete {batch.BatchId}: lo firmó la clave registrada de la tienda."),
            cancellationToken);
        return Result.Success();
    }
}

/// <summary>Descartar un paquete sin verificar (no se reconoce): se borran los documentos que dejó y que ningún otro lote reescribió.</summary>
[RequiresPermission(CloudPermissions.SyncPackageReview)]
public sealed record DiscardSyncBatchCommand(Guid BatchId) : ICommand<int>;

internal sealed class DiscardSyncBatchHandler(ISyncRepository repository, ISyncAccess access, IPortalUserContext user, IAuditWriter audit)
    : ICommandHandler<DiscardSyncBatchCommand, int>
{
    public async Task<Result<int>> Handle(DiscardSyncBatchCommand command, CancellationToken cancellationToken)
    {
        if (await repository.GetBatchAsync(command.BatchId, cancellationToken) is not { } batch)
        {
            return SyncErrors.BatchNotFound;
        }

        if (await SyncReview.CheckAsync(repository, access, batch.InstallationId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (batch.SignatureStatus != "UNVERIFIED")
        {
            return SyncErrors.NotUnverified;
        }

        var deleted = await repository.DiscardAsync(batch.BatchId, user.UserId, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("sync", "SYNC_BATCH_DISCARDED", "SyncBatch", batch.BatchId, null,
                $"{user.DisplayName ?? "Un usuario"} descartó el paquete sin verificar {batch.BatchId}: {deleted} documento(s) volvieron a su versión anterior o se borraron.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return deleted;
    }
}

/// <summary>
/// Reiniciar la clave de firma de una tienda (perdió el equipo, se restauró en otro, la clave quedó expuesta): la próxima sincronización en
/// línea registra una nueva; mientras tanto sus paquetes quedan sin verificar.
/// </summary>
[RequiresPermission(CloudPermissions.SyncPackageReview)]
public sealed record ResetSyncSigningKeyCommand(Guid InstallationId, string Reason) : ICommand;

internal sealed class ResetSyncSigningKeyHandler(ISyncRepository repository, ISyncAccess access, IPortalUserContext user, IAuditWriter audit)
    : ICommandHandler<ResetSyncSigningKeyCommand>
{
    public static readonly Error ReasonRequired = Error.Validation("SYNC.REASON_REQUIRED", "Escriba el motivo (de 5 a 300 caracteres).");
    public static readonly Error NoKey = Error.NotFound("SYNC.NO_SIGNING_KEY", "Esa tienda no tiene clave de firma registrada.");

    public async Task<Result> Handle(ResetSyncSigningKeyCommand command, CancellationToken cancellationToken)
    {
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length is < 5 or > 300)
        {
            return ReasonRequired;
        }

        if (await SyncReview.CheckAsync(repository, access, command.InstallationId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await repository.ResetSigningKeyAsync(command.InstallationId, user.UserId, cancellationToken))
        {
            return NoKey;
        }

        await audit.WriteAsync(
            new AuditEntry("sync", "SYNC_KEY_RESET", "Installation", command.InstallationId, null,
                $"{user.DisplayName ?? "Un usuario"} reinició la clave de firma de paquetes de la instalación {command.InstallationId}: {reason}",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }
}

/// <summary>Revisar un lote o una clave exige que la instalación exista y que el usuario tenga acceso a su empresa.</summary>
internal static class SyncReview
{
    public static async Task<Error?> CheckAsync(ISyncRepository repository, ISyncAccess access, Guid installationId, CancellationToken cancellationToken)
    {
        if (await repository.InstallationOrganizationAsync(installationId, cancellationToken) is not { } organizationId)
        {
            return SyncErrors.UnknownInstallation;
        }

        return await access.CanAccessAsync(organizationId, cancellationToken) ? null : SyncErrors.Forbidden;
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

/// <summary>Todo lo que el portal muestra de una empresa (una sola consulta por pantalla), incluidos los paquetes sin verificar.</summary>
public sealed record StoreDataDto(
    IReadOnlyList<SyncInstallationStatusDto> Installations,
    IReadOnlyList<SalesDayDto> SalesByDay,
    IReadOnlyList<SaleRowDto> RecentSales,
    IReadOnlyList<CashSessionRowDto> CashSessions,
    IReadOnlyList<UnverifiedBatchDto>? UnverifiedBatches = null);

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
            await repository.CashSessionsAsync(query.OrganizationId, 50, cancellationToken),
            await repository.UnverifiedBatchesAsync(query.OrganizationId, cancellationToken));
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
