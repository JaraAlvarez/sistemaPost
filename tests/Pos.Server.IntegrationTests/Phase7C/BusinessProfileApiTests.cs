using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase2.OrganizationApiTests;

namespace Pos.Server.IntegrationTests.Phase7C;

/// <summary>
/// Fase 7-C parte A: tipo de negocio y funciones activables. El asistente aplica los valores del tipo elegido, /auth/me los entrega a la
/// interfaz, el dueño los cambia (auditado) y con "Báscula y PLU" apagada la caja no lee la báscula (la configuración se conserva).
/// </summary>
public class BusinessProfileApiTests
{
    private static readonly string[] SomeFeatures = ["SCALE", "PROMOTIONS", "open_price"];
    private static readonly string[] UnknownFeature = ["DRONES"];
    private static readonly string[] WithoutScale = ["PROMOTIONS", "PURCHASING"];
    private static readonly string[] OnlyScale = ["SCALE"];

    [Theory]
    [InlineData("SUPERMARKET", new[] { "LOTS_EXPIRY", "PROMOTIONS", "PURCHASING", "SCALE" }, "Abarrotes")]
    [InlineData("FRESH_MARKET", new[] { "PROMOTIONS", "PURCHASING", "SCALE" }, "Frutas")]
    [InlineData("PHARMACY", new[] { "LOTS_EXPIRY", "PROMOTIONS", "PURCHASING" }, "Medicamentos")]
    [InlineData("APPAREL", new[] { "PROMOTIONS", "PURCHASING", "VARIANTS" }, "Calzado")]
    [InlineData("HARDWARE", new[] { "FRACTIONAL_SALE", "OPEN_PRICE", "PROMOTIONS", "PURCHASING" }, "Herramientas")]
    [InlineData("OTHER", new[] { "PROMOTIONS", "PURCHASING" }, null)]
    public async Task El_asistente_aplica_el_tipo_de_negocio_elegido(string profile, string[] features, string? starterCategory)
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/setup", SetupWith(profile), Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        await LoginAsync(client, OwnerUsername, OwnerPassword);

        var me = await GetAsync<MeDto>(client, "/api/v1/auth/me");
        me.Business.ShouldNotBeNull();
        me.Business.Profile.ShouldBe(profile);
        me.Business.EnabledFeatures.ShouldBe(features);
        (await GetAsync<BusinessFeaturesDto>(client, "/api/v1/organization/business-features")).EnabledFeatures.ShouldBe(features);
        (await ScalarAsync<string>(factory, "SELECT summary FROM audit.audit_log WHERE action = 'SETUP_COMPLETED'"))!.ShouldContain($"tipo de negocio {profile}");

