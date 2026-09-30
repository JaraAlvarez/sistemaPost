using Pos.Application.Abstractions.Security;
using Pos.Modules.Licensing.Contracts;
using Pos.Modules.Sync.Contracts;
using Pos.Sync.Contracts;

namespace Pos.Modules.Sync.Application;

public sealed class SyncPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => SyncPermissions.All;
}

/// <summary>Posición de un cursor: marca de tiempo del último dato y su id (desempate).</summary>
public readonly record struct CursorPosition(DateTimeOffset Timestamp, Guid Id)
{
    public static readonly CursorPosition Start = new(DateTimeOffset.MinValue, Guid.Empty);

    public static CursorPosition Max(CursorPosition a, CursorPosition b) =>
        a.Timestamp > b.Timestamp || (a.Timestamp == b.Timestamp && a.Id.CompareTo(b.Id) >= 0) ? a : b;
}

public sealed record SyncCursor(string Kind, CursorPosition Acked, CursorPosition Exported);

/// <summary>Un dato leído con su posición (para avanzar el cursor al confirmar).</summary>
public sealed record FeedItem(SyncItem Item, CursorPosition Position);

// ------------------------------------------------------------------------------------------------ Puertos

/// <summary>Lectura de las tablas por cursor (D16-01), con un margen de seguridad para no saltar transacciones aún abiertas.</summary>
public interface ISyncFeeds
{
    Task<IReadOnlyList<FeedItem>> ReadAsync(string kind, CursorPosition after, int limit, CancellationToken cancellationToken);

    Task<int> CountAsync(string kind, CursorPosition after, CancellationToken cancellationToken);
}

public interface ISyncStore
{
    Task<IReadOnlyDictionary<string, SyncCursor>> CursorsAsync(CancellationToken cancellationToken);

    Task SaveAckedAsync(string kind, CursorPosition position, CancellationToken cancellationToken);

    Task SaveExportedAsync(string kind, CursorPosition position, CancellationToken cancellationToken);

