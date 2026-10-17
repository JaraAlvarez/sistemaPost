using System.Net;
using Pos.Modules.Billing.Contracts;
using Pos.Server.IntegrationTests.Phase11B;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase11B.ElectronicBilling;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.PantallasAdmin;

/// <summary>
/// Salvaguardas de la cola de facturación electrónica con Factus como adaptador por defecto (PA1): con el modo OFF no sale nada aunque
/// haya llaves y documentos en cola; una instalación "vieja" encendida sin verificación no envía; los documentos de antes de la primera
/// verificación correcta esperan "Enviar ahora" o "No enviar" (NOT_SENT, sin reintentos), con permiso y auditoría.
/// </summary>
public class FacturacionColaSeguraTests
{
    private static Task<FiscalConnectionVerificationDto> VerifyAsync(HttpClient client) =>
        PostAsync<FiscalConnectionVerificationDto>(client, "/api/v1/billing/settings/verify");

    private static Task<BillingSettingsDto> ModeAsync(HttpClient client, string mode) =>
        SendAsync<BillingSettingsDto>(client, HttpMethod.Put, "/api/v1/billing/settings", new { mode, environment = "SANDBOX" });

    /// <summary>Instalación anterior a esta versión: llaves y modo encendido SIN verificación (lo que la migración V060 apaga).</summary>
    private static async Task LegacyOnAsync(SalesScenario shop)
    {
        await CredentialsAsync(shop.Owner);
        await shop.Catalog.ExecuteAsync("UPDATE billing.provider_settings SET mode = 'EVERY_SALE'");
    }

    [Fact]
    public async Task Con_el_modo_OFF_y_llaves_verificadas_la_cola_no_envia_ni_lo_que_ya_estaba_en_cola()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop);

        var sale = await SellAsync(shop);
        sale.DocumentStatus.ShouldBe("PENDING");
        await ModeAsync(shop.Owner, "OFF");
        (await ProcessAsync(shop)).StoppedBecause.ShouldBe("NOT_CONFIGURED");
        factory.Fiscal.Submissions.ShouldBeEmpty();

        // Encendida otra vez (ya verificada): sale lo que estaba en cola.
        await ModeAsync(shop.Owner, "EVERY_SALE");
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task Encendida_sin_verificacion_no_envia_y_el_atraso_espera_Enviar_ahora()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await LegacyOnAsync(shop);

        // Venta con la facturación "encendida" de antes: queda pendiente y no sale (sin verificación).
        var old = await SellAsync(shop);
        old.DocumentStatus.ShouldBe("PENDING");
        (await ProcessAsync(shop)).StoppedBecause.ShouldBe("NOT_CONFIGURED");
        factory.Fiscal.Submissions.ShouldBeEmpty();

        // Conectar (verificar + encender): lo nuevo sale; lo de antes queda retenido y se avisa.
        (await VerifyAsync(shop.Owner)).Ok.ShouldBeTrue();
        await ModeAsync(shop.Owner, "EVERY_SALE");
        var ranges = await PostAsync<List<FiscalRangeDto>>(shop.Owner, "/api/v1/billing/ranges/sync");
        foreach (var range in ranges)
        {
            await AssignAsync(shop, range.Id, shop.Catalog.Setup.BranchId);
        }

        var fresh = await SellAsync(shop);
        await DueNowAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        factory.Fiscal.Invoices.ShouldHaveSingleItem().Header.ReferenceCode.ShouldBe(fresh.Sale.Id.ToString("N"));
        (await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings")).HeldDocuments.ShouldBe(1);
        (await GetAsync<FiscalAlertsDto>(shop.Owner, "/api/v1/billing/alerts")).HeldDocuments.ShouldBe(1);
        await DueNowAsync(shop);
        (await ProcessAsync(shop)).Sent.ShouldBe(0);

        // El supervisor no decide; el dueño "Enviar ahora": sale con su fecha original.
        (await StatusAsync(shop.Supervisor, HttpMethod.Post, "/api/v1/billing/backlog/send")).ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync<int>(shop.Owner, "/api/v1/billing/backlog/send")).ShouldBe(1);
        await DueNowAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        (await ForSourceAsync(shop, old.Sale.Id)).Status.ShouldBe("ACCEPTED");
        (await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings")).HeldDocuments.ShouldBe(0);
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_BACKLOG_RELEASED'")).ShouldBe(1);
    }

    [Fact]
    public async Task No_enviar_deja_el_atraso_como_NOT_SENT_sin_reintentos()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await LegacyOnAsync(shop);
        var old = await SellAsync(shop);

        // Verificar sin encender ya fija el corte; "No enviar" marca lo de antes y la cola nunca lo toma.
        (await VerifyAsync(shop.Owner)).Ok.ShouldBeTrue();
        (await PostAsync<int>(shop.Owner, "/api/v1/billing/backlog/discard")).ShouldBe(1);
        var document = await ForSourceAsync(shop, old.Sale.Id);
        document.Status.ShouldBe("NOT_SENT");
        await RawAsync(shop.Owner, HttpMethod.Post, $"/api/v1/billing/documents/{document.Id}/retry")
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.NOT_RETRYABLE");

        await ModeAsync(shop.Owner, "EVERY_SALE");
        await DueNowAsync(shop);
        await ProcessAsync(shop);
        factory.Fiscal.Submissions.ShouldBeEmpty();
        (await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings")).HeldDocuments.ShouldBe(0);
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_BACKLOG_DISCARDED' AND severity = 'WARNING'")).ShouldBe(1);
    }
}
