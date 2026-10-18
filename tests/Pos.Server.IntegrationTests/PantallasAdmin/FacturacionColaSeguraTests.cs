using System.Net;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Sales.Contracts;
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

    /// <summary>Caja Única con la configuración por defecto (sin <c>Pos:Billing:Provider</c>).</summary>
    private sealed class DefaultFactory : PosServerFactory
    {
        protected override string Edition => "SINGLE";
    }

    /// <summary>Caja Única con la facturación prohibida en la instalación (<c>Pos:Billing:Provider = NONE</c>).</summary>
    private sealed class NoneFactory : PosServerFactory
    {
        protected override string Edition => "SINGLE";

        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Pos:Billing:Provider", "NONE")];
    }

    [Fact]
    public async Task Por_defecto_el_adaptador_es_FACTUS_apagado_y_no_se_enciende_sin_verificar()
    {
        await using var factory = new DefaultFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var settings = await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings");
        settings.ActiveAdapter.ShouldBe("FACTUS");
        settings.Mode.ShouldBe("OFF");
        await CredentialsAsync(shop.Owner);
        // Sin verificar no se enciende (no se llama a Factus en esta prueba).
        await RawAsync(shop.Owner, HttpMethod.Put, "/api/v1/billing/settings", new { mode = "EVERY_SALE", environment = "SANDBOX" })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.VERIFICATION_REQUIRED");
    }

    [Fact]
    public async Task Con_NONE_en_la_configuracion_no_se_puede_verificar_ni_encender()
    {
        await using var factory = new NoneFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        (await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings")).ActiveAdapter.ShouldBe("NONE");
        await CredentialsAsync(shop.Owner);
        var check = await PostAsync<FiscalConnectionVerificationDto>(shop.Owner, "/api/v1/billing/settings/verify");
        check.Ok.ShouldBeFalse();
        check.Message.ShouldContain("Pos:Billing:Provider");
        await RawAsync(shop.Owner, HttpMethod.Put, "/api/v1/billing/settings", new { mode = "EVERY_SALE", environment = "SANDBOX" })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.VERIFICATION_REQUIRED");
    }

    [Fact]
    public async Task Los_documentos_de_otro_ambiente_nunca_salen_y_se_pueden_marcar_no_enviados()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop);

        // SANDBOX sin Internet: la venta queda en contingencia.
        factory.Fiscal.Offline = true;
        var sandbox = await SellAsync(shop);
        (await ProcessAsync(shop)).StoppedBecause.ShouldBe("CONTINGENCY");
        (await ForSourceAsync(shop, sandbox.Sale.Id)).Status.ShouldBe("CONTINGENCY");
        factory.Fiscal.Offline = false;

        // Desconectar → PRODUCCIÓN (verificar y encender): lo de SANDBOX no sale, ni con "Enviar ahora".
        await ModeAsync(shop.Owner, "OFF");
        await SendAsync<BillingSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/billing/settings", new { mode = "OFF", environment = "PRODUCTION" });
        (await VerifyAsync(shop.Owner)).Ok.ShouldBeTrue();
        await SendAsync<BillingSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/billing/settings", new { mode = "EVERY_SALE", environment = "PRODUCTION" });
        var held = await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings");
        (held.HeldDocuments, held.HeldOtherEnvironment, held.HeldInReview, held.RangesSyncRequired).ShouldBe((1, 1, 1, true));
        (await PostAsync<int>(shop.Owner, "/api/v1/billing/backlog/send")).ShouldBe(0);
        var submissions = factory.Fiscal.Submissions.Count;
        await DueNowAsync(shop);
        await ProcessAsync(shop);
        factory.Fiscal.Submissions.Count.ShouldBe(submissions);
        (await ForSourceAsync(shop, sandbox.Sale.Id)).Status.ShouldBe("CONTINGENCY");

        // N2: sin rangos de PRODUCCIÓN no sale nada (los de pruebas nunca sirven); la lista y la asignación son del ambiente vigente.
        var production = await SellAsync(shop);
        await DueNowAsync(shop);
        (await ProcessAsync(shop)).StoppedBecause.ShouldBe("NOT_CONFIGURED");
        (await GetAsync<List<FiscalRangeDto>>(shop.Owner, "/api/v1/billing/ranges")).ShouldBeEmpty();
        var sandboxRange = await shop.Catalog.ScalarAsync<Guid>("SELECT id FROM billing.fiscal_numbering_ranges WHERE environment = 'SANDBOX' LIMIT 1");
        await RawAsync(shop.Owner, HttpMethod.Put, $"/api/v1/billing/ranges/{sandboxRange}/assignment", new { branchId = shop.Catalog.Setup.BranchId })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.RANGE_OTHER_ENVIRONMENT");
        var productionRanges = await PostAsync<List<FiscalRangeDto>>(shop.Owner, "/api/v1/billing/ranges/sync");
        productionRanges.Count.ShouldBe(4);
        foreach (var range in productionRanges)
        {
            await AssignAsync(shop, range.Id, shop.Catalog.Setup.BranchId);
        }

        (await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings")).RangesSyncRequired.ShouldBeFalse();

        // Ahora la venta en PRODUCCIÓN sale, con un rango de PRODUCCIÓN.
        await DueNowAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        (await shop.Catalog.ScalarAsync<string>(
                $"SELECT r.environment FROM billing.fiscal_documents f JOIN billing.fiscal_numbering_ranges r ON r.id = f.numbering_range_id WHERE f.source_id = '{production.Sale.Id}'"))
            .ShouldBe("PRODUCTION");
        (await CountAsync(shop, "SELECT count(*) FROM billing.fiscal_documents f JOIN billing.fiscal_numbering_ranges r ON r.id = f.numbering_range_id WHERE f.environment <> r.environment"))
            .ShouldBe(0);
        factory.Fiscal.LastConnection!.Environment.ShouldBe(Pos.Modules.Billing.Domain.FiscalEnvironment.Production);
        (await shop.Catalog.ScalarAsync<string>($"SELECT environment FROM billing.fiscal_documents WHERE source_id = '{production.Sale.Id}'")).ShouldBe("PRODUCTION");

        // N1: el de SANDBOX ya se intentó enviar (contingencia): queda "en revisión", "No enviar" no lo toca.
        (await PostAsync<int>(shop.Owner, "/api/v1/billing/backlog/discard")).ShouldBe(0);
        (await ForSourceAsync(shop, sandbox.Sale.Id)).Status.ShouldBe("CONTINGENCY");

        // Uno de SANDBOX que nunca se intentó sí se marca no enviado; la conciliación lo muestra.
        await shop.Catalog.ExecuteAsync($"UPDATE billing.fiscal_documents SET attempts = 0 WHERE source_id = '{sandbox.Sale.Id}'");
        (await PostAsync<int>(shop.Owner, "/api/v1/billing/backlog/discard")).ShouldBe(1);
        (await ForSourceAsync(shop, sandbox.Sale.Id)).Status.ShouldBe("NOT_SENT");
        var today = DateOnly.FromDateTime(DateTime.Now);
        (await GetAsync<List<FiscalReconciliationDayDto>>(shop.Owner, $"/api/v1/billing/reconciliation?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}"))
            .Sum(d => d.NotSent).ShouldBe(1);
    }

    [Fact]
    public async Task La_nota_credito_de_una_factura_no_enviada_queda_no_enviada_con_alerta()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await LegacyOnAsync(shop);
        var sale = await SellAsync(shop);

        // La factura quedó reclamada por un envío que nunca terminó (SUBMITTING): también cuenta en el atraso (PB7).
        await shop.Catalog.ExecuteAsync($"UPDATE billing.fiscal_documents SET status = 'SUBMITTING' WHERE source_id = '{sale.Sale.Id}'");
        (await VerifyAsync(shop.Owner)).Ok.ShouldBeTrue();
        await ModeAsync(shop.Owner, "EVERY_SALE");
        foreach (var range in await PostAsync<List<FiscalRangeDto>>(shop.Owner, "/api/v1/billing/ranges/sync"))
        {
            await AssignAsync(shop, range.Id, shop.Catalog.Setup.BranchId);
        }

        (await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings")).ShouldSatisfyAllConditions(
            s => s.HeldDocuments.ShouldBe(1), s => s.HeldInReview.ShouldBe(1));

        // Anular la venta emite nota crédito (la factura estaba "en envío"). Reclamada, "No enviar" no la toca (N1); consultada y vuelta a
        // pendiente (nunca llegó al proveedor), sí: la factura queda NO ENVIADA.
        await shop.AuthorizedAsync<SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{sale.Sale.Id}/void", new { reason = "Cliente desistió" });
        (await PostAsync<int>(shop.Owner, "/api/v1/billing/backlog/discard")).ShouldBe(0);
        await shop.Catalog.ExecuteAsync($"UPDATE billing.fiscal_documents SET status = 'PENDING', attempts = 0 WHERE source_id = '{sale.Sale.Id}' AND source = 'SALE'");
        (await PostAsync<int>(shop.Owner, "/api/v1/billing/backlog/discard")).ShouldBe(1);

        // La nota no se aplaza para siempre: queda NO ENVIADA, con alerta en la auditoría, y nada sale al proveedor.
        await DueNowAsync(shop);
        await ProcessAsync(shop);
        (await ForSourceAsync(shop, sale.Sale.Id, "SALE_VOID")).Status.ShouldBe("NOT_SENT");
        factory.Fiscal.CreditNotes.ShouldBeEmpty();
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_DOCUMENT_NOT_SENT'")).ShouldBe(1);
    }

    [Fact]
    public async Task Rotar_llaves_en_el_mismo_ambiente_no_retiene_lo_que_esta_en_cola()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop);

        factory.Fiscal.Offline = true;
        var sale = await SellAsync(shop);
        (await ProcessAsync(shop)).StoppedBecause.ShouldBe("CONTINGENCY");
        factory.Fiscal.Offline = false;

        (await PostAsync<FiscalConnectionVerificationDto>(shop.Owner, "/api/v1/billing/settings/credentials/rotate", new
        {
            username = "otra@correo.co", password = "Otra-Clave-1", clientId = "cliente-10", clientSecret = "secreto-nuevo",
        })).Ok.ShouldBeTrue();
        (await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings")).ShouldSatisfyAllConditions(
            s => s.HeldDocuments.ShouldBe(0), s => s.Mode.ShouldBe("EVERY_SALE"), s => s.CredentialHint.ShouldBe("ot***@correo.co"));
        await DueNowAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        (await ForSourceAsync(shop, sale.Sale.Id)).Status.ShouldBe("ACCEPTED");
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
