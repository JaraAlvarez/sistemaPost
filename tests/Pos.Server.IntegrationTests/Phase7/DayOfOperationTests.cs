using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Server.IntegrationTests.Phase5;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7;

/// <summary>
/// "Día de operación" (criterio de aceptación de la Fase 7): tienda Multicaja con 3 cajas emparejadas y 3 cajeras que venden
/// en paralelo, con pagos mixtos, anulaciones autorizadas y cambios de mercancía; dos cajas compiten por la última unidad; al
/// cerrar, lo vendido por medio de pago cuadra al centavo con la caja y el kardex no tiene diferencias.
/// </summary>
[Collection(PerformanceCollection.Name)]
public class DayOfOperationTests
{
    /// <summary>Ventas del día (repartidas entre las cajas). Si la prueba se volviera lenta (&gt; 5 min), bajar aquí.</summary>
    private const int TotalSales = 500;

    /// <summary>Límite muy holgado del p95 de cobrar: la prueba solo informa el valor, no es una prueba de rendimiento fina.</summary>
    private static readonly TimeSpan CompleteP95Limit = TimeSpan.FromSeconds(2);

    private static readonly (string Code, string Pin)[] Cashiers = [("201", "5937"), ("202", "6284"), ("203", "7419")];

    [Fact]
    public async Task Tres_cajas_venden_500_ventas_en_paralelo_y_el_cierre_cuadra_con_la_caja_y_el_kardex()
    {
        var total = Stopwatch.StartNew();
        await using var factory = new PosServerFactory();
        var shop = await SalesScenario.CreateAsync(factory, withTerminalUsers: false);
        var owner = shop.Owner;
        var branchId = shop.Catalog.Setup.BranchId;

        // Existencias suficientes para el día y un producto con una sola unidad (la "última unidad").
        var last = (await shop.Catalog.ProductAsync("ULTIMO-1", "Último paquete de café", shop.Groceries, price: 8_000m)).Id;
        await shop.Purchasing.BuyAsync(
            "FE-900",
            new { productId = shop.Rice, quantity = 1_500, unitCost = 1_000 },
            new { productId = shop.Soda, quantity = 1_500, unitCost = 1_200 },
            new { productId = shop.Yogurt, quantity = 1_500, unitCost = 2_100, lotNumber = "L-C", expiryDate = PurchasingScenario.Today.AddDays(90) },
            new { productId = last, quantity = 1, unitCost = 5_000 });
        var products = new[] { shop.Rice, shop.Soda, shop.Yogurt };
        var initialStock = new Dictionary<Guid, decimal>();
        foreach (var product in products.Append(last))
        {
            initialStock[product] = await shop.StockAsync(product);
        }

        // Multicaja: la caja de la instalación y 2 más, cada una emparejada con su equipo; 3 cajeras y un supervisor.
        var terminalIds = new List<Guid> { shop.Catalog.Setup.PosTerminalId };
        foreach (var code in new[] { "C02", "C03" })
        {
            terminalIds.Add((await PostAsync<TerminalDto>(owner, $"/api/v1/organization/branches/{branchId}/terminals",
                new { code, name = $"Caja {code}" }, HttpStatusCode.Created)).Id);
        }

        await shop.Security.CreateUserAsync("supervisor", "CASH_SUPERVISOR", posCode: SupervisorCode, pin: SupervisorPin);
        var tills = new List<Till>();
        for (var i = 0; i < terminalIds.Count; i++)
        {
            await shop.Security.CreateUserAsync($"cajera{i + 1}", "CASHIER", posCode: Cashiers[i].Code, pin: Cashiers[i].Pin);
            var (client, device) = await shop.Security.PairTerminalAsync(terminalIds[i], ip: $"192.168.1.{21 + i}", hostname: $"CAJA-0{i + 1}",
                fingerprint: (char)('a' + i));
            device.PosTerminalId.ShouldBe(terminalIds[i]);
            var login = await SecurityScenario.PosLoginAsync(client, Cashiers[i].Code, Cashiers[i].Pin);
            login.User.PosTerminalId.ShouldBe(terminalIds[i]);
            var session = await shop.OpenSessionAsync(client);
            session.PosTerminalId.ShouldBe(terminalIds[i]);
            tills.Add(new Till(i, client, session));
        }

        // 500 ventas en paralelo entre cajas (dentro de una caja son secuenciales: una sola venta abierta por caja).
        var completeTimes = new ConcurrentBag<double>();
        var sold = new ConcurrentDictionary<Guid, decimal>();
        var dayWatch = Stopwatch.StartNew();
        await Task.WhenAll(tills.Select(till => Task.Run(() => RunTillAsync(shop, till, products, TotalSales / tills.Count + (till.Index < TotalSales % tills.Count ? 1 : 0),
            completeTimes, sold), Ct)));
        dayWatch.Stop();

        // Dos cajas por la última unidad: las dos la escanean (hay 1), cobran a la vez; exactamente una gana.
        var contenders = tills.Take(2).ToList();
        var raceSales = new List<SaleDto>();
        foreach (var till in contenders)
        {
            var sale = await shop.StartAsync(till.Client);
            raceSales.Add(await shop.AddAsync(sale.Id, last, 1m, till.Client));
        }

        var race = await Task.WhenAll(contenders.Select((till, i) => RawAsync(till.Client, HttpMethod.Post, $"/api/v1/sales/{raceSales[i].Id}/complete",
            new { payments = new[] { Pay(shop.Cash, 10_000m) } })));
        race.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var loserIndex = Array.FindIndex(race, r => r.StatusCode != HttpStatusCode.OK);
        race[loserIndex].StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await race[loserIndex].Content.ReadAsStringAsync(Ct));
        (await ErrorCodeAsync(race[loserIndex])).ShouldBe("SALES.INSUFFICIENT_STOCK");
        var winner = contenders[1 - loserIndex];
        winner.CashIn += (await GetAsync<SaleDto>(winner.Client, $"/api/v1/sales/{raceSales[1 - loserIndex].Id}")).Total;
        winner.Completed++;
        sold.AddOrUpdate(last, 1m, (_, q) => q + 1m);
        (await shop.StockAsync(last)).ShouldBe(0m);

