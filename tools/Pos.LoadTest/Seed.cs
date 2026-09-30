using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pos.LoadTest;

/// <summary>Lo que la corrida necesita de la tienda preparada (se guarda en <c>carga.json</c>).</summary>
public sealed record LoadStore(
    string Server,
    string Edition,
    Guid CashMethodId,
    Guid DebitMethodId,
    Guid OpeningDenominationId,
    decimal OpeningDenominationValue,
    IReadOnlyList<Denomination> Denominations,
    IReadOnlyList<LoadProduct> Products,
    IReadOnlyList<LoadCashier> Cashiers);

public sealed record Denomination(Guid Id, decimal Value);

public sealed record LoadProduct(Guid Id, string Barcode, string Name);

public sealed record LoadCashier(string PosCode, string Pin, string TerminalCode, string? DeviceId, string? DeviceSecret);

/// <summary>
/// Prepara una tienda de prueba de tamaño real (D14-02) SOLO por la API (así se cumplen todas las reglas): productos con código de
/// barras, existencias iniciales, cajas, cajeros y, en Multicaja, un equipo emparejado por caja.
/// </summary>
public static class Seed
{
    private static readonly string[] Words =
    [
        "arroz", "leche", "aceite", "azúcar", "café", "chocolate", "galletas", "jabón", "detergente", "papel", "atún", "frijol", "lenteja",
        "pasta", "harina", "sal", "panela", "yogur", "queso", "mantequilla", "gaseosa", "jugo", "agua", "cerveza", "vino", "pan", "huevos",
    ];

