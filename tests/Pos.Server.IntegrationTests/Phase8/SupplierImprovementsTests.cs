using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Purchasing.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Server.IntegrationTests.Phase5;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase8;

/// <summary>
/// Criterios de aceptación del bloque 8.4 (docs/fases/fase-08-propuesta.md §5.5, §11 y §14): cuentas bancarias con
/// verificación (RN-PUR-09), resumen y costos por producto, vencimientos, agenda y retenciones sugeridas (RN-PUR-10).
/// </summary>
public class SupplierImprovementsTests
{
    private static object BankAccount(string number, bool isPrimary = false, bool isActive = true) => new
    {
        bankCode = "1007", accountType = "Savings", accountNumber = number, holderName = "Distribuidora Láctea SAS", holderIdentificationType = "NIT",
        holderIdentificationNumber = "800197268", isPrimary, isActive,
    };

    [Fact]
    public async Task Cuenta_nueva_queda_por_verificar_el_pago_advierte_y_la_verifica_otro_usuario()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var owner = scenario.Owner;
        var security = SecurityScenario.ForExisting(factory, owner, scenario.Catalog.Setup);
        var adminId = await security.CreateUserAsync("admin2", "ADMIN");
        await security.CreateUserAsync("compras", "PURCHASING");
        var admin = await security.LocalClientAsync("admin2");
        var buyer = await security.LocalClientAsync("compras");
        var accounts = $"/api/v1/purchasing/suppliers/{scenario.SupplierId}/bank-accounts";

        (await GetAsync<List<BankDto>>(owner, "/api/v1/purchasing/banks")).ShouldContain(b => b.Code == "1007" && b.Name == "Bancolombia");

