using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Customers.Application;
using Pos.Modules.Customers.Contracts;
using Pos.Modules.Customers.Domain;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase8;

/// <summary>Criterios de aceptación de la Fase 8 (propuesta §14): cliente identificado, listas de precio, historial y privacidad.</summary>
public class CustomersApiTests
{
    private static readonly string[] SmsOnly = ["sms"];

    private static async Task<PriceListDto> PriceListAsync(SalesScenario shop, string code, decimal? percent, bool promotions) =>
        await PostAsync<PriceListDto>(shop.Owner, "/api/v1/catalog/price-lists", new
        {
            code, name = $"Lista {code}", pricesIncludeTax = true, adjustmentPercent = percent, roundingIncrement = 50m, allowsPromotions = promotions,
        }, HttpStatusCode.Created);

    private static async Task<CustomerGroupDto> GroupAsync(SalesScenario shop, string code, Guid priceListId) =>
        await PostAsync<CustomerGroupDto>(shop.Owner, "/api/v1/customers/groups", new { code, name = $"Grupo {code}", priceListId }, HttpStatusCode.Created);

    private static Task<QuickCreateResultDto> QuickAsync(HttpClient client, string type, string number, string first, string last, string? phone = null,
        bool service = true, string personType = "NATURAL", string? checkDigit = null, string? legalName = null) =>
        PostAsync<QuickCreateResultDto>(client, "/api/v1/customers/quick", new
        {
            personType, identificationType = type, identificationNumber = number, checkDigit, firstNames = first, lastNames = last, legalName, phone,
            consents = service ? new object[] { new { purpose = "Service", granted = true } } : [],
        });

    private static async Task<PromotionDto> SpecialPriceAsync(SalesScenario shop, Guid productId, decimal price)
    {
        var promotion = await PostAsync<PromotionDto>(shop.Owner, "/api/v1/promotions", new
        {
            name = $"Especial {price}", type = "SpecialPrice", validFrom = DateTimeOffset.UtcNow.AddHours(-1), price, items = new[] { new { productId } },
        }, HttpStatusCode.Created);
        return await PostAsync<PromotionDto>(shop.Owner, $"/api/v1/promotions/{promotion.Id}/activate");
    }

    [Fact]
    public async Task Cliente_identificado_con_lista_de_empleados_promocion_factura_cambio_e_historial()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var employees = await PriceListAsync(shop, "EMPLEADOS", -5m, promotions: true);
        employees.AdjustmentPercent.ShouldBe(-5m);
        var group = await GroupAsync(shop, "EMPLEADOS", employees.Id);
        await SpecialPriceAsync(shop, shop.Rice, 2_900m);

        // La cajera busca y, como no existe, lo crea con su autorización de datos (verbal en la caja, D8-06).
        (await GetAsync<List<CustomerLookupDto>>(shop.Cashier, "/api/v1/customers/lookup?q=1020304050")).ShouldBeEmpty();
        var created = await QuickAsync(shop.Cashier, "CC", "1020304050", "Juan", "Pérez Gómez", phone: "300 123 4567");
        created.Created.ShouldBeTrue();
        var juan = created.Customer;
        juan.ServiceConsent.ShouldBeTrue();
        juan.ConsentPolicyVersion.ShouldBe(1);
        juan.TaxRegime.ShouldBe("49");
        juan.FiscalResponsibilities.ShouldBe(["R-99-PN"]);
        juan.MissingInvoiceEmail.ShouldBeTrue();

        // Búsqueda en caja por celular, nombre sin tildes y prefijo de la cédula.
        (await GetAsync<List<CustomerLookupDto>>(shop.Cashier, "/api/v1/customers/lookup?q=3001234567")).Single().MatchedBy.ShouldBe("PHONE");
        (await GetAsync<List<CustomerLookupDto>>(shop.Cashier, "/api/v1/customers/lookup?q=juan%20perez")).Single().PartyId.ShouldBe(juan.PartyId);
        (await GetAsync<List<CustomerLookupDto>>(shop.Cashier, "/api/v1/customers/lookup?q=10203.040")).Single().MatchedBy.ShouldBe("IDENTIFICATION");

        // La cajera no asigna listas, no corrige datos ni edita terceros (D8-04, RN-PRL-04); el propietario sí.
        await RawAsync(shop.Cashier, HttpMethod.Put, $"/api/v1/customers/{juan.PartyId}/pricing", new { groupId = group.Id })
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        await RawAsync(shop.Cashier, HttpMethod.Put, $"/api/v1/parties/{juan.PartyId}", new { })
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        (await SendAsync<CustomerDto>(shop.Owner, HttpMethod.Put, $"/api/v1/customers/{juan.PartyId}/pricing", new { groupId = group.Id })).GroupCode
            .ShouldBe("EMPLEADOS");