    public static async Task<LoadStore> RunAsync(Uri server, string owner, string password, int products, int terminals, CancellationToken ct)
    {
        using var api = new PosApi(server);
        var status = (await api.GetAsync("api/v1/setup/status", ct))!;
        if (!status["isCompleted"]!.GetValue<bool>())
        {
            Console.WriteLine("Configuración inicial de la tienda de prueba…");
            await api.PostAsync("api/v1/setup", SetupBody(owner, password), ct);
            status = (await api.GetAsync("api/v1/setup/status", ct))!;
        }

        await api.LoginAsync(owner, password, ct);
        var edition = status["edition"]!.GetValue<string>();
        var branchId = status["branchId"]!.GetValue<Guid>();
        if (edition != "MULTI" && terminals > 1)
        {
            Console.WriteLine("Edición Caja Única: se usa una sola caja.");
            terminals = 1;
        }

        var methods = (await api.GetAsync("api/v1/cash/payment-methods", ct))!.AsArray();
        var cash = Id(methods.First(m => m!["code"]!.GetValue<string>() == "EFECTIVO")!);
        var debit = Id(methods.First(m => m!["code"]!.GetValue<string>() == "DEBITO")!);
        var denominations = (await api.GetAsync("api/v1/cash/denominations", ct))!.AsArray()
            .Select(d => new Denomination(Id(d!), d!["value"]!.GetValue<decimal>())).OrderByDescending(d => d.Value).ToList();
        var bill = denominations.First(d => d.Value == 50_000m);

        // Productos (en paralelo, 8 a la vez).
        var category = Id((await api.PostAsync("api/v1/catalog/categories", new { name = $"Carga {DateTime.Now:yyyyMMddHHmm}", sortOrder = 0 }, ct))!);
        var created = new LoadProduct[products];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, products), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (i, token) =>
        {
            var name = $"{Words[i % Words.Length]} prueba {i + 1}";
            var barcode = Ean13(i + 1);
            var node = (await api.PostAsync("api/v1/catalog/products", new
            {
                product = new
                {
                    sku = $"CARGA-{i + 1:D6}", name, shortName = (string?)null, description = (string?)null, categoryId = category, brandId = (Guid?)null,
                    baseUnitCode = "UND", saleMode = "Unit", productType = "Stockable", isSoldByScale = false, allowsDecimalQuantity = false,
                    allowsOpenPrice = false, tracksLots = false, tracksExpiry = false, pluCode = (string?)null, netContent = (decimal?)null,
                    netContentUnit = (string?)null,
                },
                barcode,
                generateBarcode = false,
                taxes = (object?)null,
                price = 1_000m + (i * 37 % 480 * 100m),
            }, token))!;
            created[i] = new LoadProduct(Id(node), barcode, name);
            if (Interlocked.Increment(ref done) % 1000 == 0)
            {
                Console.WriteLine($"  {done} productos");
            }
        });
        Console.WriteLine($"{products} productos creados.");

        // Cajas: la del asistente y las demás en la misma bodega.
        var terminalList = (await api.GetAsync($"api/v1/organization/terminals?branchId={branchId}", ct))!.AsArray();
        var warehouse = terminalList[0]!["warehouseId"]!.GetValue<Guid>();
        for (var n = terminalList.Count + 1; n <= terminals; n++)
        {
            await api.PostAsync($"api/v1/organization/branches/{branchId}/terminals", new { code = $"C{n:D2}", name = $"Caja {n}", warehouseId = warehouse }, ct);
        }

        terminalList = (await api.GetAsync($"api/v1/organization/terminals?branchId={branchId}", ct))!.AsArray();

        // Existencias iniciales: los más vendidos (ley de Zipf) con más unidades.
        foreach (var chunk in created.Select((p, rank) => (p, rank)).Chunk(5000))
        {
            var csv = new StringBuilder("producto;cantidad;costo\n");
            foreach (var (product, rank) in chunk)
            {
                csv.Append(CultureInfo.InvariantCulture, $"CARGA-{rank + 1:D6};{Math.Max(2_000, 400_000 / (rank + 1))};{600 + (rank * 37 % 480 * 60)}\n");
            }

            var draft = (await api.UploadAsync($"api/v1/inventory/adjustments/initial-balance/import?warehouseId={warehouse}", "saldo.csv",
                Encoding.UTF8.GetBytes(csv.ToString()), ct))!;
            await api.PostAsync($"api/v1/inventory/adjustments/{Id(draft)}/post", null, ct);
        }

        Console.WriteLine("Existencias iniciales cargadas.");

        // Cajeros y equipos emparejados.
        var cashierRole = Id((await api.GetAsync("api/v1/identity/roles", ct))!.AsArray().First(r => r!["code"]!.GetValue<string>() == "CASHIER")!);
        var cashiers = new List<LoadCashier>();
        var n2 = 0;
        foreach (var terminal in terminalList.Take(terminals))
        {
            n2++;
            var posCode = (600 + n2).ToString(CultureInfo.InvariantCulture);
            var pin = (5_000 + (n2 * 731 % 4_000)).ToString(CultureInfo.InvariantCulture);
            try
            {
                await api.PostAsync("api/v1/identity/users", new
                {
                    username = $"cajero.carga{n2}", displayName = $"Cajero de carga {n2}", password = "Clave-Carga-2026", posCode, pin,
                    roles = new[] { new { roleId = cashierRole, branchId = (Guid?)null } },
                }, ct);
            }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
            {
                // Ya existe de una preparación anterior.
            }

            string? deviceId = null;
            string? secret = null;
            if (edition == "MULTI")
            {
                var code = (await api.PostAsync("api/v1/devices/pairing-codes", new { kind = "Terminal", posTerminalId = Id(terminal!) }, ct))!;
                using var device = new PosApi(server);
                var paired = (await device.PostAsync("api/v1/devices/pair", new
                {
                    code = code["code"]!.GetValue<string>(), hostname = $"CARGA-{n2:D2}", machineFingerprintHash = Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"carga-{n2}-{Guid.NewGuid()}"))),
                    osVersion = "Prueba de carga", appVersion = "1.0.0",
                }, ct))!;
                deviceId = paired["deviceId"]!.GetValue<string>();
                secret = paired["deviceSecret"]!.GetValue<string>();
            }

            cashiers.Add(new LoadCashier(posCode, pin, terminal!["code"]!.GetValue<string>(), deviceId, secret));
        }

        Console.WriteLine($"{cashiers.Count} cajas con su cajero{(edition == "MULTI" ? " y su equipo emparejado" : string.Empty)}.");
        return new LoadStore(server.ToString(), edition, cash, debit, bill.Id, bill.Value, denominations, created, cashiers);
    }

    public static object SetupBody(string owner, string password) => new
    {
        company = new
        {
            legalName = "Supermercado de Carga SAS", tradeName = "Carga", personType = "Legal", identificationType = "NIT", identificationNumber = "900123456",
            checkDigit = "8", taxRegime = "48", fiscalResponsibilities = new[] { "R-99-PN" }, municipalityCode = "05001", address = "Calle 1 # 2-3",
            phone = (string?)null, email = (string?)null,
        },
        branch = new { code = "S01", name = "Principal", municipalityCode = "05001", address = "Calle 1 # 2-3" },
        owner = new { username = owner, displayName = "Propietario de carga", password, posCode = "100", pin = "4826" },
        terminal = new { code = "C01", name = "Caja 1" },
    };

    /// <summary>EAN-13 con prefijo 20 (uso interno) y dígito de control.</summary>
    public static string Ean13(int number)
    {
        var body = string.Create(CultureInfo.InvariantCulture, $"20{number:D10}");
        var sum = body.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return body + ((10 - (sum % 10)) % 10).ToString(CultureInfo.InvariantCulture);
    }

    public static async Task SaveAsync(LoadStore store, string path, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(store, PosApi.Json), ct);

    public static async Task<LoadStore> LoadAsync(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<LoadStore>(await File.ReadAllTextAsync(path, ct), PosApi.Json)
        ?? throw new InvalidOperationException($"{path} no es un archivo de carga válido.");

    private static Guid Id(JsonNode node) => node["id"]!.GetValue<Guid>();
}