    Task RecordBatchAsync(SyncBatchDto batch, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncBatchDto>> ListBatchesAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>Resultado del envío: el acuse, un error de la nube o "sin conexión" (<c>Ack</c> y <c>Error</c> nulos).</summary>
public sealed record SyncSendResult(SyncAck? Ack, string? Error);

public interface ISyncCloud
{
    bool IsConfigured { get; }

    /// <summary>Clave pública de sincronización de la nube (para cifrar el paquete); <c>null</c> si no está configurada.</summary>
    string? CloudPublicKey { get; }

    Task<SyncSendResult> SendAsync(SyncBatch batch, LicenseCredentials credentials, CancellationToken cancellationToken);
}

public interface ISyncEnvironment
{
    Guid InstallationId { get; }

    string AppVersion { get; }

    DateTimeOffset UtcNow { get; }
}

// ------------------------------------------------------------------------------------------------ Servicio

/// <summary>
/// Sincronización tienda → nube (docs/fases/fase-16-propuesta.md): arma lotes desde los cursores, los envía y avanza el cursor solo con el
/// acuse (D16-02); el paquete <c>.possync</c> es el mismo lote cifrado para la nube, con su propio cursor de exportación.
/// </summary>
public sealed class SyncService(ISyncFeeds feeds, ISyncStore store, ISyncCloud cloud, ILicenseCredentials credentials, ISyncEnvironment environment)
{
    public const int BatchSize = 500;
    public const int MaxPackageItems = 200_000;

    public async Task<SyncStatusDto> StatusAsync(CancellationToken cancellationToken)
    {
        var cursors = await store.CursorsAsync(cancellationToken);
        var pending = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kind in SyncKinds.All)
        {
            pending[kind] = await feeds.CountAsync(kind, Position(cursors, kind, exported: false), cancellationToken);
        }

        var batches = await store.ListBatchesAsync(30, cancellationToken);
        var reason = await NotConfiguredReasonAsync(cancellationToken);
        return new SyncStatusDto(reason is null, reason, batches.FirstOrDefault(b => b.Status == "ACKED")?.AckedAt,
            batches.Count > 0 && batches[0].Status == "FAILED" ? batches[0].Error : null, pending, batches);
    }

    /// <summary>Envía lo pendiente en lotes de <see cref="BatchSize"/> (hasta <paramref name="maxBatches"/>). Devuelve cuántos datos confirmó la nube.</summary>
    public async Task<int> PushAsync(int maxBatches, CancellationToken cancellationToken)
    {
        if (await credentials.GetAsync(cancellationToken) is not { } credential || !cloud.IsConfigured)
        {
            return 0;
        }

        var confirmed = 0;
        for (var n = 0; n < maxBatches; n++)
        {
            var (batch, positions) = await BuildAsync(exported: false, BatchSize, cancellationToken);
            if (batch.Items.Count == 0)
            {
                break;
            }

            var result = await cloud.SendAsync(batch, credential, cancellationToken);
            if (result.Ack is not { } ack)
            {
                await store.RecordBatchAsync(new SyncBatchDto(batch.BatchId, environment.UtcNow, "ONLINE", batch.Items.Count, "FAILED", null, null,
                    result.Error ?? "Sin conexión con la nube.", null), cancellationToken);
                break;
            }

            foreach (var (kind, position) in positions)
            {
                await store.SaveAckedAsync(kind, position, cancellationToken);
            }

            await store.RecordBatchAsync(new SyncBatchDto(batch.BatchId, environment.UtcNow, "ONLINE", batch.Items.Count, "ACKED", ack.Applied, ack.Ignored, null,
                environment.UtcNow), cancellationToken);
            confirmed += batch.Items.Count;
        }

        return confirmed;
    }

    /// <summary>Paquete <c>.possync</c> con lo que aún no se exportó ni confirmó (D16-04); <c>null</c> si falta la clave de la nube.</summary>
    public async Task<(string FileName, byte[] Content, int Items)?> ExportAsync(CancellationToken cancellationToken)
    {
        if (cloud.CloudPublicKey is not { } publicKey)
        {
            return null;
        }

        var (batch, positions) = await BuildAsync(exported: true, MaxPackageItems, cancellationToken);
        var content = SyncPackage.Seal(batch, publicKey);
        foreach (var (kind, position) in positions)
        {
            await store.SaveExportedAsync(kind, position, cancellationToken);
        }

        await store.RecordBatchAsync(new SyncBatchDto(batch.BatchId, environment.UtcNow, "FILE", batch.Items.Count, "EXPORTED", null, null, null, null),
            cancellationToken);
        return ($"tienda-{environment.UtcNow:yyyyMMdd-HHmm}.possync", content, batch.Items.Count);
    }

    private async Task<string?> NotConfiguredReasonAsync(CancellationToken cancellationToken) =>
        !cloud.IsConfigured ? "Falta la dirección de la nube (Pos:Licensing:ServerUrl)."
        : await credentials.GetAsync(cancellationToken) is null ? "La licencia no está activada: la tienda se identifica ante la nube con su licencia."
        : null;

    private async Task<(SyncBatch Batch, Dictionary<string, CursorPosition> Positions)> BuildAsync(bool exported, int limit, CancellationToken cancellationToken)
    {
        var cursors = await store.CursorsAsync(cancellationToken);
        var items = new List<SyncItem>();
        var positions = new Dictionary<string, CursorPosition>(StringComparer.Ordinal);
        foreach (var kind in SyncKinds.All)
        {
            var remaining = limit - items.Count;
            if (remaining <= 0)
            {
                break;
            }

            var read = await feeds.ReadAsync(kind, Position(cursors, kind, exported), remaining, cancellationToken);
            if (read.Count > 0)
            {
                items.AddRange(read.Select(r => r.Item));
                positions[kind] = read[^1].Position;
            }
        }

        return (new SyncBatch(Guid.CreateVersion7(), environment.InstallationId, environment.UtcNow, environment.AppVersion, items), positions);
    }

    private static CursorPosition Position(IReadOnlyDictionary<string, SyncCursor> cursors, string kind, bool exported) =>
        cursors.TryGetValue(kind, out var cursor) ? exported ? CursorPosition.Max(cursor.Acked, cursor.Exported) : cursor.Acked : CursorPosition.Start;
}