        // La venta que perdió queda abierta: se cancela con autorización para poder cerrar la caja.
        var loser = contenders[loserIndex];
        (await shop.AuthorizedAsync<SaleDto>(HttpMethod.Post, $"/api/v1/sales/{raceSales[loserIndex].Id}/cancel", new { reason = "Sin existencias" },
            client: loser.Client)).Status.ShouldBe("CANCELLED");

        // Cierre de las 3 jornadas: se cuenta exactamente lo esperado de cada medio → diferencia cero.
        foreach (var till in tills)
        {
            (await PostAsync<CashSessionDto>(till.Client, $"/api/v1/cash/sessions/{till.Session.Id}/start-closing")).Status.ShouldBe("CLOSING");
            var x = await GetAsync<CashReportDto>(owner, $"/api/v1/cash/sessions/{till.Session.Id}/report-x");
            x.Totals.Single(t => t.Code == "EFECTIVO").Expected.ShouldBe(till.Session.OpeningFloat + till.CashIn);
            x.Totals.Single(t => t.Code == "DEBITO").Expected.ShouldBe(till.DebitIn);
            var count = x.Totals.Where(t => t.AffectsCashDrawer || t.Expected is > 0m)
                .Select(t => new { paymentMethodId = t.PaymentMethodId, amount = t.Expected!.Value }).ToArray();
            var z = await PostAsync<CashReportDto>(till.Client, $"/api/v1/cash/sessions/{till.Session.Id}/close", new { count });
            z.Kind.ShouldBe("Z");
            z.Difference.ShouldBe(0m);
        }

        // Σ ventas por medio de pago (ventas vigentes, sin el crédito de cambio que no entra a la caja) = Σ movimientos de venta por medio.
        foreach (var method in new[] { shop.Cash, shop.Debit })
        {
            var bySales = await shop.Catalog.ScalarAsync<decimal>($"""
                SELECT COALESCE(sum(p.applied), 0) FROM sales.sale_payments p JOIN sales.sales s ON s.id = p.sale_id
                WHERE s.status = 'COMPLETED' AND p.payment_method_id = '{method}'
                """);
            var byCash = await shop.Catalog.ScalarAsync<decimal>($"""
                SELECT COALESCE(sum(direction * amount), 0) FROM cash.cash_movements
                WHERE movement_type IN ('SALE', 'SALE_VOID') AND payment_method_id = '{method}'
                """);
            byCash.ShouldBe(bySales);
            bySales.ShouldBe(method == shop.Cash ? tills.Sum(t => t.CashIn) : tills.Sum(t => t.DebitIn));
        }

