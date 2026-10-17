using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.FactusFake;
using Pos.Server.IntegrationTests.Phase11B;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase11B.ElectronicBilling;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.PantallasAdmin;

/// <summary>
/// Pantalla "Conectar Factus" (docs/fases/pantallas-faltantes-propuesta.md §2) contra el Factus SIMULADO en Kestrel: guardar desactivado →
/// verificar → activar solo si verificó; volver a probar sin Internet; cambiar llaves (apaga); desconectar; auditoría y permisos. Las
/// credenciales nunca vuelven completas al cliente.
/// </summary>
public class FactusConexionTests
{
    private const string WrongPassword = "Clave-Equivocada-987";

    private static object Credentials(string password) => new
    {
        username = FactusFakeServer.DefaultUsername, password,
        clientId = FactusFakeServer.DefaultClientId, clientSecret = FactusFakeServer.DefaultClientSecret,
    };

    private static Task<BillingSettingsDto> SettingsAsync(HttpClient client, string mode, string environment = "SANDBOX") =>
        SendAsync<BillingSettingsDto>(client, HttpMethod.Put, "/api/v1/billing/settings", new { mode, environment });

    private static Task<FiscalConnectionVerificationDto> VerifyAsync(HttpClient client) =>
        PostAsync<FiscalConnectionVerificationDto>(client, "/api/v1/billing/settings/verify");