        // Categorías de ejemplo del tipo (además de "General"); "Otro" deja solo "General".
        var categories = (await GetAsync<List<CategoryDto>>(client, "/api/v1/catalog/categories")).Select(c => c.Name).ToList();
        categories.ShouldContain("General");
        if (starterCategory is null)
        {
            categories.ShouldHaveSingleItem();
        }
        else
        {
            categories.ShouldContain(starterCategory);
        }
    }

    [Fact]
    public async Task Sin_tipo_de_negocio_queda_como_antes_y_un_tipo_desconocido_se_rechaza()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/setup", SetupWith("PANADERIA"), Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "VALIDATION.FAILED");

        // Instaladores anteriores (sin el campo): supermercado, sin categorías de ejemplo.
        await SetupAsync(client);
        var me = await GetAsync<MeDto>(client, "/api/v1/auth/me");
        me.Business!.Profile.ShouldBe("SUPERMARKET");
        me.Business.EnabledFeatures.ShouldBe(["LOTS_EXPIRY", "PROMOTIONS", "PURCHASING", "SCALE"]);
        (await GetAsync<List<CategoryDto>>(client, "/api/v1/catalog/categories")).ShouldHaveSingleItem().Name.ShouldBe("General");
    }

    [Fact]
    public async Task El_dueno_enciende_y_apaga_funciones_y_cambia_el_tipo_de_negocio()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);

        // Apagar compras y lotes, encender precio abierto: queda guardado, en /me y en la auditoría.
        var saved = await SendAsync(shop.Owner, HttpMethod.Put, "/api/v1/organization/business-features",
            new { enabledFeatures = SomeFeatures });
        saved.EnabledFeatures.ShouldBe(["OPEN_PRICE", "PROMOTIONS", "SCALE"]);
        (await GetAsync<MeDto>(shop.Owner, "/api/v1/auth/me")).Business!.EnabledFeatures.ShouldBe(["OPEN_PRICE", "PROMOTIONS", "SCALE"]);
        (await GetAsync<MeDto>(shop.Cashier, "/api/v1/auth/me")).Business!.Has(BusinessCatalog.Purchasing).ShouldBeFalse();
        var summary = (await shop.Catalog.ScalarAsync<string>("SELECT summary FROM audit.audit_log WHERE action = 'BUSINESS_FEATURES_CHANGED'"))!;
        summary.ShouldContain("Encendidas: Precio abierto.");
        summary.ShouldContain("Apagadas (se ocultan, los datos se conservan)");
        summary.ShouldContain("Compras y proveedores");

        // Apagar no borra datos: las compras siguen ahí y se pueden consultar.
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM purchasing.purchases")).ShouldBeGreaterThan(0);
        (await shop.Owner.GetAsync("/api/v1/purchasing/purchases", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Códigos desconocidos y quien no tiene el permiso de la empresa: rechazados.
        await SalesScenario.RawAsync(shop.Owner, HttpMethod.Put, "/api/v1/organization/business-features", new { enabledFeatures = UnknownFeature })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "ORGANIZATION.INVALID_BUSINESS_FEATURE");
        await SalesScenario.RawAsync(shop.Cashier, HttpMethod.Put, "/api/v1/organization/business-features", new { enabledFeatures = Array.Empty<string>() })
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        await SalesScenario.RawAsync(shop.Owner, HttpMethod.Put, "/api/v1/organization/business-profile", new { profile = "PANADERIA" })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "ORGANIZATION.INVALID_BUSINESS_PROFILE");

        // Cambiar el tipo deja los interruptores en los valores de ese tipo.
        var pharmacy = await SendAsync(shop.Owner, HttpMethod.Put, "/api/v1/organization/business-profile", new { profile = "PHARMACY" });
        pharmacy.Profile.ShouldBe("PHARMACY");
        pharmacy.EnabledFeatures.ShouldBe(["LOTS_EXPIRY", "PROMOTIONS", "PURCHASING"]);
        (await shop.Catalog.ScalarAsync<string>("SELECT business_profile FROM org.companies")).ShouldBe("PHARMACY");
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'BUSINESS_FEATURES_CHANGED'")).ShouldBe(2);
    }

    [Fact]
    public async Task Con_la_bascula_apagada_la_caja_no_la_lee_pero_la_configuracion_se_conserva()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var terminal = (await GetAsync<List<TerminalDto>>(shop.Owner, "/api/v1/organization/terminals")).Single();
        var url = $"/api/v1/organization/terminals/{terminal.Id}/scale";
        await SalesScenario.SendAsync<TerminalScaleDto>(shop.Owner, HttpMethod.Put, url, new
        {
            enabled = true, driver = "TOLEDO_8217", port = "COM3", stableWaitSeconds = 3, emptyWaitSeconds = 5, allowManualWeight = true,
        });
        (await GetAsync<TerminalScaleDto>(shop.Cashier, url)).FeatureEnabled.ShouldBeTrue();

        await SendAsync(shop.Owner, HttpMethod.Put, "/api/v1/organization/business-features", new { enabledFeatures = WithoutScale });
        var off = await GetAsync<TerminalScaleDto>(shop.Cashier, url);
        (off.Configured, off.Enabled, off.FeatureEnabled, off.Port).ShouldBe((true, true, false, (string?)"COM3"));

        // Al encenderla de nuevo la caja vuelve a leerla, con la misma configuración.
        await SendAsync(shop.Owner, HttpMethod.Put, "/api/v1/organization/business-features", new { enabledFeatures = OnlyScale });
        (await GetAsync<TerminalScaleDto>(shop.Cashier, url)).FeatureEnabled.ShouldBeTrue();
    }

    private static JsonObject SetupWith(string profile)
    {
        var body = JsonSerializer.SerializeToNode(SetupBody(), Json)!.AsObject();
        body["businessProfile"] = profile;
        return body;
    }

    private static Task<BusinessFeaturesDto> SendAsync(HttpClient client, HttpMethod method, string url, object body) =>
        SalesScenario.SendAsync<BusinessFeaturesDto>(client, method, url, body);
}
