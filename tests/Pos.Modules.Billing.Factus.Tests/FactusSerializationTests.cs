using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Modules.Billing.Infrastructure.Factus;

namespace Pos.Modules.Billing.Factus.Tests;

/// <summary>El JSON enviado debe coincidir exactamente con el formato de Factus API v2 (nombres, orden, texto con dos decimales).</summary>
public class FactusSerializationTests
{
    private const string ExpectedBill = """
        {
          "reference_code": "7f1c2a9e-0b1d-4c55-9a51-3f0e2d8b6a10",
          "document": "01",
          "numbering_range_id": 8,
          "operation_type": "10",
          "send_email": false,
          "observation": "Venta C1-000045",
          "payment_details": [
            { "payment_form": "1", "payment_method_code": "10", "amount": "41600.00" },
            { "payment_form": "1", "payment_method_code": "49", "reference_code": "AUT-123456", "amount": "20000.00" }
          ],
          "cash_rounding_amount": "10.00",
          "establishment": {
            "name": "Supermercado Demo Sede Centro",
            "address": "Calle 10 # 5-20",
            "phone_number": "6011234567",
            "email": "centro@super.test",
            "municipality_code": "11001"
          },
          "customer": {
            "identification_document_code": "13",
            "identification": "222222222222",
            "legal_organization_code": "2",
            "tribute_code": "ZZ",
            "names": "Consumidor final",
            "country_code": "CO"
          },
          "items": [
            {
              "code_reference": "7702001", "name": "Gaseosa 1,5 L", "quantity": "2.00", "discount_rate": "0.00", "price": "4201.68",
              "unit_measure_code": "94", "standard_code": "999", "taxes": [ { "code": "01", "rate": "19.00" } ]
            },
            {
              "code_reference": "7702002", "name": "Café molido 250 g", "quantity": "1.00", "discount_rate": "0.00", "price": "9523.81",
              "unit_measure_code": "94", "standard_code": "999", "taxes": [ { "code": "01", "rate": "5.00" } ]
            },
            {
              "code_reference": "7702003", "name": "Leche entera 1 L", "quantity": "3.00", "discount_rate": "10.00", "price": "3500.00",
              "unit_measure_code": "94", "standard_code": "999", "taxes": [ { "code": "01", "rate": "0.00" } ]
            },
            {
              "code_reference": "7702004", "name": "Huevos AA x 30", "quantity": "1.00", "discount_rate": "0.00", "price": "12000.00",
              "unit_measure_code": "94", "standard_code": "999", "taxes": [ { "code": "01", "rate": "0.00", "is_excluded": true } ]
            },
            {
              "code_reference": "7702005", "name": "Hamburguesa preparada", "quantity": "1.00", "discount_rate": "0.00", "price": "18518.52",
              "unit_measure_code": "94", "standard_code": "999", "taxes": [ { "code": "04", "rate": "8.00" } ]
            },
            {
              "code_reference": "BOLSA", "name": "Bolsa plástica (impuesto al consumo)", "quantity": "2.00", "discount_rate": "0.00", "price": "70.00",
              "unit_measure_code": "94", "standard_code": "999", "taxes": [ { "code": "01", "rate": "0.00", "is_excluded": true } ]
            }
          ]
        }
        """;

    [Fact]
    public void Factura_con_IVA_19_5_0_excluido_INC_y_bolsa_se_serializa_exactamente()
    {
        var actual = FactusJson.Serialize(FactusSamples.Bill());

        // Normaliza el esperado (sin espacios) conservando orden y tipos: la comparación es de texto exacto.
        var expected = JsonNode.Parse(ExpectedBill)!.ToJsonString(FactusJson.Options);
        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task El_simulado_calcula_los_totales_como_Factus_con_redondeo_bancario_por_impuesto()
    {
        await using var harness = FactusHarness.Create();

        var result = await harness.Api.CreateBillAsync(FactusSamples.Bill(), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FactusOutcome.Accepted);
        result.Document!.Total.ShouldBe(61_590m);
        var totals = harness.Server.Documents.Single().Totals;
        (totals.GrossAmount, totals.DiscountAmount, totals.TaxableAmount, totals.TaxAmount, totals.Total)
            .ShouldBe((59_085.69m, 1_050m, 45_895.69m, 3_554.31m, 61_590m));

        // Pagado 61.600 = total 61.590 + redondeo del efectivo 10.
        var sent = FactusSamples.Bill();
        (sent.PaymentDetails.Sum(p => p.Amount) - sent.CashRoundingAmount!.Value).ShouldBe(totals.Total);
    }

    [Fact]
    public void Un_valor_con_mas_de_dos_decimales_no_se_redondea_en_silencio()
    {
        var bill = FactusSamples.Bill() with
        {
            Items = [FactusSamples.Bill().Items[0] with { Quantity = 0.345m }],
        };

        Should.Throw<JsonException>(() => FactusJson.Serialize(bill)).Message.ShouldContain("dos decimales");
    }

    [Fact]
    public void Los_opcionales_nulos_se_omiten_y_los_codigos_v2_usan_nombres_snake_case()
    {
        var json = JsonNode.Parse(FactusJson.Serialize(FactusSamples.SupportDocument()))!.AsObject();

        json.ContainsKey("cash_rounding_amount").ShouldBeFalse();
        json.ContainsKey("establishment").ShouldBeFalse();
        json["provider"]!["identification_document_code"]!.GetValue<string>().ShouldBe("13");
        json["provider"]!.AsObject().ContainsKey("dv").ShouldBeFalse();
        json["items"]![0]!["taxes"]![0]!["is_excluded"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Las_opciones_nunca_muestran_credenciales()
    {
        var options = FactusHarness.Options(new Uri("https://api-sandbox.factus.com.co/"));

        options.ToString().ShouldNotContain(options.Password);
        options.ToString().ShouldNotContain(options.ClientSecret);
        new FactusOptions { ClientId = "a", ClientSecret = "b", Username = "c", Password = "d", Environment = FactusEnvironment.Production }
            .EffectiveBaseUrl.ShouldBe(FactusOptions.ProductionUrl);
    }
}
