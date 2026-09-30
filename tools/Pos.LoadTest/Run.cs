using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Pos.LoadTest;

/// <summary>Opciones de una corrida de carga. Las <see cref="Run.WarmUpSales"/> primeras ventas de cada caja no se miden (calentamiento).</summary>
public sealed record RunOptions(TimeSpan Duration, int MaxSalesPerCashier, int MaxLines, int ThinkMilliseconds, bool Close, string OwnerUser, string OwnerPassword);

/// <summary>
/// Varias cajas vendiendo a la vez con el flujo real (D14-01): iniciar venta, escanear o agregar productos (los más vendidos según Zipf),
/// cobrar en efectivo o débito, buscar productos por nombre, cerrar la jornada con el arqueo y, al final, el reporte del mes en Excel.
/// </summary>
public static class Run
{
    public const int WarmUpSales = 2;

    public static async Task<(Metrics Metrics, int Sales, TimeSpan Elapsed)> ExecuteAsync(LoadStore store, RunOptions options, CancellationToken ct)
    {
        var metrics = new Metrics();
        var zipf = new Zipf(store.Products.Count, 1.07);
        var sales = 0;
        var watch = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + options.Duration;
        await Task.WhenAll(store.Cashiers.Select((cashier, index) => Task.Run(async () =>
        {
            var random = new Random(1_000 + index);
            using var api = new PosApi(new Uri(store.Server), metrics);
            if (cashier.DeviceId is not null)
            {
                api.UseDevice(cashier.DeviceId, cashier.DeviceSecret!);
            }

            await api.PosLoginAsync(cashier.PosCode, cashier.Pin, ct);
            var session = await OpenSessionAsync(api, store, ct);
            var cashInDrawer = store.OpeningDenominationValue * 2;
            var debitTotal = 0m;
            for (var n = 0; n < options.MaxSalesPerCashier && DateTime.UtcNow < deadline && !ct.IsCancellationRequested; n++)
            {
                try
                {
                    api.Recording = n >= WarmUpSales;
                    var (total, paidCash) = await SellAsync(api, store, zipf, random, options, ct);
                    if (paidCash)
                    {
                        cashInDrawer += total;
                    }
                    else
                    {
                        debitTotal += total;
                    }

                    Interlocked.Increment(ref sales);
                    if (n % 25 == 0)
                    {
                        var word = store.Products[random.Next(store.Products.Count)].Name.Split(' ')[0];
                        await api.GetAsync($"api/v1/catalog/products?search={Uri.EscapeDataString(word)}&pageSize=20", ct, "buscar");
                    }
                }
                catch (ApiException ex)
                {
                    metrics.Add($"error {ex.Code ?? ((int)ex.Status).ToString(CultureInfo.InvariantCulture)}", 0, ok: false);
                }

                if (options.ThinkMilliseconds > 0)
                {
                    await Task.Delay(random.Next(options.ThinkMilliseconds / 2, options.ThinkMilliseconds * 3 / 2), ct);
                }
            }

            api.Recording = true;
            if (options.Close)
            {
                await CloseAsync(api, store, session, cashInDrawer, debitTotal, ct);
            }
        }, ct)));
        var elapsed = watch.Elapsed;

        // Reporte del mes en Excel con todo el volumen (el propietario).
        using var owner = new PosApi(new Uri(store.Server), metrics);
        await owner.LoginAsync(options.OwnerUser, options.OwnerPassword, ct);
        var today = DateOnly.FromDateTime(DateTime.Now);
        await owner.DownloadAsync($"api/v1/reports/SALES_DAILY?format=xlsx&from={today.AddDays(-30):yyyy-MM-dd}&to={today:yyyy-MM-dd}", "reporte-mes-xlsx", ct);
        await owner.DownloadAsync($"api/v1/reports/SALES_BY_PRODUCT?format=xlsx&from={today.AddDays(-30):yyyy-MM-dd}&to={today:yyyy-MM-dd}", "reporte-productos-xlsx", ct);
        return (metrics, sales, elapsed);
    }

