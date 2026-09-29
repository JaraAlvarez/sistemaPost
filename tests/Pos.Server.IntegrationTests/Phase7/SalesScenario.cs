using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Server.IntegrationTests.Phase4;
using Pos.Server.IntegrationTests.Phase5;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase7;

/// <summary>
/// Atajos de la Fase 7: tienda Caja Única con arroz (SKU ARROZ-500, $3.000), gaseosa ($2.500), tomate por peso ($4.980/kg, IVA
/// excluido) y yogurt con lotes ($3.500); existencias compradas; cajera (200/5937) y supervisor (300/7152) en la caja.
/// </summary>
public sealed class SalesScenario
{
    public const string CashierCode = "200";
    public const string CashierPin = "5937";
    public const string SupervisorCode = "300";
    public const string SupervisorPin = "7152";

    private SalesScenario(PurchasingScenario purchasing, SecurityScenario security)
    {
        Purchasing = purchasing;
        Security = security;
    }

    public PurchasingScenario Purchasing { get; }

    public SecurityScenario Security { get; }

    public CatalogScenario Catalog => Purchasing.Catalog;

    public HttpClient Owner => Purchasing.Owner;

    public HttpClient Cashier { get; private set; } = null!;

    public HttpClient Supervisor { get; private set; } = null!;

    public Guid SupervisorId { get; private set; }

    public Guid Rice => Purchasing.Rice;

    public Guid Yogurt => Purchasing.Yogurt;

    public Guid Soda { get; private set; }

    public Guid Tomato { get; private set; }

    public Guid Groceries { get; private set; }

    public Guid Cash => Purchasing.Cash;

    public Guid Transfer => Purchasing.Transfer;

    public Guid Debit { get; private set; }

    public Guid Bill50 { get; private set; }

    public static async Task<SalesScenario> CreateAsync(PosServerFactory factory, bool withTerminalUsers = true)
    {
        var purchasing = await PurchasingScenario.CreateAsync(factory);
        var security = SecurityScenario.ForExisting(factory, purchasing.Owner, purchasing.Catalog.Setup);
        var scenario = new SalesScenario(purchasing, security);
        var catalog = purchasing.Catalog;
        scenario.Groceries = await catalog.CategoryAsync("Abarrotes");
        scenario.Soda = (await catalog.ProductAsync("GAS-400", "Gaseosa 400 ml", scenario.Groceries, barcode: "7702004003508", price: 2_500m)).Id;
        var produce = await catalog.CategoryAsync("Fruver");
        var excluded = await catalog.TaxIdAsync("IVA_EXCLUIDO");
        scenario.Tomato = (await catalog.ProductAsync("TOMATE", "Tomate chonto", produce, price: 4_980m, unit: "KG", saleMode: "Weight", scale: true, plu: "00123",
            taxes: [new { taxId = excluded }])).Id;
        foreach (var rule in await GetAsync<List<BarcodeRuleDto>>(scenario.Owner, "/api/v1/catalog/barcode-rules"))
        {
            (await scenario.Owner.PutAsJsonAsync($"/api/v1/catalog/barcode-rules/{rule.Id}", new
            {
                prefix = rule.Prefix, content = rule.Content == "WEIGHT" ? "Weight" : "Price", pluStart = rule.PluStart, pluLength = rule.PluLength,
                valueStart = rule.ValueStart, valueLength = rule.ValueLength, valueDecimals = rule.ValueDecimals, isActive = true,
            }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var methods = await GetAsync<List<PaymentMethodDto>>(scenario.Owner, "/api/v1/cash/payment-methods");
        scenario.Debit = methods.Single(m => m.Code == "DEBITO").Id;
        var denominations = await GetAsync<List<DenominationDto>>(scenario.Owner, "/api/v1/cash/denominations");
        scenario.Bill50 = denominations.Single(d => d.Value == 50_000m).Id;

        // Existencias: 50 arroz, 40 gaseosas, 20 kg de tomate y yogurt en dos lotes (vence en 10 y en 60 días).
        var today = PurchasingScenario.Today;
        await purchasing.BuyAsync(
            "FE-700",
            new { productId = scenario.Rice, quantity = 50, unitCost = 1_000 },
            new { productId = scenario.Soda, quantity = 40, unitCost = 1_200 },
            new { productId = scenario.Tomato, quantity = 20, unitCost = 2_000 },
            new { productId = scenario.Yogurt, quantity = 10, unitCost = 2_000, lotNumber = "L-A", expiryDate = today.AddDays(10) },
            new { productId = scenario.Yogurt, quantity = 10, unitCost = 2_200, lotNumber = "L-B", expiryDate = today.AddDays(60) });

        if (withTerminalUsers)
        {
            await security.CreateUserAsync("cajera", "CASHIER", posCode: CashierCode, pin: CashierPin);
            scenario.SupervisorId = await security.CreateUserAsync("supervisor", "CASH_SUPERVISOR", posCode: SupervisorCode, pin: SupervisorPin);
            scenario.Cashier = factory.CreateClient();
            await SecurityScenario.PosLoginAsync(scenario.Cashier, CashierCode, CashierPin);
            scenario.Supervisor = factory.CreateClient();
            await SecurityScenario.PosLoginAsync(scenario.Supervisor, SupervisorCode, SupervisorPin);
        }

        return scenario;
    }

    public static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected = HttpStatusCode.OK,
        Guid? grant = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body ?? new { }, options: Json) };
        if (grant is { } g)
        {
            request.Headers.Add("X-Authorization-Grant", g.ToString());
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    public static Task<T> PostAsync<T>(HttpClient client, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK, Guid? grant = null) =>
        SendAsync<T>(client, HttpMethod.Post, url, body, expected, grant);

    public static async Task<HttpResponseMessage> RawAsync(HttpClient client, HttpMethod method, string url, object? body = null, Guid? grant = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body ?? new { }, options: Json) };
        if (grant is { } g)
        {
            request.Headers.Add("X-Authorization-Grant", g.ToString());
        }

        return await client.SendAsync(request, Ct);
    }

