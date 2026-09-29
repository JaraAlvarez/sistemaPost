using System.Net;
using System.Net.Http.Json;
using System.Text;
using Npgsql;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Organization.Contracts;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase4;

/// <summary>Atajos de la Fase 4: tienda configurada, Propietario autenticado y catálogo base.</summary>
public sealed class CatalogScenario
{
    private CatalogScenario(PosServerFactory factory, HttpClient owner, SetupResultDto setup)
    {
        Factory = factory;
        Owner = owner;
        Setup = setup;
    }

    public PosServerFactory Factory { get; }

    public HttpClient Owner { get; }

    public SetupResultDto Setup { get; }

    public static async Task<CatalogScenario> CreateAsync(PosServerFactory factory)
    {
        var client = factory.CreateClient();
        var setup = await SetupAsync(client);
        return new CatalogScenario(factory, client, setup);
    }

    public async Task<Guid> CategoryAsync(string name, Guid? parentId = null)
    {
        var response = await Owner.PostAsJsonAsync("/api/v1/catalog/categories", new { name, parentId, sortOrder = 0 }, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(Json, Ct))!.Id;
    }

    public async Task<ProductDetailDto> ProductAsync(
        string? sku, string name, Guid categoryId, string? barcode = null, decimal? price = null, string unit = "UND", string saleMode = "Unit",
        bool scale = false, string? plu = null, object[]? taxes = null, bool generateBarcode = false)
    {
        var response = await Owner.PostAsJsonAsync(
            "/api/v1/catalog/products",
            new
            {
                product = new
                {
                    sku, name, shortName = (string?)null, description = (string?)null, categoryId, brandId = (Guid?)null, baseUnitCode = unit, saleMode,
                    productType = "Stockable", isSoldByScale = scale, allowsDecimalQuantity = false, allowsOpenPrice = false, tracksLots = false,
                    tracksExpiry = false, pluCode = plu, netContent = (decimal?)null, netContentUnit = (string?)null,
                },
                barcode,
                generateBarcode,
                taxes,
                price,
            },
            Json,
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(Json, Ct))!;
    }

    public async Task<Guid> TaxIdAsync(string code) =>
        (await GetAsync<List<TaxDto>>(Owner, "/api/v1/catalog/taxes")).Single(t => t.Code == code).Id;

    public async Task<ScanResultDto> ScanAsync(string code) => await GetAsync<ScanResultDto>(Owner, $"/api/v1/catalog/scan/{Uri.EscapeDataString(code)}");

    /// <summary>Sube un archivo como multipart (campo "file").</summary>
    public async Task<HttpResponseMessage> UploadAsync(string url, byte[] content, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(fileName.EndsWith(".csv", StringComparison.Ordinal) ? "text/csv" : "application/octet-stream");
        form.Add(file, "file", fileName);
        return await Owner.PostAsync(url, form, Ct);
    }

    public static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Etiqueta EAN-13 con su dígito de control.</summary>
    public static string Ean13(string twelveDigits)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += (twelveDigits[11 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        }

        return twelveDigits + (char)('0' + ((10 - (sum % 10)) % 10));
    }
}
