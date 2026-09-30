using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Sync.Application;
using Pos.Sync.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>Verificación de coherencia de la Fase 16 en la nube: recepción autenticada e idempotente de lotes y usuario Cliente por cuenta.</summary>
public class SyncTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    [Fact]
    public async Task El_lote_se_recibe_autenticado_una_sola_vez_y_conserva_la_version_mas_reciente()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var pos = cloud.NewPos(customer.Nit);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();

        var saleId = Guid.CreateVersion7().ToString();
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        var batch = Batch(pos.Identity.InstallationId, Sale(saleId, at, 12_500m, "COMPLETED"));

        // Sin token o con la huella de otro equipo: rechazado.
        var client = cloud.Factory.CreateClient();
        (await client.PostAsJsonAsync(SyncRoutes.Batches, batch, SyncJson.Options, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(client, batch, pos.Token!, "fp1.otro.equipo.x")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var ack = await AckAsync(client, batch, pos.Token!, pos.Identity.Fingerprint);
        (ack.Received, ack.Applied, ack.Duplicate).ShouldBe((1, 1, false));
        (await AckAsync(client, batch, pos.Token!, pos.Identity.Fingerprint)).Duplicate.ShouldBeTrue();

        // Otro lote con la misma venta: una versión más vieja se ignora; la anulación (más nueva) la reemplaza.
        (await AckAsync(client, Batch(pos.Identity.InstallationId, Sale(saleId, at.AddMinutes(-1), 1m, "COMPLETED")), pos.Token!, pos.Identity.Fingerprint))
            .Ignored.ShouldBe(1);
        (await AckAsync(client, Batch(pos.Identity.InstallationId, Sale(saleId, at.AddMinutes(1), 12_500m, "VOIDED")), pos.Token!, pos.Identity.Fingerprint))
            .Applied.ShouldBe(1);

        await using var scope = cloud.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISyncRepository>();
        (await repository.OrganizationsAsync(customer.AccountId, Ct)).ShouldHaveSingleItem().Id.ShouldBe(customer.OrganizationId);
        var day = (await repository.SalesByDayAsync(customer.OrganizationId, Today, Today, Ct))
            .ShouldHaveSingleItem();
        (day.Tickets, day.Voided).ShouldBe((0, 1));
        (await repository.StatusAsync(customer.OrganizationId, Ct)).ShouldHaveSingleItem().Sales.ShouldBe(1);

        // El usuario Cliente se crea atado a su cuenta; sin cuenta no se puede.
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, "/admin/users",
            new { email = $"cliente-{Guid.NewGuid():N}@cliente.co", displayName = "Cliente", role = PortalRoles.Customer })).Status.ShouldNotBe(HttpStatusCode.OK);
        (await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, PortalRoles.Customer, customer.AccountId)).Client.ShouldNotBeNull();
    }

    private static SyncBatch Batch(Guid installation, SyncItem item) =>
        new(Guid.CreateVersion7(), installation, DateTimeOffset.UtcNow, "1.0.0", [item]);

    private static SyncItem Sale(string id, DateTimeOffset version, decimal total, string status)
    {
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            number = "S-1", status, branch = "Principal", branchCode = "PRINCIPAL", terminal = "CAJA-01", cashier = "Cajera", total,
            completedAt = version, businessDate = Today,
        }, SyncJson.Options));
        return new SyncItem(SyncKinds.Sale, id, version, Today, data.RootElement.Clone());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, SyncBatch batch, string token, string fingerprint)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncRoutes.Batches) { Content = JsonContent.Create(batch, options: SyncJson.Options) };
        request.Headers.Authorization = new AuthenticationHeaderValue(SyncRoutes.AuthorizationScheme, token);
        request.Headers.Add(SyncRoutes.FingerprintHeader, fingerprint);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<SyncAck> AckAsync(HttpClient client, SyncBatch batch, string token, string fingerprint)
    {
        var response = await SendAsync(client, batch, token, fingerprint);
        await PortalApi.EnsureAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SyncAck>(SyncJson.Options, Ct))!;
    }
}