    private static async Task<Guid> OpenSessionAsync(PosApi api, LoadStore store, CancellationToken ct)
    {
        try
        {
            if (await api.GetAsync("api/v1/cash/sessions/current", ct) is { } current && current["id"] is { } id && current["status"]?.GetValue<string>() == "OPEN")
            {
                return id.GetValue<Guid>();
            }
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            // Sin jornada abierta.
        }

        var opened = (await api.PostAsync("api/v1/cash/sessions", new
        {
            openingFloat = store.OpeningDenominationValue * 2,
            openingCount = new[] { new { paymentMethodId = store.CashMethodId, denominationId = store.OpeningDenominationId, quantity = 2 } },
        }, ct, "abrir-jornada"))!;
        return opened["id"]!.GetValue<Guid>();
    }

    private static async Task<(decimal Total, bool Cash)> SellAsync(PosApi api, LoadStore store, Zipf zipf, Random random, RunOptions options, CancellationToken ct)
    {
        var sale = (await api.PostAsync("api/v1/sales", null, ct, "iniciar-venta"))!;
        var saleId = sale["id"]!.GetValue<Guid>();
        var lines = Math.Clamp(1 + (int)(-Math.Log(1 - random.NextDouble()) * 11), 1, options.MaxLines);
        JsonNode? last = null;
        for (var i = 0; i < lines || last is null; i++)
        {
            var product = store.Products[zipf.Next(random)];
            try
            {
                last = random.NextDouble() < 0.8
                    ? await api.PostAsync($"api/v1/sales/{saleId}/lines", new { code = product.Barcode }, ct, "escanear")
                    : await api.PostAsync($"api/v1/sales/{saleId}/lines", new { productId = product.Id, quantity = random.Next(1, 4) }, ct, "agregar");
            }
            catch (ApiException ex) when (ex.Code is not null && ex.Code.Contains("STOCK", StringComparison.Ordinal) && i < lines + 5)
            {
                // Sin existencias: se intenta con otro producto.
            }
        }

        var total = last!["total"]!.GetValue<decimal>();
        var cash = random.NextDouble() < 0.7;
        var payment = cash
            ? new { paymentMethodId = store.CashMethodId, amount = Math.Ceiling(total / 1_000m) * 1_000m, reference = (string?)null }
            : new { paymentMethodId = store.DebitMethodId, amount = total, reference = (string?)"4321" };
        await api.SendAsync(HttpMethod.Post, $"api/v1/sales/{saleId}/complete", new { payments = new[] { payment } }, "cobrar", ct, Guid.NewGuid().ToString());
        return (total, cash);
    }

    private static async Task CloseAsync(PosApi api, LoadStore store, Guid session, decimal cash, decimal debit, CancellationToken ct)
    {
        await api.PostAsync($"api/v1/cash/sessions/{session}/start-closing", null, ct);
        var count = new List<object>();
        var remaining = cash;
        foreach (var denomination in store.Denominations)
        {
            var quantity = (int)(remaining / denomination.Value);
            if (quantity > 0)
            {
                count.Add(new { paymentMethodId = store.CashMethodId, denominationId = (Guid?)denomination.Id, quantity = (int?)quantity, amount = (decimal?)null });
                remaining -= quantity * denomination.Value;
            }
        }

        if (debit > 0)
        {
            count.Add(new { paymentMethodId = store.DebitMethodId, denominationId = (Guid?)null, quantity = (int?)null, amount = (decimal?)debit });
        }

        await api.PostAsync($"api/v1/cash/sessions/{session}/close", new { count, differenceNote = "Cierre de la prueba de carga" }, ct, "cerrar-jornada");
    }
}

/// <summary>Distribución de Zipf: pocos productos concentran la mayoría de las ventas (D14 riesgo "datos poco realistas").</summary>
public sealed class Zipf
{
    private readonly double[] _cumulative;

    public Zipf(int count, double exponent)
    {
        _cumulative = new double[count];
        var sum = 0d;
        for (var i = 0; i < count; i++)
        {
            sum += 1 / Math.Pow(i + 1, exponent);
            _cumulative[i] = sum;
        }

        for (var i = 0; i < count; i++)
        {
            _cumulative[i] /= sum;
        }
    }

    public int Next(Random random)
    {
        var index = Array.BinarySearch(_cumulative, random.NextDouble());
        return Math.Min(index < 0 ? ~index : index, _cumulative.Length - 1);
    }
}