    public Task<CashSessionDto> OpenSessionAsync(HttpClient? client = null, int bills50 = 2) =>
        PostAsync<CashSessionDto>(client ?? Cashier, "/api/v1/cash/sessions", new
        {
            openingFloat = bills50 * 50_000m,
            openingCount = new[] { new { paymentMethodId = Cash, denominationId = Bill50, quantity = bills50 } },
        }, HttpStatusCode.Created);

    public Task<SaleDto> StartAsync(HttpClient? client = null) => PostAsync<SaleDto>(client ?? Cashier, "/api/v1/sales", null, HttpStatusCode.Created);

    public Task<SaleDto> ScanAsync(Guid saleId, string code, decimal? quantity = null, HttpClient? client = null) =>
        PostAsync<SaleDto>(client ?? Cashier, $"/api/v1/sales/{saleId}/lines", new { code, quantity });

    public Task<SaleDto> AddAsync(Guid saleId, Guid productId, decimal quantity, HttpClient? client = null) =>
        PostAsync<SaleDto>(client ?? Cashier, $"/api/v1/sales/{saleId}/lines", new { productId, quantity });

    public static object Pay(Guid method, decimal amount, string? reference = null) => new { paymentMethodId = method, amount, reference };

    public Task<SaleReceiptDto> CompleteAsync(Guid saleId, object[] payments, HttpClient? client = null, string? key = null) =>
        SendAsync<SaleReceiptDto>(client ?? Cashier, HttpMethod.Post, $"/api/v1/sales/{saleId}/complete", new { payments }, idempotencyKey: key);

    /// <summary>El supervisor autoriza con su código y PIN la acción que la caja pidió (autorización de un solo uso).</summary>
    public async Task<Guid> GrantAsync(HttpResponseMessage denied, HttpClient? client = null, string? supervisorCode = null, string? supervisorPin = null)
    {
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync(Ct));
        var problem = await SecurityScenario.ProblemAsync(denied);
        problem.GetProperty("code").GetString().ShouldBe("AUTH.AUTHORIZATION_REQUIRED");
        var targetId = problem.TryGetProperty("targetId", out var target) && target.ValueKind == System.Text.Json.JsonValueKind.String ? target.GetGuid() : (Guid?)null;
        var response = await (client ?? Cashier).PostAsJsonAsync("/api/v1/auth/authorizations", new
        {
            supervisorCode = supervisorCode ?? SupervisorCode, supervisorPin = supervisorPin ?? SupervisorPin, permissionCode = problem.GetProperty("permission").GetString(),
            action = problem.GetProperty("action").GetString(), targetId, targetType = "Sale", reason = "Autorizado en la caja",
        }, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AuthorizationGrantDto>(Json, Ct))!.GrantId;
    }

    /// <summary>Hace la petición; si la caja necesita autorización, la pide al supervisor y la repite con la autorización.</summary>
    public async Task<T> AuthorizedAsync<T>(
        HttpMethod method, string url, object? body = null, HttpClient? client = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var first = await RawAsync(client ?? Cashier, method, url, body);
        var grant = await GrantAsync(first, client);
        return await SendAsync<T>(client ?? Cashier, method, url, body, expected, grant: grant);
    }

    public async Task<decimal> StockAsync(Guid product) => (await Purchasing.StockAsync(product)).Quantity;
}
