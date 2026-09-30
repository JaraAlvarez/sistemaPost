using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using Pos.Modules.Licensing.Contracts;
using Pos.Modules.Sync.Application;
using Pos.Modules.Sync.Contracts;
using Pos.Modules.Sync.Infrastructure;
using Pos.Server.IntegrationTests.Phase12;
using Pos.Server.IntegrationTests.Phase7;
using Pos.Sync.Contracts;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase16;

/// <summary>Nube de sincronización en memoria: guarda los lotes recibidos y responde el acuse (o falla, si la prueba lo pide).</summary>
internal sealed class FakeSyncCloud : HttpMessageHandler
{
    public List<SyncBatch> Batches { get; } = [];

    public List<(string? Authorization, string? Fingerprint)> Headers { get; } = [];

    public bool Offline { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("Sin Internet (simulado).");
        }

        var batch = (await request.Content!.ReadFromJsonAsync<SyncBatch>(SyncJson.Options, cancellationToken))!;
        Batches.Add(batch);
        Headers.Add((request.Headers.Authorization?.ToString(), request.Headers.GetValues(SyncRoutes.FingerprintHeader).SingleOrDefault()));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new SyncAck(batch.BatchId, batch.Items.Count, batch.Items.Count, 0, false), options: SyncJson.Options),
        };
    }
}

/// <summary>Caja Única con licencia simulada y nube de sincronización en memoria (margen de seguridad en 0 para no esperar).</summary>
public sealed class SyncServerFactory : LicenseServerFactory
{
    internal FakeSyncCloud SyncCloud { get; } = new();

    internal (Key Private, string Public) CloudKey { get; } = SyncPackage.GenerateKeyPair();

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        .. base.ExtraSettings,
        new("Pos:Sync:SafetyLagSeconds", "0"),
        new("Pos:Sync:IntervalSeconds", "3600"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<ISyncCloud>(new SyncCloudClient(
            new HttpClient(SyncCloud) { BaseAddress = new Uri("https://nube.test/") }, CloudKey.Public, NullLogger<SyncCloudClient>.Instance))));
    }
}

/// <summary>Verificación de coherencia de la Fase 16 en la tienda (docs/fases/fase-16-propuesta.md §9).</summary>
public class SyncApiTests
{
    [Fact]
    public void El_paquete_solo_lo_abre_la_nube_y_detecta_alteraciones()
    {
        var (cloudKey, cloudPublic) = SyncPackage.GenerateKeyPair();
        using var data = System.Text.Json.JsonDocument.Parse("""{"total": 12500}""");
        var batch = new SyncBatch(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow, "1.0.0",
            [new SyncItem(SyncKinds.Sale, Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow, new DateOnly(2026, 9, 30), data.RootElement.Clone())]);

        var package = SyncPackage.Seal(batch, cloudPublic);
        SyncPackage.ReadHeader(package).InstallationId.ShouldBe(batch.InstallationId);
        Encoding.UTF8.GetString(package).ShouldNotContain("12500"); // cifrado

        var opened = SyncPackage.Open(package, cloudKey);
        opened.BatchId.ShouldBe(batch.BatchId);
        opened.Items.Single().Data.GetProperty("total").GetInt32().ShouldBe(12500);

        using var other = SyncPackage.GenerateKeyPair().Private;
        Should.Throw<SyncPackageException>(() => SyncPackage.Open(package, other));
        var tampered = (byte[])package.Clone();
        tampered[^5] ^= 0x01;
        Should.Throw<SyncPackageException>(() => SyncPackage.Open(tampered, cloudKey));
        Should.Throw<SyncPackageException>(() => SyncPackage.Open("no es un paquete"u8.ToArray(), cloudKey));
        cloudKey.Dispose();
    }

    [Fact]
    public async Task La_venta_sube_con_acuse_una_sola_vez_y_el_paquete_lleva_lo_no_exportado()
    {
        await using var factory = new SyncServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);

