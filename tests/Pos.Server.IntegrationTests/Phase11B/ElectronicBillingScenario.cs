using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Modules.Billing.Application;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase11B;

/// <summary>
/// Caja Única con el proveedor fiscal SIMULADO (<see cref="FakeFiscalProvider"/>). Por defecto la cola NO corre sola: las pruebas la
/// dirigen con <c>POST /billing/queue/process</c> para que sean deterministas.
/// </summary>
public class ElectronicBillingServerFactory : PosServerFactory
{
    public FakeFiscalProvider Fiscal { get; } = new();

    protected virtual bool Worker => false;

    protected override string Edition => "SINGLE";

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Pos:Billing:Worker", Worker ? "true" : "false"),
        new("Pos:Billing:QueueIntervalSeconds", "1"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IFiscalProvider>(Fiscal)));
    }
}

/// <summary>Igual, con el proceso en segundo plano encendido (para la espera del tiquete).</summary>
public sealed class WorkerBillingServerFactory : ElectronicBillingServerFactory
{
    protected override bool Worker => true;
}

/// <summary>Atajos de la Fase 11-B sobre el escenario de ventas de la Fase 7.</summary>
internal static class ElectronicBilling
{
    public const string Password = "Clave-Factus-Secreta-123";
    public const string ClientSecret = "secreto-del-cliente-xyz";

    public static async Task<BillingSettingsDto> CredentialsAsync(HttpClient owner) =>
        await SendAsync<BillingSettingsDto>(owner, HttpMethod.Put, "/api/v1/billing/settings/credentials", new
        {
            username = "tienda@correo.co", password = Password, clientId = "cliente-9", clientSecret = ClientSecret,
        });

    /// <summary>Credenciales, modo, sincronización de rangos y asignación de todos a la sucursal.</summary>
    public static async Task<List<FiscalRangeDto>> EnableAsync(SalesScenario shop, string mode = "EVERY_SALE")
    {
        await CredentialsAsync(shop.Owner);
        (await SendAsync<BillingSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/billing/settings", new { mode, environment = "SANDBOX" })).Mode.ShouldBe(mode);
        var ranges = await PostAsync<List<FiscalRangeDto>>(shop.Owner, "/api/v1/billing/ranges/sync");
        var assigned = new List<FiscalRangeDto>();
        foreach (var range in ranges)
        {
            assigned.Add(await AssignAsync(shop, range.Id, shop.Catalog.Setup.BranchId));
        }

        return assigned;
    }

    public static Task<FiscalRangeDto> AssignAsync(SalesScenario shop, Guid rangeId, Guid? branchId, Guid? terminalId = null) =>
        SendAsync<FiscalRangeDto>(shop.Owner, HttpMethod.Put, $"/api/v1/billing/ranges/{rangeId}/assignment", new { branchId, posTerminalId = terminalId });

    public static Task<FiscalQueueRun> ProcessAsync(SalesScenario shop) => PostAsync<FiscalQueueRun>(shop.Owner, "/api/v1/billing/queue/process");

    public static Task<List<FiscalDocumentDto>> DocumentsAsync(SalesScenario shop, string query = "") =>
        GetAsync<List<FiscalDocumentDto>>(shop.Owner, $"/api/v1/billing/documents{query}");

    public static Task<FiscalDocumentDto> DocumentAsync(SalesScenario shop, Guid id) => GetAsync<FiscalDocumentDto>(shop.Owner, $"/api/v1/billing/documents/{id}");

    public static async Task<FiscalDocumentDto> ForSourceAsync(SalesScenario shop, Guid sourceId, string source = "SALE") =>
        (await DocumentsAsync(shop, $"?source={source}")).Single(d => d.SourceId == sourceId);

    /// <summary>Vence las esperas de los documentos pendientes (simula que pasó el tiempo de reintento).</summary>
    public static Task DueNowAsync(SalesScenario shop) =>
        shop.Catalog.ExecuteAsync("UPDATE billing.fiscal_documents SET next_attempt_at = now() - interval '1 second' WHERE status IN ('PENDING', 'ERROR', 'CONTINGENCY')");

    /// <summary>Venta de 2 arroces ($6.000) y una gaseosa ($2.500) en efectivo.</summary>
    public static async Task<SaleReceiptDto> SellAsync(SalesScenario shop, decimal cash = 10_000m)
    {
        var sale = await shop.StartAsync();
        await shop.AddAsync(sale.Id, shop.Rice, 2);
        await shop.AddAsync(sale.Id, shop.Soda, 1);
        return await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, cash)]);
    }

    public static async Task<int> CountAsync(SalesScenario shop, string sql) => (int)(await shop.Catalog.ScalarAsync<long>(sql));

    public static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body ?? new { }, options: Json) };
        return (await client.SendAsync(request, Ct)).StatusCode;
    }
}