        // Número sin repetir por caja y todas las ventas del día contadas.
        var completed = tills.Sum(t => t.Completed);
        var voided = tills.Sum(t => t.Voided);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM sales.sales WHERE status = 'COMPLETED'")).ShouldBe(completed);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM sales.sales WHERE status = 'VOIDED'")).ShouldBe(voided);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(DISTINCT (pos_terminal_id, number)) FROM sales.sales WHERE number IS NOT NULL"))
            .ShouldBe(completed + voided);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(DISTINCT pos_terminal_id) FROM sales.sales WHERE status = 'COMPLETED'")).ShouldBe(3);

        // Kardex: existencias = compradas − vendidas (neto de anulaciones y de lo devuelto por cambios) y la verificación sin diferencias.
        foreach (var product in products.Append(last))
        {
            (await shop.StockAsync(product)).ShouldBe(initialStock[product] - sold.GetValueOrDefault(product));
        }

        var verification = await PostAsync<VerificationDto>(owner, "/api/v1/inventory/verification");
        verification.CheckedBalances.ShouldBeGreaterThan(0);
        verification.Discrepancies.ShouldBe(0, string.Join("; ", verification.Details));

        var times = completeTimes.Order().ToArray();
        var p95 = times[(int)Math.Ceiling(times.Length * 0.95) - 1];
        var message = $"Día de operación: {TotalSales} ventas en {tills.Count} cajas + 1 por la última unidad ({completed} vigentes, {voided} anuladas, "
            + $"{tills.Sum(t => t.Exchanges)} cambios) en {dayWatch.Elapsed.TotalSeconds:0.0} s · cobrar p50 = {times[times.Length / 2]:0.0} ms, "
            + $"p95 = {p95:0.0} ms, máx = {times[^1]:0.0} ms · prueba completa {total.Elapsed.TotalSeconds:0.0} s";
        TestContext.Current.SendDiagnosticMessage(message);
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        p95.ShouldBeLessThan(CompleteP95Limit.TotalMilliseconds);
    }

    /// <summary>
    /// Una caja durante el día: ventas de 1 a 3 productos con efectivo con cambio, débito con referencia o mixto; cada 25 ventas
    /// una anulación autorizada y cada 40 un cambio de mercancía (1 unidad vuelve a la bodega, la cliente lleva 2 y paga la diferencia).
    /// </summary>
    private static async Task RunTillAsync(
        SalesScenario shop, Till till, Guid[] products, int count, ConcurrentBag<double> completeTimes, ConcurrentDictionary<Guid, decimal> sold)
    {
        var random = new Random(1_000 + till.Index);
        var kept = new List<SaleDto>();
        for (var n = 1; n <= count; n++)
        {
            SaleDto done;
            if (n % 40 == 0 && kept.Count > 0)
            {
                done = await ExchangeAsync(shop, till, kept, n, completeTimes, sold);
            }
            else
            {
                var sale = await shop.StartAsync(till.Client);
                var lines = random.Next(1, 4);
                foreach (var product in products.OrderBy(_ => random.Next()).Take(lines))
                {
                    sale = await shop.AddAsync(sale.Id, product, random.Next(1, 4), till.Client);
                }

                var payments = (n % 3) switch
                {
                    0 => [Pay(shop.Debit, sale.AmountDue, $"APR-{till.Index}-{n}")],
                    1 => new[] { Pay(shop.Cash, Math.Ceiling(sale.AmountDue / 10_000m) * 10_000m) },
                    _ => [Pay(shop.Debit, 2_000m, $"APR-{till.Index}-{n}"), Pay(shop.Cash, Math.Ceiling((sale.AmountDue - 2_000m) / 5_000m) * 5_000m)],
                };
                done = await TimedCompleteAsync(shop, till, sale.Id, payments, completeTimes);
                done.Status.ShouldBe("COMPLETED");
                done.ChangeTotal.ShouldBe(done.Payments.Sum(p => p.Change));
                (done.PaidTotal - done.ChangeTotal).ShouldBe(done.Total);
                foreach (var line in done.Lines.Where(l => l.Status == "ACTIVE"))
                {
                    sold.AddOrUpdate(line.ProductId, line.Quantity, (_, q) => q + line.Quantity);
                }
            }

            Account(till, done, +1);
            till.Completed++;

            if (n % 25 == 0 && done.ExchangeId is null)
            {
                var voided = await shop.AuthorizedAsync<SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{done.Id}/void",
                    new { reason = "Cobro repetido por error" }, client: till.Client);
                voided.Sale.Status.ShouldBe("VOIDED");
                Account(till, done, -1);
                till.Completed--;
                till.Voided++;
                foreach (var line in done.Lines.Where(l => l.Status == "ACTIVE"))
                {
                    sold.AddOrUpdate(line.ProductId, -line.Quantity, (_, q) => q - line.Quantity);
                }
            }
            else if (done.ExchangeId is null)
            {
                kept.Add(done);
            }
        }
    }

    /// <summary>Cambio de 1 unidad de una venta anterior de la misma caja: vuelve a la bodega y la venta nueva lleva 2 del mismo producto.</summary>
    private static async Task<SaleDto> ExchangeAsync(
        SalesScenario shop, Till till, List<SaleDto> kept, int n, ConcurrentBag<double> completeTimes, ConcurrentDictionary<Guid, decimal> sold)
    {
        var original = kept[0];
        kept.RemoveAt(0);
        var line = original.Lines.First(l => l.Status == "ACTIVE");
        var body = new
        {
            originalSaleId = original.Id, reason = "Cliente cambia de referencia",
            lines = new[] { new { saleLineId = line.Id, quantity = 1m, destination = "ReturnToStock" } },
        };
        var grant = await shop.GrantAsync(await RawAsync(till.Client, HttpMethod.Post, "/api/v1/exchanges", body), till.Client);
        var started = await SendAsync<ExchangeStartedDto>(till.Client, HttpMethod.Post, "/api/v1/exchanges", body, HttpStatusCode.Created, grant: grant);
        sold.AddOrUpdate(line.ProductId, -1m, (_, q) => q - 1m);

        var replacement = await shop.AddAsync(started.Sale.Id, line.ProductId, 2m, till.Client);
        object[] payments = replacement.AmountDue > 0m ? [Pay(shop.Cash, Math.Ceiling(replacement.AmountDue / 1_000m) * 1_000m)] : [];
        var done = await TimedCompleteAsync(shop, till, replacement.Id, payments, completeTimes);
        done.Status.ShouldBe("COMPLETED");
        done.Payments.Single(p => p.MethodKind == "EXCHANGE_CREDIT").Applied.ShouldBe(started.Exchange.CreditTotal);
        sold.AddOrUpdate(line.ProductId, 2m, (_, q) => q + 2m);
        till.Exchanges++;
        (await GetAsync<ExchangeDto>(till.Client, $"/api/v1/exchanges/{started.Exchange.Id}")).Status.ShouldBe("COMPLETED");
        return done;
    }

    private static async Task<SaleDto> TimedCompleteAsync(SalesScenario shop, Till till, Guid saleId, object[] payments, ConcurrentBag<double> completeTimes)
    {
        var watch = Stopwatch.StartNew();
        var receipt = await shop.CompleteAsync(saleId, payments, till.Client, key: $"cobro-{saleId}");
        completeTimes.Add(watch.Elapsed.TotalMilliseconds);
        return receipt.Sale;
    }

    /// <summary>Lo que la venta metió a la caja por medio (el crédito de cambio no entra).</summary>
    private static void Account(Till till, SaleDto sale, int sign)
    {
        foreach (var payment in sale.Payments)
        {
            switch (payment.MethodCode)
            {
                case "EFECTIVO":
                    till.CashIn += sign * payment.Applied;
                    break;
                case "DEBITO":
                    till.DebitIn += sign * payment.Applied;
                    break;
            }
        }
    }

    private sealed class Till(int index, HttpClient client, CashSessionDto session)
    {
        public int Index { get; } = index;

        public HttpClient Client { get; } = client;

        public CashSessionDto Session { get; } = session;

        public decimal CashIn { get; set; }

        public decimal DebitIn { get; set; }

        public int Completed { get; set; }

        public int Voided { get; set; }

        public int Exchanges { get; set; }
    }
}