        // Sin licencia activada la tienda no se puede identificar ante la nube.
        var status = await GetAsync<SyncStatusDto>(shop.Owner, "/api/v1/sync");
        status.Configured.ShouldBeFalse();
        (await RawAsync(shop.Cashier, HttpMethod.Get, "/api/v1/sync")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/activate", new { licenseKey = FakeLicenseCloud.ValidKey });

        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 2);
        await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 10_000m)]);

        status = await GetAsync<SyncStatusDto>(shop.Owner, "/api/v1/sync");
        status.Configured.ShouldBeTrue();
        status.Pending[SyncKinds.Sale].ShouldBe(1);

        // Sin Internet: no avanza nada y queda el intento fallido.
        factory.SyncCloud.Offline = true;
        (await PostAsync<PushResult>(shop.Owner, "/api/v1/sync/push")).Confirmed.ShouldBe(0);
        status = await GetAsync<SyncStatusDto>(shop.Owner, "/api/v1/sync");
        status.Pending[SyncKinds.Sale].ShouldBe(1);
        status.Batches[0].Status.ShouldBe("FAILED");

        // Con Internet: sube con el token de licencia y la huella; el cursor avanza solo con el acuse.
        factory.SyncCloud.Offline = false;
        (await PostAsync<PushResult>(shop.Owner, "/api/v1/sync/push")).Confirmed.ShouldBeGreaterThan(0);
        var sent = factory.SyncCloud.Batches.Single();
        var saleItem = sent.Items.Single(i => i.Kind == SyncKinds.Sale);
        saleItem.Id.ShouldBe(sale.Id.ToString());
        saleItem.Data.GetProperty("total").GetDecimal().ShouldBe(sale.Total);
        saleItem.Data.GetProperty("lines").GetArrayLength().ShouldBe(1);
        sent.Items.ShouldContain(i => i.Kind == SyncKinds.Stock);
        sent.Items.ShouldContain(i => i.Kind == SyncKinds.Product);
        factory.SyncCloud.Headers.Single().Authorization.ShouldStartWith(SyncRoutes.AuthorizationScheme + " ");
        factory.SyncCloud.Headers.Single().Fingerprint.ShouldNotBeNullOrEmpty();

        status = await GetAsync<SyncStatusDto>(shop.Owner, "/api/v1/sync");
        status.Pending.Values.Sum().ShouldBe(0);
        status.LastAckAt.ShouldNotBeNull();
        (await PostAsync<PushResult>(shop.Owner, "/api/v1/sync/push")).Confirmed.ShouldBe(0);
        factory.SyncCloud.Batches.Count.ShouldBe(1);

        // Otra venta sin Internet: el paquete lleva solo lo no confirmado y la nube lo abre con su clave privada.
        var second = await shop.StartAsync();
        second = await shop.AddAsync(second.Id, shop.Soda, 1);
        await shop.CompleteAsync(second.Id, [Pay(shop.Cash, 5_000m)]);
        var export = await shop.Owner.GetAsync("/api/v1/sync/export", Ct);
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.Content.Headers.ContentDisposition!.FileName!.ShouldContain(".possync");
        var package = SyncPackage.Open(await export.Content.ReadAsByteArrayAsync(Ct), factory.CloudKey.Private);
        package.Items.Where(i => i.Kind == SyncKinds.Sale).Select(i => i.Id).ShouldBe([second.Id.ToString()]);

        // Un segundo paquete ya no repite lo exportado; el envío en línea sí lo sube (el cursor en línea es independiente).
        var again = SyncPackage.Open(await shop.Owner.GetByteArrayAsync("/api/v1/sync/export", Ct), factory.CloudKey.Private);
        again.Items.ShouldNotContain(i => i.Kind == SyncKinds.Sale);
        (await PostAsync<PushResult>(shop.Owner, "/api/v1/sync/push")).Confirmed.ShouldBeGreaterThan(0);
        factory.SyncCloud.Batches[^1].Items.ShouldContain(i => i.Kind == SyncKinds.Sale && i.Id == second.Id.ToString());
    }

    private sealed record PushResult(int Confirmed);
}