        // Venta a Consumidor final: arroz con la promoción ($2.900) y 2 gaseosas ($2.500).
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        await shop.ScanAsync(sale.Id, "ARROZ-500");
        sale = await shop.ScanAsync(sale.Id, "7702004003508", 2);
        sale.Total.ShouldBe(7_900m);

        // Al identificar al empleado se re-precia: arroz 3.000 × 0,95 = 2.850 (la promoción de 2.900 no mejora) y gaseosa
        // 2.500 × 0,95 = 2.375 → 2.400 (redondeo a $50, D8-11).
        sale = await SendAsync<SaleDto>(shop.Cashier, HttpMethod.Put, $"/api/v1/sales/{sale.Id}/customer", new { partyId = juan.PartyId, invoiceRequested = true });
        sale.PriceListCode.ShouldBe("EMPLEADOS");
        sale.CustomerGroupCode.ShouldBe("EMPLEADOS");
        sale.Warnings.Count.ShouldBe(2);
        sale.Lines.Single(l => l.ProductId == shop.Rice).UnitPrice.ShouldBe(2_850m);
        sale.Lines.Single(l => l.ProductId == shop.Rice).PromotionDiscount.ShouldBe(0m);
        sale.Lines.Single(l => l.ProductId == shop.Soda).UnitPrice.ShouldBe(2_400m);
        sale.Lines.ShouldAllBe(l => l.PriceSource == "DERIVED");
        sale.Total.ShouldBe(7_650m);