    [Fact]
    public async Task Conectar_guarda_desactivado_verifica_contra_Factus_y_activa_solo_si_verifico()
    {
        await using var factus = new FactusFakeServer();
        var url = await factus.StartAsync(Ct);
        await using var factory = new FactusBillingServerFactory(url);
        var shop = await SalesScenario.CreateAsync(factory);
        var owner = shop.Owner;

        // Estado inicial: Factus es el adaptador por defecto, apagado y sin llaves; verificar sin llaves no se puede.
        var initial = await GetAsync<BillingSettingsDto>(owner, "/api/v1/billing/settings");
        (initial.ActiveAdapter, initial.Mode, initial.HasCredentials, initial.LastVerifiedAt).ShouldBe(("FACTUS", "OFF", false, (DateTimeOffset?)null));
        await RawAsync(owner, HttpMethod.Post, "/api/v1/billing/settings/verify").ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.CREDENTIALS_REQUIRED");

        // 1. Guardar llaves (equivocadas): quedan cifradas, la facturación apagada y solo vuelve el usuario enmascarado.
        var response = await owner.PutAsJsonAsync("/api/v1/billing/settings/credentials", Credentials(WrongPassword), Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(WrongPassword);
        body.ShouldNotContain(FactusFakeServer.DefaultClientSecret);
        body.ShouldNotContain(FactusFakeServer.DefaultUsername);
        var saved = (await response.Content.ReadFromJsonAsync<BillingSettingsDto>(Json, Ct))!;
        (saved.Mode, saved.HasCredentials, saved.CredentialHint).ShouldBe(("OFF", true, (string?)"in***@factus.test"));

        // 2. Verificar: Factus rechaza; 3. activar sin verificación correcta no se permite.
        var failed = await VerifyAsync(owner);
        (failed.Ok, failed.Environment).ShouldBe((false, "SANDBOX"));
        failed.Message.ShouldContain("rechazó las credenciales");
        await RawAsync(owner, HttpMethod.Put, "/api/v1/billing/settings", new { mode = "ON_REQUEST", environment = "SANDBOX" })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.VERIFICATION_REQUIRED");

        // 4. Llaves correctas → verifica (solo pide un token, no emite) → activa → trae los rangos.
        await SendAsync<BillingSettingsDto>(owner, HttpMethod.Put, "/api/v1/billing/settings/credentials", Credentials(FactusFakeServer.DefaultPassword));
        (await VerifyAsync(owner)).Ok.ShouldBeTrue();
        factus.Documents.ShouldBeEmpty();
        var connected = await SettingsAsync(owner, "ON_REQUEST");
        (connected.Mode, connected.LastVerificationOk).ShouldBe(("ON_REQUEST", (bool?)true));
        connected.LastVerifiedAt.ShouldNotBeNull();
        (await PostAsync<List<FiscalRangeDto>>(owner, "/api/v1/billing/ranges/sync")).Count.ShouldBe(4);

        // Cambiar de modo en el mismo ambiente no pide verificar; cambiar de ambiente encendida sí.
        (await SettingsAsync(owner, "EVERY_SALE")).Mode.ShouldBe("EVERY_SALE");
        await RawAsync(owner, HttpMethod.Put, "/api/v1/billing/settings", new { mode = "EVERY_SALE", environment = "PRODUCTION" })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.VERIFICATION_REQUIRED");

        // 5. Volver a probar sin Internet: informa el problema, no apaga la facturación y NO quita la verificación correcta (PA9).
        var factusUrl = factory.Connection.BaseUrl;
        factory.Connection.BaseUrl = new Uri("http://127.0.0.1:1/");
        var offline = await VerifyAsync(owner);
        (offline.Ok, offline.Transient).ShouldBe((false, true));
        offline.Message.ShouldContain("No hubo conexión con Factus");
        factory.Connection.BaseUrl = factusUrl;
        (await GetAsync<BillingSettingsDto>(owner, "/api/v1/billing/settings")).ShouldSatisfyAllConditions(
            s => s.Mode.ShouldBe("EVERY_SALE"), s => s.LastVerificationOk.ShouldBe(true));
        (await VerifyAsync(owner)).Ok.ShouldBeTrue();

        // Cambiar llaves (PA6): las candidatas se prueban SIN guardarlas; malas → no cambia nada; vacías → error de validación;
        // buenas → se guardan y el modo sigue igual.
        var before = await GetAsync<BillingSettingsDto>(owner, "/api/v1/billing/settings");
        var candidate = await PostAsync<FiscalConnectionVerificationDto>(owner, "/api/v1/billing/settings/verify-candidate", Credentials(WrongPassword));
        candidate.Ok.ShouldBeFalse();
        var rejected = await PostAsync<FiscalConnectionVerificationDto>(owner, "/api/v1/billing/settings/credentials/rotate", Credentials(WrongPassword));
        rejected.Ok.ShouldBeFalse();
        (await GetAsync<BillingSettingsDto>(owner, "/api/v1/billing/settings")).ShouldSatisfyAllConditions(
            s => s.Mode.ShouldBe("EVERY_SALE"), s => s.CredentialsUpdatedAt.ShouldBe(before.CredentialsUpdatedAt), s => s.LastVerificationOk.ShouldBe(true));
        await RawAsync(owner, HttpMethod.Post, "/api/v1/billing/settings/credentials/rotate", new { username = "", password = "", clientId = "", clientSecret = "" })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "BILLING.INVALID_CREDENTIALS");
        var rotated = await owner.PostAsJsonAsync("/api/v1/billing/settings/credentials/rotate", Credentials(FactusFakeServer.DefaultPassword), Json, Ct);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await rotated.Content.ReadAsStringAsync(Ct)).ShouldNotContain(FactusFakeServer.DefaultPassword);
        (await GetAsync<BillingSettingsDto>(owner, "/api/v1/billing/settings")).ShouldSatisfyAllConditions(
            s => s.Mode.ShouldBe("EVERY_SALE"), s => s.LastVerificationOk.ShouldBe(true), s => s.CredentialsUpdatedAt.ShouldNotBe(before.CredentialsUpdatedAt));
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_CREDENTIALS_CHANGED' AND summary LIKE '%no cambió%'")).ShouldBe(1);

        // 6. Cambiar llaves: se apaga hasta verificarlas de nuevo (queda en la auditoría).
        (await SendAsync<BillingSettingsDto>(owner, HttpMethod.Put, "/api/v1/billing/settings/credentials", Credentials(FactusFakeServer.DefaultPassword)))
            .ShouldSatisfyAllConditions(s => s.Mode.ShouldBe("OFF"), s => s.LastVerifiedAt.ShouldBeNull());
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_CREDENTIALS_CHANGED' AND summary LIKE '%se apagó%'")).ShouldBe(1);
        (await VerifyAsync(owner)).Ok.ShouldBeTrue();
        (await SettingsAsync(owner, "EVERY_SALE")).Mode.ShouldBe("EVERY_SALE");

        // 7. Desconectar: modo OFF, las llaves quedan guardadas; reconectar solo pide verificar.
        (await SettingsAsync(owner, "OFF")).ShouldSatisfyAllConditions(s => s.Mode.ShouldBe("OFF"), s => s.HasCredentials.ShouldBeTrue());
        (await SettingsAsync(owner, "ON_REQUEST")).Mode.ShouldBe("ON_REQUEST");

