using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Server.IntegrationTests.Phase4;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase5;

/// <summary>Atajos de la Fase 5: tienda con un proveedor, productos (uno con lotes y caja x24) y medios de pago.</summary>
public sealed class PurchasingScenario
{
    private static readonly string[] GreatTaxpayer = ["O-13"];

    private PurchasingScenario(CatalogScenario catalog) => Catalog = catalog;

    public CatalogScenario Catalog { get; }

    public HttpClient Owner => Catalog.Owner;

    public Guid Floor { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid PartyId { get; private set; }

    public Guid Yogurt { get; private set; }

    public Guid YogurtBox { get; private set; }

    public Guid Rice { get; private set; }

    public Guid Cash { get; private set; }

    public Guid Transfer { get; private set; }

    public static async Task<PurchasingScenario> CreateAsync(PosServerFactory factory)
    {
        var scenario = new PurchasingScenario(await CatalogScenario.CreateAsync(factory));
        var owner = scenario.Owner;
        var branch = await GetAsync<BranchDetailDto>(owner, $"/api/v1/organization/branches/{scenario.Catalog.Setup.BranchId}");
        scenario.Floor = branch.Warehouses.Single(w => w.Kind == "SalesFloor").Id;

        var party = await scenario.CreatePartyAsync(new
        {
            personType = "Legal", identificationType = "NIT", identificationNumber = "800197268", checkDigit = "4", legalName = "Distribuidora Láctea SAS",
            taxRegime = "48", fiscalResponsibilities = GreatTaxpayer,
        });
        scenario.PartyId = party.Id;
        var supplier = await owner.PostAsJsonAsync("/api/v1/purchasing/suppliers",
            new { partyId = party.Id, supplier = new { code = "DISLAC", paymentTermDays = 30, issuesInvoices = true } }, Json, Ct);
        supplier.StatusCode.ShouldBe(HttpStatusCode.Created, await supplier.Content.ReadAsStringAsync(Ct));
        scenario.SupplierId = (await supplier.Content.ReadFromJsonAsync<SupplierDto>(Json, Ct))!.Id;

        var category = await scenario.Catalog.CategoryAsync("Lácteos");
        var yogurt = await owner.PostAsJsonAsync("/api/v1/catalog/products", new
        {
            product = new
            {
                sku = "YOG-1", name = "Yogurt fresa 1 L", categoryId = category, baseUnitCode = "UND", saleMode = "Unit", productType = "Stockable",
                tracksLots = true, tracksExpiry = true,
            },
            price = 3_500m,
        }, Json, Ct);
        yogurt.StatusCode.ShouldBe(HttpStatusCode.Created, await yogurt.Content.ReadAsStringAsync(Ct));
        scenario.Yogurt = (await yogurt.Content.ReadFromJsonAsync<ProductDetailDto>(Json, Ct))!.Id;
        var box = await owner.PostAsJsonAsync($"/api/v1/catalog/products/{scenario.Yogurt}/packagings", new { name = "Caja x24", factor = 24 }, Json, Ct);
        box.StatusCode.ShouldBe(HttpStatusCode.Created, await box.Content.ReadAsStringAsync(Ct));
        scenario.YogurtBox = (await box.Content.ReadFromJsonAsync<ProductDetailDto>(Json, Ct))!.Packagings.Single().Id;
        scenario.Rice = (await scenario.Catalog.ProductAsync("ARROZ-500", "Arroz 500 g", category, price: 3_000m)).Id;

        var methods = await GetAsync<List<PaymentMethodDto>>(owner, "/api/v1/cash/payment-methods");
        scenario.Cash = methods.Single(m => m.Code == "EFECTIVO").Id;
        scenario.Transfer = methods.Single(m => m.Code == "TRANSFERENCIA").Id;
        return scenario;
    }

    public async Task<PartyDto> CreatePartyAsync(object party)
    {
        var response = await Owner.PostAsJsonAsync("/api/v1/parties", party, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PartyDto>(Json, Ct))!;
    }

    /// <summary>Compra en borrador (IVA del producto; sin total de factura → se toma el calculado).</summary>
    public async Task<PurchaseDto> DraftAsync(string invoice, object[] lines, string mode = "Credit", Guid? method = null, Guid? orderId = null,
        decimal charges = 0m, object[]? withholdings = null, decimal? invoiceTotal = null, HttpClient? client = null)
    {
        var response = await (client ?? Owner).PostAsJsonAsync("/api/v1/purchasing/purchases", new
        {
            supplierId = SupplierId, warehouseId = Floor, purchaseOrderId = orderId, supplierInvoiceNumber = invoice, invoiceDate = Today, paymentMode = mode,
            paymentMethodId = method, paymentReference = method == Transfer ? "TRF-1" : null, invoiceTotal, proration = "Value", chargesTotal = charges,
            lines, withholdings,
        }, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var draft = (await response.Content.ReadFromJsonAsync<PurchaseDto>(Json, Ct))!;
        if (invoiceTotal is null)
        {
            // El total de la factura se digita aparte: se completa con el calculado para poder contabilizar.
            var updated = await (client ?? Owner).PutAsJsonAsync($"/api/v1/purchasing/purchases/{draft.Id}", new
            {
                supplierId = SupplierId, warehouseId = Floor, purchaseOrderId = orderId, supplierInvoiceNumber = invoice, invoiceDate = Today, paymentMode = mode,
                paymentMethodId = method, paymentReference = method == Transfer ? "TRF-1" : null, invoiceTotal = draft.Total, proration = "Value",
                chargesTotal = charges, lines = draft.Lines.Select(l => new
                {
                    productId = l.ProductId, packagingId = l.PackagingId, quantity = l.Quantity, unitCost = l.UnitCost, discount = l.DiscountAmount,
                    lotNumber = l.LotNumber, expiryDate = l.ExpiryDate, orderLineId = l.OrderLineId,
                }),
                withholdings,
            }, Json, Ct);
            updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
            draft = (await updated.Content.ReadFromJsonAsync<PurchaseDto>(Json, Ct))!;
        }

        return draft;
    }

    public async Task<PurchaseDto> PostAsync(Guid purchaseId, HttpClient? client = null)
    {
        var response = await (client ?? Owner).PostAsync($"/api/v1/purchasing/purchases/{purchaseId}/post", null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PurchaseDto>(Json, Ct))!;
    }

    public async Task<PurchaseDto> BuyAsync(string invoice, params object[] lines) => await PostAsync((await DraftAsync(invoice, lines)).Id);

    public async Task<T> SendAsync<T>(string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await Owner.PostAsJsonAsync(url, body ?? new { }, Json, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    public async Task<StockDto> StockAsync(Guid product) =>
        (await GetAsync<List<StockDto>>(Owner, $"/api/v1/inventory/stock?warehouseId={Floor}&productId={product}")).Single();

    public async Task<List<LotStockDto>> LotsAsync(Guid product) => await GetAsync<List<LotStockDto>>(Owner, $"/api/v1/inventory/lots?productId={product}");

    /// <summary>Fecha de negocio (Colombia, UTC−5).</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
}
