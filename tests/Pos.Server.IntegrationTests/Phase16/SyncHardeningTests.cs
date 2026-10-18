using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using Pos.Modules.Licensing.Contracts;
using Pos.Modules.Sync.Application;
using Pos.Modules.Sync.Contracts;
using Pos.Modules.Sync.Infrastructure;
using Pos.Sync.Contracts;

namespace Pos.Server.IntegrationTests.Phase16;

/// <summary>
/// Revisión, ronda 3: un dato de más de 1 MiB se salta con aviso (no traba el lote) y la clave de firma solo se regenera si el archivo no se
/// puede descifrar (con copia .bak); un error de disco falla el ciclo y se reintenta.
/// </summary>
public class SyncHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Un_dato_demasiado_grande_se_salta_con_aviso_y_no_traba_el_lote()
    {
        var feeds = new FakeFeeds();
        feeds.Add(SyncKinds.Product, "pequeno", 100);
        feeds.Add(SyncKinds.Product, "enorme", SyncLimits.MaxItemBytes + 10);
        var store = new FakeStore();
        var cloud = new FakeCloud();
        using var signer = new FakeSigner();
        var service = new SyncService(feeds, store, cloud, new FakeCredentials(), new FakeEnvironment(), signer);

        (await service.PushAsync(5, Ct)).ShouldBe(1);
        cloud.Sent.SelectMany(b => b.Items).Select(i => i.Id).ShouldBe(["pequeno"]);
        store.Acked[SyncKinds.Product].Id.ShouldBe(feeds.IdOf("enorme")); // el cursor pasó el dato grande
        store.Batches.ShouldContain(b => b.Status == "ACKED" && b.Error!.Contains("enorme", StringComparison.Ordinal));
        (await service.StatusAsync(Ct)).Warnings!.ShouldHaveSingleItem().ShouldContain("1 MiB");

        // Solo queda un dato grande: se salta igual (lote de 0 datos con el aviso) y el envío no se queda dando vueltas.
        feeds.Add(SyncKinds.Product, "otro-enorme", SyncLimits.MaxItemBytes + 10);
        (await service.PushAsync(5, Ct)).ShouldBe(0);
        store.Acked[SyncKinds.Product].Id.ShouldBe(feeds.IdOf("otro-enorme"));
        cloud.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public void La_clave_ilegible_se_guarda_como_bak_y_se_regenera_pero_un_error_de_disco_no()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pos-sync-key-" + Guid.CreateVersion7().ToString("N"));
        var file = Path.Combine(directory, "sync-signing.key");
        try
        {
            using (var first = new SyncSigningKeyStore(file, NullLogger<SyncSigningKeyStore>.Instance))
            {
                var original = first.PublicKey;
                using var again = new SyncSigningKeyStore(file, NullLogger<SyncSigningKeyStore>.Instance);
                again.PublicKey.ShouldBe(original); // se lee la misma
            }

            // Archivo dañado: copia .bak y clave nueva.
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            using (var repaired = new SyncSigningKeyStore(file, NullLogger<SyncSigningKeyStore>.Instance))
            {
                SyncSigning.IsPublicKey(repaired.PublicKey).ShouldBeTrue();
                File.ReadAllBytes(file + ".bak").ShouldBe([1, 2, 3, 4]);
            }

            // Archivo bloqueado (error de disco): falla este intento, no toca la clave, y el siguiente funciona.
            var before = File.ReadAllBytes(file);
            using var locked = new SyncSigningKeyStore(file, NullLogger<SyncSigningKeyStore>.Instance);
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Should.Throw<IOException>(() => locked.PublicKey);
            }

            File.ReadAllBytes(file).ShouldBe(before);
            SyncSigning.IsPublicKey(locked.PublicKey).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeFeeds : ISyncFeeds
    {
        private readonly List<(string Kind, string Id, Guid Key, int Bytes)> _rows = [];
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddHours(-1);

        public void Add(string kind, string id, int bytes) => _rows.Add((kind, id, Guid.CreateVersion7(), bytes));

        public Guid IdOf(string id) => _rows.Single(r => r.Id == id).Key;

        public Task<IReadOnlyList<FeedItem>> ReadAsync(string kind, CursorPosition after, int limit, CancellationToken cancellationToken)
        {
            var items = _rows.Select((r, i) => (Row: r, Position: new CursorPosition(_start.AddSeconds(i), r.Key)))
                .Where(x => x.Row.Kind == kind && (x.Position.Timestamp > after.Timestamp))
                .Take(limit)
                .Select(x =>
                {
                    using var data = JsonDocument.Parse(JsonSerializer.Serialize(new { relleno = new string('x', x.Row.Bytes) }));
                    return new FeedItem(new SyncItem(kind, x.Row.Id, x.Position.Timestamp, null, data.RootElement.Clone()), x.Position);
                })
                .ToList();
            return Task.FromResult<IReadOnlyList<FeedItem>>(items);
        }

        public Task<int> CountAsync(string kind, CursorPosition after, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class FakeStore : ISyncStore
    {
        public Dictionary<string, CursorPosition> Acked { get; } = new(StringComparer.Ordinal);

        public List<SyncBatchDto> Batches { get; } = [];

        public Task<IReadOnlyDictionary<string, SyncCursor>> CursorsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, SyncCursor>>(
                Acked.ToDictionary(a => a.Key, a => new SyncCursor(a.Key, a.Value, CursorPosition.Start), StringComparer.Ordinal));

        public Task SaveAckedAsync(string kind, CursorPosition position, CancellationToken cancellationToken)
        {
            Acked[kind] = position;
            return Task.CompletedTask;
        }

        public Task SaveExportedAsync(string kind, CursorPosition position, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordBatchAsync(SyncBatchDto batch, CancellationToken cancellationToken)
        {
            Batches.Insert(0, batch);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SyncBatchDto>> ListBatchesAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SyncBatchDto>>([.. Batches.Take(limit)]);
    }

    private sealed class FakeCloud : ISyncCloud
    {
        public List<SyncBatch> Sent { get; } = [];

        public bool IsConfigured => true;

        public string? CloudPublicKey => null;

        public Task<SyncSendResult> SendAsync(SyncBatch batch, LicenseCredentials credentials, CancellationToken cancellationToken)
        {
            Sent.Add(batch);
            return Task.FromResult(new SyncSendResult(new SyncAck(batch.BatchId, batch.Items.Count, batch.Items.Count, 0, false), null));
        }
    }

    private sealed class FakeCredentials : ILicenseCredentials
    {
        public Task<LicenseCredentials?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<LicenseCredentials?>(new("token", "huella"));
    }

    private sealed class FakeEnvironment : ISyncEnvironment
    {
        public Guid InstallationId { get; } = Guid.CreateVersion7();

        public string AppVersion => "1.0.0";

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class FakeSigner : ISyncSigner, IDisposable
    {
        public Key Key { get; } = SyncSigning.GenerateKey();

        public string PublicKey => SyncSigning.PublicOf(Key);

        public void Dispose() => Key.Dispose();
    }
}