        // Compras NO registra ni verifica cuentas (permiso sensible solo de propietario y administrador); sí las consulta.
        (await buyer.PostAsJsonAsync(accounts, BankAccount("123456789"), Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var account = await scenario.SendAsync<SupplierBankAccountDto>(accounts, BankAccount("123-456-789"), HttpStatusCode.Created);
        account.Status.ShouldBe("PENDING_VERIFICATION");
        account.AccountNumber.ShouldBe("123456789");
        account.MaskedNumber.ShouldBe("****6789");
        account.IsPrimary.ShouldBeTrue(); // la primera cuenta activa es la principal
        account.BankName.ShouldBe("Bancolombia");
        (await GetAsync<List<SupplierBankAccountDto>>(buyer, accounts)).Single().Id.ShouldBe(account.Id);
        (await buyer.PostAsync($"{accounts}/{account.Id}/verify", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await owner.PostAsJsonAsync(accounts, BankAccount("123456789"), Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "PURCHASING.BANK_ACCOUNT_DUPLICATED");
        await owner.PostAsJsonAsync(accounts, new { bankCode = "0000", accountType = "Savings", accountNumber = "99999", holderName = "X",
                holderIdentificationType = "CC", holderIdentificationNumber = "1" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.BANK_NOT_FOUND");

        // Quien la registró no puede verificarla.
        await owner.PostAsync($"{accounts}/{account.Id}/verify", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "PURCHASING.BANK_ACCOUNT_SAME_USER");

        // El pago por transferencia se registra, con la advertencia (no bloquea); en efectivo no aplica.
        var purchase = await scenario.BuyAsync("FE-801", new { productId = scenario.Rice, quantity = 100, unitCost = 2_000 });
        var payable = (await GetAsync<List<PayableDto>>(owner, $"/api/v1/purchasing/payables?supplierId={scenario.SupplierId}")).Single(p => p.PurchaseId == purchase.Id);
        var warned = await scenario.SendAsync<PaymentDto>("/api/v1/purchasing/payments", new
        {
            supplierId = scenario.SupplierId, paymentMethodId = scenario.Transfer, reference = "TRF-801",
            allocations = new[] { new { accountId = payable.Id, amount = 50_000m } },
        }, HttpStatusCode.Created);
        warned.Warnings.ShouldNotBeNull().Single().Code.ShouldBe("PURCHASING.BANK_ACCOUNT_UNVERIFIED");
        var cash = await scenario.SendAsync<PaymentDto>("/api/v1/purchasing/payments", new
        {
            supplierId = scenario.SupplierId, paymentMethodId = scenario.Cash, allocations = new[] { new { accountId = payable.Id, amount = 10_000m } },
        }, HttpStatusCode.Created);
        cash.Warnings.ShouldNotBeNull().ShouldBeEmpty();

        // Otro administrador la verifica; el siguiente pago ya no advierte.
        var verified = await PostAsync<SupplierBankAccountDto>(admin, $"{accounts}/{account.Id}/verify", new { });
        verified.Status.ShouldBe("VERIFIED");
        verified.VerifiedBy.ShouldBe(adminId);
        await admin.PostAsync($"{accounts}/{account.Id}/verify", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "PURCHASING.BANK_ACCOUNT_NOT_PENDING");
        (await scenario.SendAsync<PaymentDto>("/api/v1/purchasing/payments", new
        {
            supplierId = scenario.SupplierId, paymentMethodId = scenario.Transfer, reference = "TRF-802",
            allocations = new[] { new { accountId = payable.Id, amount = 10_000m } },
        }, HttpStatusCode.Created)).Warnings.ShouldNotBeNull().ShouldBeEmpty();

        // Cambiar el número la deja otra vez por verificar; una cuenta nueva marcada principal le quita la marca a la anterior.
        var changed = await PutAsync<SupplierBankAccountDto>(owner, $"{accounts}/{account.Id}", BankAccount("987654321", isPrimary: true));
        changed.Status.ShouldBe("PENDING_VERIFICATION");
        changed.VerifiedBy.ShouldBeNull();
        var second = await scenario.SendAsync<SupplierBankAccountDto>(accounts, BankAccount("5550001234", isPrimary: true), HttpStatusCode.Created);
        second.IsPrimary.ShouldBeTrue();
        (await GetAsync<List<SupplierBankAccountDto>>(owner, accounts)).Count(a => a.IsPrimary).ShouldBe(1);
        (await PutAsync<SupplierBankAccountDto>(owner, $"{accounts}/{second.Id}", BankAccount("5550001234", isActive: false))).Status.ShouldBe("INACTIVE");

        // Auditoría crítica (registro, verificación, cambio, segunda cuenta e inactivación), sin el número completo de la cuenta.
        (await scenario.Catalog.ScalarAsync<long>(
                "SELECT count(*) FROM audit.audit_log WHERE action LIKE 'SUPPLIER_BANK_ACCOUNT_%' AND severity = 'CRITICAL'"))
            .ShouldBe(5);
        (await scenario.Catalog.ScalarAsync<long>(
                "SELECT count(*) FROM audit.audit_log WHERE action LIKE 'SUPPLIER_BANK_ACCOUNT_%' AND (coalesce(new_values::text, '') LIKE '%987654321%' OR coalesce(summary, '') LIKE '%987654321%')"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Resumen_del_proveedor_quien_vende_el_producto_historial_de_costos_y_vencimientos()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var owner = scenario.Owner;
        var today = PurchasingScenario.Today;

        // Tres compras: una vencida hace 10 días, una que vence en 5 días y otra a 30 días.
        var old = await PostAsync(scenario, "FE-901", today.AddDays(-40), today.AddDays(-10), 2_000m);
        var soon = await PostAsync(scenario, "FE-902", today, today.AddDays(5), 2_100m);
        var last = await PostAsync(scenario, "FE-903", today, today.AddDays(30), 2_200m);
        var back = await scenario.SendAsync<SupplierReturnDto>("/api/v1/purchasing/returns",
            new { purchaseId = last.Id, reason = "Empaque roto", lines = new[] { new { purchaseLineId = last.Lines[0].Id, baseQuantity = 5m } } },
            HttpStatusCode.Created);
        back = await scenario.SendAsync<SupplierReturnDto>($"/api/v1/purchasing/returns/{back.Id}/post");

        var summary = await GetAsync<SupplierSummaryDto>(owner, $"/api/v1/purchasing/suppliers/{scenario.SupplierId}/summary?from={today.AddDays(-60):yyyy-MM-dd}");
        summary.PurchaseCount.ShouldBe(3);
        summary.PurchasedTotal.ShouldBe(old.Total + soon.Total + last.Total);
        summary.LastPurchaseDate.ShouldBe(today);
        summary.ReturnCount.ShouldBe(1);
        summary.ReturnsTotal.ShouldBe(back.CreditTotal);
        summary.OverdueCount.ShouldBe(1);
        summary.OverdueBalance.ShouldBe(old.PayableTotal);
        summary.Balance.ShouldBe((await GetAsync<List<PayableDto>>(owner, $"/api/v1/purchasing/payables?supplierId={scenario.SupplierId}")).Sum(p => p.Balance));
        summary.ActiveProducts.ShouldBe(1);
        summary.PrimaryBankAccountStatus.ShouldBeNull();
        (await GetAsync<SupplierSummaryDto>(owner, $"/api/v1/purchasing/suppliers/{scenario.SupplierId}/summary")).PurchaseCount.ShouldBe(3);
        await owner.GetAsync($"/api/v1/purchasing/suppliers/{scenario.SupplierId}/summary?from={today:yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}", Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.INVALID_PERIOD");

        // ¿Quién me vende el arroz? y su historial de costos (más reciente primero).
        var sellers = await GetAsync<List<ProductSupplierDto>>(owner, $"/api/v1/purchasing/products/{scenario.Rice}/suppliers");
        sellers.Single().SupplierId.ShouldBe(scenario.SupplierId);
        sellers.Single().LastCost.ShouldBe(last.Lines[0].NetUnitCost);
        var history = await GetAsync<List<CostHistoryEntryDto>>(owner, $"/api/v1/purchasing/products/{scenario.Rice}/cost-history");
        history.Select(h => h.UnitCost).ShouldBe([2_200m, 2_100m, 2_000m], ignoreOrder: true);
        history[^1].SupplierInvoiceNumber.ShouldBe("FE-901");
        history.ShouldAllBe(h => h.SupplierName == "Distribuidora Láctea SAS");
        (await GetAsync<List<CostHistoryEntryDto>>(owner, $"/api/v1/purchasing/products/{scenario.Rice}/cost-history?from={today:yyyy-MM-dd}")).Count.ShouldBe(2);
        await owner.GetAsync($"/api/v1/purchasing/products/{Guid.CreateVersion7()}/suppliers", Ct)
            .ShouldFailWithAsync(HttpStatusCode.NotFound, "PURCHASING.PRODUCT_NOT_FOUND");

        // La cajera no ve costos ni cartera.
        var security = SecurityScenario.ForExisting(factory, owner, scenario.Catalog.Setup);
        await security.CreateUserAsync("cajera", "CASHIER");
        var cashier = await security.LocalClientAsync("cajera");
        (await cashier.GetAsync($"/api/v1/purchasing/products/{scenario.Rice}/cost-history", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/v1/purchasing/payables/due", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Vencimientos: la vencida y la que vence en 5 días (a 7 días); a 30 días entra la tercera.
        var due = await GetAsync<DuePayablesDto>(owner, "/api/v1/purchasing/payables/due?days=7");
        due.Overdue.Single().DocumentNumber.ShouldBe("FE-901");
        due.Overdue.Single().DaysOverdue.ShouldBe(10);
        due.DueSoon.Single().DocumentNumber.ShouldBe("FE-902");
        due.OverdueTotal.ShouldBe(old.PayableTotal);
        (await GetAsync<DuePayablesDto>(owner, "/api/v1/purchasing/payables/due?days=30")).DueSoon.Count.ShouldBe(2);
        (await GetAsync<DuePayablesDto>(owner, "/api/v1/purchasing/payables/due")).Days.ShouldBe(7);
        await owner.GetAsync("/api/v1/purchasing/payables/due?days=91", Ct).ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.INVALID_PERIOD");
    }

    [Fact]
    public async Task Agenda_pedido_minimo_y_retenciones_sugeridas_que_prellenan_la_compra()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var owner = scenario.Owner;
        var supplier = $"/api/v1/purchasing/suppliers/{scenario.SupplierId}";

        var schedule = await PutAsync<SupplierScheduleDto>(owner, $"{supplier}/schedule", new
        {
            minimumOrderAmount = 300_000m, orderCutoffNote = "Pedidos hasta el lunes a las 10 a. m.",
            entries = new object[]
            {
                new { dayOfWeek = 1, kind = "Order", notes = "Por WhatsApp" },
                new { dayOfWeek = 1, kind = "Visit", branchId = scenario.Catalog.Setup.BranchId },
                new { dayOfWeek = 3, kind = "Delivery" },
            },
        });
        schedule.MinimumOrderAmount.ShouldBe(300_000m);
        schedule.Entries.Select(e => (e.Kind, e.DayOfWeek)).ShouldBe([("VISIT", 1), ("ORDER", 1), ("DELIVERY", 3)]);
        schedule.Entries.Single(e => e.Kind == "DELIVERY").DayName.ShouldBe("miércoles");
        schedule.Entries.ShouldAllBe(e => e.NextDate >= PurchasingScenario.Today && e.NextDate < PurchasingScenario.Today.AddDays(7));

        // Reemplazo: se conserva el pedido del lunes (misma fila), se quitan la visita y la entrega y se agrega el jueves.
        var orderId = schedule.Entries.Single(e => e.Kind == "ORDER").Id;
        schedule = await PutAsync<SupplierScheduleDto>(owner, $"{supplier}/schedule", new
        {
            minimumOrderAmount = 350_000m, entries = new object[] { new { dayOfWeek = 1, kind = "Order" }, new { dayOfWeek = 4, kind = "Delivery" } },
        });
        schedule.Entries.Count.ShouldBe(2);
        schedule.Entries.Single(e => e.Kind == "ORDER").Id.ShouldBe(orderId);
        (await GetAsync<SupplierScheduleDto>(owner, $"{supplier}/schedule")).Entries.Count.ShouldBe(2);
        (await GetAsync<SupplierScheduleDto>(owner, $"{supplier}/schedule")).OrderCutoffNote.ShouldBeNull();
        await owner.PutAsJsonAsync($"{supplier}/schedule", new { entries = new object[] { new { dayOfWeek = 9, kind = "Order" } } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.INVALID_SCHEDULE");

        // Retenciones sugeridas: retefuente 2,5 % y reteICA 0,414 %.
        var defaults = await PutAsync<List<SupplierWithholdingDefaultDto>>(owner, $"{supplier}/withholdings", new
        {
            withholdings = new object[]
            {
                new { kind = "Retefuente", rate = 2.5m, concept = "Compras generales" },
                new { kind = "Reteica", rate = 0.414m },
            },
        });
        defaults.Select(d => d.Kind).ShouldBe(["RETEFUENTE", "RETEICA"]);
        (await GetAsync<List<SupplierWithholdingDefaultDto>>(owner, $"{supplier}/withholdings")).Count.ShouldBe(2);
        await owner.PutAsJsonAsync($"{supplier}/withholdings", new { withholdings = new object[] { new { kind = "Reteiva", rate = 0m } } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.INVALID_WITHHOLDING_DEFAULT");

        // Compra nueva sin retenciones: se pre-llenan sobre la base antes de impuestos; con retenciones explícitas, no.
        var draft = await scenario.SendAsync<PurchaseDto>("/api/v1/purchasing/purchases", Purchase("FE-950", null), HttpStatusCode.Created);
        var taxable = draft.Subtotal - draft.DiscountTotal;
        draft.Withholdings.Select(w => (w.Kind, w.Base, w.Rate)).ShouldBe([("RETEFUENTE", taxable, (decimal?)2.5m), ("RETEICA", taxable, (decimal?)0.414m)]);
        draft.Withholdings[0].Amount.ShouldBe(decimal.Round(taxable * 0.025m, 2, MidpointRounding.AwayFromZero));
        draft.PayableTotal.ShouldBe(draft.Total - draft.WithholdingTotal);
        draft.Status.ShouldBe("DRAFT");
        var none = await scenario.SendAsync<PurchaseDto>("/api/v1/purchasing/purchases", Purchase("FE-951", []), HttpStatusCode.Created);
        none.Withholdings.ShouldBeEmpty();

        // El usuario confirma (o ajusta) al editar el borrador; lo que contabiliza es lo que quedó en la compra.
        var edited = await PutAsync<PurchaseDto>(owner, $"/api/v1/purchasing/purchases/{draft.Id}", Purchase("FE-950", [
            new { kind = "Retefuente", @base = taxable, rate = 2.5m },
        ], draft.Total));
        edited.Withholdings.Single().Kind.ShouldBe("RETEFUENTE");
        (await scenario.PostAsync(edited.Id)).WithholdingTotal.ShouldBe(edited.Withholdings.Single().Amount);

        // Compras (rol PURCHASING) administra agenda y retenciones; la cajera no.
        var security = SecurityScenario.ForExisting(factory, owner, scenario.Catalog.Setup);
        await security.CreateUserAsync("cajera", "CASHIER");
        var cashier = await security.LocalClientAsync("cajera");
        (await cashier.PutAsJsonAsync($"{supplier}/withholdings", new { withholdings = Array.Empty<object>() }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.GetAsync($"{supplier}/schedule", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        object Purchase(string invoice, object[]? withholdings, decimal? invoiceTotal = null) => new
        {
            supplierId = scenario.SupplierId, warehouseId = scenario.Floor, supplierInvoiceNumber = invoice, invoiceDate = PurchasingScenario.Today,
            paymentMode = "Credit", invoiceTotal, proration = "Value", chargesTotal = 0,
            lines = new[] { new { productId = scenario.Rice, quantity = 100, unitCost = 2_000 } }, withholdings,
        };
    }

    /// <summary>Compra a crédito de 100 arroces con fechas de factura y vencimiento dadas, contabilizada.</summary>
    private static async Task<PurchaseDto> PostAsync(PurchasingScenario scenario, string invoice, DateOnly invoiceDate, DateOnly dueDate, decimal unitCost)
    {
        object Request(decimal? total) => new
        {
            supplierId = scenario.SupplierId, warehouseId = scenario.Floor, supplierInvoiceNumber = invoice, invoiceDate, dueDate, paymentMode = "Credit",
            invoiceTotal = total, proration = "Value", chargesTotal = 0, lines = new[] { new { productId = scenario.Rice, quantity = 100, unitCost } },
        };

        var draft = await scenario.SendAsync<PurchaseDto>("/api/v1/purchasing/purchases", Request(null), HttpStatusCode.Created);
        var ready = await PutAsync<PurchaseDto>(scenario.Owner, $"/api/v1/purchasing/purchases/{draft.Id}", Request(draft.Total));
        return await scenario.PostAsync(ready.Id);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    private static async Task<T> PutAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(url, body, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }
}