        // Auditoría: cada verificación (también la de llaves candidatas y el cambio rechazado) queda registrada y nunca con la clave.
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_CONNECTION_VERIFIED'")).ShouldBe(7);
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_CONNECTION_VERIFIED' AND severity = 'WARNING'")).ShouldBe(4);
        (await CountAsync(shop, $"SELECT count(*) FROM audit.audit_log WHERE summary LIKE '%{WrongPassword}%' OR new_values::text LIKE '%{WrongPassword}%'"))
            .ShouldBe(0);
        var stored = await shop.Catalog.ScalarAsync<string>("SELECT encode(credentials, 'escape') FROM billing.provider_settings");
        stored!.ShouldNotContain(FactusFakeServer.DefaultPassword);
    }

    [Fact]
    public async Task Verificar_se_limita_a_una_vez_cada_10_segundos()
    {
        await using var factus = new FactusFakeServer();
        var url = await factus.StartAsync(Ct);
        await using var factory = new FactusBillingServerFactory(url, verifyMinSeconds: 10);
        var shop = await SalesScenario.CreateAsync(factory);
        await SendAsync<BillingSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/billing/settings/credentials", Credentials(FactusFakeServer.DefaultPassword));

        (await VerifyAsync(shop.Owner)).Ok.ShouldBeTrue();
        await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/billing/settings/verify").ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.VERIFY_TOO_SOON");

        // El freno es por juego de llaves (PA6): las mismas candidatas esperan; OTRAS candidatas no.
        (await PostAsync<FiscalConnectionVerificationDto>(shop.Owner, "/api/v1/billing/settings/verify-candidate", Credentials(FactusFakeServer.DefaultPassword)))
            .Ok.ShouldBeTrue();
        await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/billing/settings/verify-candidate", Credentials(FactusFakeServer.DefaultPassword))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.VERIFY_TOO_SOON");
        (await PostAsync<FiscalConnectionVerificationDto>(shop.Owner, "/api/v1/billing/settings/verify-candidate", Credentials(WrongPassword)))
            .Ok.ShouldBeFalse();

        // N4: además, como máximo 5 verificaciones por minuto por empresa, aunque sean llaves distintas.
        (await PostAsync<FiscalConnectionVerificationDto>(shop.Owner, "/api/v1/billing/settings/verify-candidate", Credentials("Otra-1"))).Ok.ShouldBeFalse();
        (await PostAsync<FiscalConnectionVerificationDto>(shop.Owner, "/api/v1/billing/settings/verify-candidate", Credentials("Otra-2"))).Ok.ShouldBeFalse();
        await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/billing/settings/verify-candidate", Credentials("Otra-3"))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.VERIFY_TOO_SOON");
    }

    [Fact]
    public async Task Solo_quien_configura_la_facturacion_puede_verificar_la_conexion()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await CredentialsAsync(shop.Owner);

        foreach (var client in new[] { shop.Supervisor, shop.Cashier })
        {
            (await StatusAsync(client, HttpMethod.Post, "/api/v1/billing/settings/verify")).ShouldBe(HttpStatusCode.Forbidden);
            (await StatusAsync(client, HttpMethod.Post, "/api/v1/billing/settings/verify-candidate", Credentials("x"))).ShouldBe(HttpStatusCode.Forbidden);
            (await StatusAsync(client, HttpMethod.Post, "/api/v1/billing/settings/credentials/rotate", Credentials("x"))).ShouldBe(HttpStatusCode.Forbidden);
            (await StatusAsync(client, HttpMethod.Post, "/api/v1/billing/backlog/send")).ShouldBe(HttpStatusCode.Forbidden);
            (await StatusAsync(client, HttpMethod.Post, "/api/v1/billing/backlog/discard")).ShouldBe(HttpStatusCode.Forbidden);
        }

        // Con el proveedor simulado: credenciales rechazadas no encienden; aceptadas sí.
        factory.Fiscal.RejectCredentials = true;
        (await VerifyAsync(shop.Owner)).Ok.ShouldBeFalse();
        await RawAsync(shop.Owner, HttpMethod.Put, "/api/v1/billing/settings", new { mode = "EVERY_SALE", environment = "SANDBOX" })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.VERIFICATION_REQUIRED");
        factory.Fiscal.RejectCredentials = false;
        (await VerifyAsync(shop.Owner)).Ok.ShouldBeTrue();
        (await SettingsAsync(shop.Owner, "EVERY_SALE")).Mode.ShouldBe("EVERY_SALE");
        factory.Fiscal.Submissions.ShouldBeEmpty();
    }
}