        // Pide factura electrónica y le falta el correo: no se cobra hasta completarlo (RN-SAL-13); la cajera completa el vacío.
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/complete", new { payments = new[] { Pay(shop.Cash, 10_000m) } })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.CUSTOMER_FISCAL_DATA_INCOMPLETE");
        (await PostAsync<CustomerDto>(shop.Cashier, $"/api/v1/customers/{juan.PartyId}/complete", new { email = "juan@example.com", phone = "999" }))
            .Phone.ShouldBe("300 123 4567");
        var receipt = await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 10_000m)]);
        receipt.Sale.InvoiceRequested.ShouldBeTrue();
        receipt.TicketText.ShouldContain("EMPLEADOS");
        receipt.TicketText.ShouldContain("Autorizó el tratamiento");
        (await shop.Catalog.ScalarAsync<string>($"SELECT customer_fiscal->>'taxRegime' FROM sales.sales WHERE id = '{sale.Id}'")).ShouldBe("49");
        (await shop.Catalog.ScalarAsync<string>($"SELECT customer_email FROM sales.sales WHERE id = '{sale.Id}'")).ShouldBe("juan@example.com");

        // Cambio de una gaseosa: la venta nueva conserva la lista del cliente y el crédito ($2.400) paga otra gaseosa.
        var body = new
        {
            originalSaleId = sale.Id, reason = "Gaseosa sin gas",
            lines = new[] { new { saleLineId = receipt.Sale.Lines.Single(l => l.ProductId == shop.Soda).Id, quantity = 1m, destination = "SendToDamaged" } },
        };
        var grant = await shop.GrantAsync(await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body));
        var started = await SendAsync<ExchangeStartedDto>(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body, HttpStatusCode.Created, grant: grant);
        started.Sale.PriceListCode.ShouldBe("EMPLEADOS");
        var replacement = await shop.ScanAsync(started.Sale.Id, "7702004003508");
        replacement.Total.ShouldBe(2_400m);
        await shop.CompleteAsync(replacement.Id, []);

        // Historial y resumen (D8-13): 2 ventas y 1 cambio; total comprado = 7.650 + 2.400 − 2.400.
        var history = await GetAsync<List<CustomerHistoryEntryDto>>(shop.Supervisor, $"/api/v1/customers/{juan.PartyId}/history");
        history.Count.ShouldBe(3);
        history.Count(h => h.Kind == "EXCHANGE").ShouldBe(1);
        var summary = await GetAsync<CustomerSalesSummaryDto>(shop.Supervisor, $"/api/v1/customers/{juan.PartyId}/summary");
        summary.Purchases.ShouldBe(2);
        summary.TotalPurchased.ShouldBe(7_650m);
        summary.Exchanges.ShouldBe(1);
        summary.TopProducts[0].Sku.ShouldBe("GAS-400");
        (await GetAsync<List<SaleSummaryDto>>(shop.Supervisor, $"/api/v1/sales?customerId={juan.PartyId}")).Count.ShouldBe(2);
        await shop.Cashier.GetAsync($"/api/v1/customers/{juan.PartyId}/history", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");

        // Cliente bloqueado: no se asigna a ventas nuevas (RN-CUS-03).
        await PostAsync<CustomerDto>(shop.Supervisor, $"/api/v1/customers/{juan.PartyId}/status", new { status = "Blocked", reason = "Cheque devuelto" });
        var next = await shop.StartAsync();
        await RawAsync(shop.Cashier, HttpMethod.Put, $"/api/v1/sales/{next.Id}/customer", new { partyId = juan.PartyId })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CUSTOMERS.BLOCKED");
    }

    [Fact]
    public async Task Lista_mayorista_sin_promociones_proveedor_como_cliente_duplicados_y_altas_simultaneas()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var wholesale = await PriceListAsync(shop, "MAYORISTA", null, promotions: false);
        await PostAsync<SetPriceResultDto>(shop.Owner, $"/api/v1/catalog/products/{shop.Rice}/prices", new { price = 2_800m, priceListId = wholesale.Id });
        var group = await GroupAsync(shop, "MAYOR", wholesale.Id);
        await SpecialPriceAsync(shop, shop.Soda, 2_000m);

        // El proveedor (NIT 800197268-4) también compra: el alta rápida devuelve el tercero existente sin modificarlo y le da el rol.
        var supplier = await QuickAsync(shop.Cashier, "NIT", "800197268", "x", "x", personType: "LEGAL", checkDigit: "4", legalName: "Otro nombre");
        supplier.Created.ShouldBeFalse();
        supplier.Customer.DisplayName.ShouldBe("Distribuidora Láctea SAS");
        supplier.Customer.Origin.ShouldBe("AUTO_ON_SALE");
        await SendAsync<CustomerDto>(shop.Owner, HttpMethod.Put, $"/api/v1/customers/{supplier.Customer.PartyId}/pricing", new { groupId = group.Id });

        // Mayorista: arroz con precio propio de la lista ($2.800) y gaseosa de la general sin la promoción de $2.000 (la lista no las admite).
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        await SendAsync<SaleDto>(shop.Cashier, HttpMethod.Put, $"/api/v1/sales/{sale.Id}/customer", new { partyId = supplier.Customer.PartyId });
        await shop.ScanAsync(sale.Id, "ARROZ-500");
        sale = await shop.ScanAsync(sale.Id, "7702004003508");
        sale.Lines.Single(l => l.ProductId == shop.Rice).UnitPrice.ShouldBe(2_800m);
        sale.Lines.Single(l => l.ProductId == shop.Rice).PriceSource.ShouldBe("LIST");
        sale.Lines.Single(l => l.ProductId == shop.Soda).PromotionDiscount.ShouldBe(0m);
        sale.Total.ShouldBe(5_300m);

        // Consumidor final en la misma venta: vuelve a la lista general con la promoción.
        sale = await SendAsync<SaleDto>(shop.Cashier, HttpMethod.Put, $"/api/v1/sales/{sale.Id}/customer", new { partyId = (Guid?)null });
        sale.PriceListCode.ShouldBeNull();
        sale.Total.ShouldBe(3_000m + 2_000m);

        // Mismo número con otro tipo (CC vs NIT de la misma persona): advertencia de posible duplicado (RN-CUS-04).
        var duplicate = await QuickAsync(shop.Cashier, "CC", "800197268", "Pedro", "Díaz");
        duplicate.Created.ShouldBeTrue();
        duplicate.PossibleDuplicates.Single().IdentificationType.ShouldBe("NIT");

        // Dos cajas crean la misma cédula a la vez: una sola ficha; la otra recibe la existente.
        var second = factory.CreateClient();
        await Phase3.SecurityScenario.PosLoginAsync(second, SupervisorCode, SupervisorPin);
        var results = await Task.WhenAll(QuickAsync(shop.Cashier, "CC", "55443322", "Ana", "Ruiz"), QuickAsync(second, "CC", "55443322", "Ana", "Ruiz"));
        results.Select(r => r.Customer.PartyId).Distinct().Count().ShouldBe(1);
        results.Count(r => r.Created).ShouldBe(1);

        // Crédito y puntos están reservados: no se crean medios de esos tipos (D8-15, D8-16).
        await shop.Owner.PostAsJsonAsync("/api/v1/cash/payment-methods", new { code = "FIADO", name = "Fiado", kind = "CustomerCredit", sortOrder = 90 }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.PAYMENT_KIND_NOT_AVAILABLE");
    }

    [Fact]
    public async Task Autorizaciones_de_solo_insercion_solicitudes_con_plazo_exportacion_y_supresion()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);

        // La política sembrada es una plantilla pendiente de revisión hasta que el propietario la active.
        var status = await GetAsync<PrivacyStatusDto>(shop.Owner, "/api/v1/customers/privacy-status");
        status.PolicyPendingReview.ShouldBeTrue();
        var policy = (await GetAsync<List<PrivacyPolicyDto>>(shop.Owner, "/api/v1/customers/privacy-policies")).Single();
        policy.Status.ShouldBe("PENDING_REVIEW");
        await PostAsync<PrivacyPolicyDto>(shop.Owner, $"/api/v1/customers/privacy-policies/{policy.Id}/activate");
        (await GetAsync<PrivacyStatusDto>(shop.Owner, "/api/v1/customers/privacy-status")).ActivePolicyVersion.ShouldBe(1);

        // Sin autorización SERVICE el cliente se identifica para la factura pero no se muestra su historial (D8-07).
        var laura = (await QuickAsync(shop.Cashier, "CC", "43123456", "Laura", "Mejía", service: false)).Customer;
        laura.ServiceConsent.ShouldBeFalse();
        await shop.Supervisor.GetAsync($"/api/v1/customers/{laura.PartyId}/summary", Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CUSTOMERS.CONSENT_REQUIRED");

        // Autoriza atención y marketing por SMS; luego revoca el marketing: cada cambio es un registro nuevo (solo inserción).
        await PostAsync<CustomerDto>(shop.Cashier, $"/api/v1/customers/{laura.PartyId}/consents", new
        {
            consents = new object[]
            {
                new { purpose = "Service", granted = true, channel = "PosSigned", evidence = "Formato 0042" },
                new { purpose = "Marketing", granted = true, marketingChannels = SmsOnly },
            },
        });
        var revoked = await PostAsync<CustomerDto>(shop.Cashier, $"/api/v1/customers/{laura.PartyId}/consents", new
        {
            consents = new[] { new { purpose = "Marketing", granted = false } },
        });
        revoked.ServiceConsent.ShouldBeTrue();
        revoked.MarketingConsent.ShouldBeFalse();
        var consents = await GetAsync<List<ConsentDto>>(shop.Cashier, $"/api/v1/customers/{laura.PartyId}/consents");
        consents.Count.ShouldBe(3);
        consents.ShouldAllBe(c => c.PolicyVersion == 1);
        await Should.ThrowAsync<Npgsql.PostgresException>(() => shop.Catalog.ExecuteAsync("UPDATE customers.customer_consents SET granted = true"));

        // Una compra con su historial.
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        await SendAsync<SaleDto>(shop.Cashier, HttpMethod.Put, $"/api/v1/sales/{sale.Id}/customer", new { partyId = laura.PartyId });
        await shop.ScanAsync(sale.Id, "ARROZ-500");
        await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 3_000m)]);
        (await GetAsync<CustomerSalesSummaryDto>(shop.Supervisor, $"/api/v1/customers/{laura.PartyId}/summary")).TotalPurchased.ShouldBe(3_000m);

        // Solicitud de consulta: vence en 10 días hábiles con los festivos de Colombia.
        var request = await PostAsync<DataRequestDto>(shop.Owner, "/api/v1/customers/data-requests", new
        {
            partyId = laura.PartyId, type = "Query", channel = "Email", detail = "Solicita copia de sus datos personales",
        }, HttpStatusCode.Created);
        request.DueOn.ShouldBe(ColombianCalendar.AddBusinessDays(request.ReceivedOn, 10));
        (await GetAsync<PrivacyStatusDto>(shop.Owner, "/api/v1/customers/privacy-status")).OpenRequests.ShouldBe(1);

        // Exportación de sus datos y respuesta de la solicitud.
        var export = await PostAsync<CustomerExportDto>(shop.Owner, $"/api/v1/customers/{laura.PartyId}/export");
        export.Consents.Count.ShouldBe(3);
        export.Purchases.Purchases.ShouldBe(1);
        (await PostAsync<DataRequestDto>(shop.Owner, $"/api/v1/customers/data-requests/{request.Id}/close", new { resolved = true, response = "Se envió el archivo por correo" }))
            .Status.ShouldBe("RESOLVED");

        // Supresión: se anonimiza el perfil; la venta conserva su snapshot.
        var anonymized = await PostAsync<CustomerDto>(shop.Owner, $"/api/v1/customers/{laura.PartyId}/anonymize", new { reason = "Solicitud de supresión del titular" });
        anonymized.DisplayName.ShouldBe("TITULAR SUPRIMIDO");
        anonymized.Status.ShouldBe("INACTIVE");
        anonymized.ServiceConsent.ShouldBeFalse();
        anonymized.Phone.ShouldBeNull();
        (await GetAsync<List<CustomerLookupDto>>(shop.Cashier, "/api/v1/customers/lookup?q=laura%20mejia")).ShouldBeEmpty();
        (await GetAsync<SaleDto>(shop.Supervisor, $"/api/v1/sales/{sale.Id}")).Customer.Name.ShouldBe("Laura Mejía");
        (await shop.Catalog.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'CUSTOMER_ANONYMIZED'")).ShouldBe("WARNING");
    }
}
