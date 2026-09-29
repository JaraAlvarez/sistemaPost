using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Audit.Contracts;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Expenses.Contracts;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Server.IntegrationTests.Phase5;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase6;

/// <summary>Criterios de aceptación de la Fase 6 (propuesta §14): jornadas, movimientos, arqueo ciego, cierres, reporte Z y gastos.</summary>
public class CashApiTests
{
    private sealed record Shop(
        PosServerFactory Factory, PurchasingScenario Purchasing, SecurityScenario Security, HttpClient Cashier, HttpClient Supervisor, Guid SupervisorId,
        Guid Cash, Guid Bill50, Guid Bill10)
    {
        public HttpClient Owner => Purchasing.Owner;
    }

    private static async Task<Shop> CreateShopAsync(PosServerFactory factory)
    {
        var purchasing = await PurchasingScenario.CreateAsync(factory);
        var security = SecurityScenario.ForExisting(factory, purchasing.Owner, purchasing.Catalog.Setup);
        await security.CreateUserAsync("cajera", "CASHIER", posCode: "200", pin: "5937");
        var supervisorId = await security.CreateUserAsync("supervisor", "CASH_SUPERVISOR", posCode: "300", pin: "7152");
        var cashier = factory.CreateClient();
        await SecurityScenario.PosLoginAsync(cashier, "200", "5937");
        var supervisor = factory.CreateClient();
        await SecurityScenario.PosLoginAsync(supervisor, "300", "7152");
        var denominations = await GetAsync<List<DenominationDto>>(cashier, "/api/v1/cash/denominations");
        denominations.Count.ShouldBe(11);
        return new Shop(factory, purchasing, security, cashier, supervisor, supervisorId, purchasing.Cash,
            denominations.Single(d => d.Value == 50_000m).Id, denominations.Single(d => d.Value == 10_000m).Id);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { }, Json, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    private static Task<CashSessionDto> OpenAsync(HttpClient client, Shop shop, int bills50 = 4) =>
        PostAsync<CashSessionDto>(client, "/api/v1/cash/sessions", new
        {
            openingFloat = bills50 * 50_000m,
            openingCount = new[] { new { paymentMethodId = shop.Cash, denominationId = shop.Bill50, quantity = bills50 } },
        }, HttpStatusCode.Created);

    [Fact]
    public async Task Turno_completo_con_arqueo_ciego_retiro_autorizado_gasto_pago_y_reporte_Z_sellado()
    {
        await using var factory = new CashServerFactory();
        var shop = await CreateShopAsync(factory);
        var cashier = shop.Cashier;

        // Sin sesión de caja (backoffice) no se abre jornada (D6-09).
        await shop.Owner.PostAsJsonAsync("/api/v1/cash/sessions", new { openingFloat = 0 }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "CASH.TERMINAL_REQUIRED");

        var session = await OpenAsync(cashier, shop);
        session.Status.ShouldBe("OPEN");
        session.BlindCount.ShouldBeTrue();
        session.ExpectedHidden.ShouldBeTrue();
        session.Totals.Single().Expected.ShouldBeNull();
        (await GetAsync<CashSessionDto>(cashier, "/api/v1/cash/sessions/current")).Id.ShouldBe(session.Id);

        // Una sola jornada por caja: el supervisor en la misma caja no abre otra (RN-CSH-01).
        await shop.Supervisor.PostAsJsonAsync("/api/v1/cash/sessions", new { openingFloat = 0 }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "CASH.SESSION_ALREADY_OPEN");

        // Ingreso, gasto menor desde la caja y pago a proveedor desde la caja (en la misma transacción que su documento).
        var cashIn = await PostAsync<CashMovementResultDto>(cashier, $"/api/v1/cash/sessions/{session.Id}/cash-in",
            new { amount = 50_000m, reason = "Cambio traído del banco" });
        cashIn.ExpectedCash.ShouldBeNull();
        var categories = await GetAsync<List<ExpenseCategoryDto>>(cashier, "/api/v1/expenses/categories");
        var cleaning = categories.Single(c => c.Name == "Aseo y cafetería").Id;
        var expense = await PostAsync<ExpenseDto>(cashier, "/api/v1/expenses/from-cash", new
        {
            categoryId = cleaning, description = "Bolsas de basura y jabón", amount = 30_000m, taxAmount = 0m, paymentMethodId = shop.Cash,
            cashSessionId = session.Id,
        }, HttpStatusCode.Created);
        expense.CashSessionId.ShouldBe(session.Id);
        var purchase = await shop.Purchasing.BuyAsync("FE-60", new { productId = shop.Purchasing.Rice, quantity = 20, unitCost = 1_000 });
        var payable = (await GetAsync<List<PayableDto>>(shop.Owner, "/api/v1/purchasing/payables")).Single(p => p.PurchaseId == purchase.Id);
        var payment = await PostAsync<PaymentDto>(shop.Owner, "/api/v1/purchasing/payments", new
        {
            supplierId = shop.Purchasing.SupplierId, paymentMethodId = shop.Cash, cashSessionId = session.Id,
            allocations = new[] { new { accountId = payable.Id, amount = payable.Balance } },
        }, HttpStatusCode.Created);
        payment.CashSessionId.ShouldBe(session.Id);

        // Retiro: la cajera necesita la autorización de un supervisor (de un solo uso, queda en la auditoría).
        var withdrawalUrl = $"/api/v1/cash/sessions/{session.Id}/withdrawals";
        var denied = await cashier.PostAsJsonAsync(withdrawalUrl, new { amount = 100_000m, reason = "Sangría a la caja fuerte" }, Json, Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await SecurityScenario.ProblemAsync(denied);
        problem.GetProperty("code").GetString().ShouldBe("AUTH.AUTHORIZATION_REQUIRED");
        var grantResponse = await cashier.PostAsJsonAsync("/api/v1/auth/authorizations", new
        {
            supervisorCode = "300", supervisorPin = "7152", permissionCode = CashPermissions.MovementWithdraw,
            action = problem.GetProperty("action").GetString(), targetId = session.Id, targetType = "CashSession", reason = "Sangría",
        }, Json, Ct);
        grantResponse.StatusCode.ShouldBe(HttpStatusCode.Created, await grantResponse.Content.ReadAsStringAsync(Ct));
        var grant = (await grantResponse.Content.ReadFromJsonAsync<AuthorizationGrantDto>(Json, Ct))!;
        var withGrant = new HttpRequestMessage(HttpMethod.Post, withdrawalUrl) { Content = JsonContent.Create(new { amount = 100_000m, reason = "Sangría a la caja fuerte" }, options: Json) };
        withGrant.Headers.Add("X-Authorization-Grant", grant.GrantId.ToString());
        (await cashier.SendAsync(withGrant, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Un retiro no deja el efectivo esperado negativo (RN-CSH-06); la apertura sin venta queda registrada con motivo.
        await shop.Supervisor.PostAsJsonAsync(withdrawalUrl, new { amount = 10_000_000m, reason = "Retiro imposible" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.INSUFFICIENT_CASH");
        (await shop.Supervisor.PostAsJsonAsync($"/api/v1/cash/sessions/{session.Id}/drawer-openings", new { reason = "Cambio para un cliente" }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Reporte X: la cajera no lo ve (arqueo ciego); el supervisor sí, con lo esperado calculado de los movimientos.
        await cashier.GetAsync($"/api/v1/cash/sessions/{session.Id}/report-x", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "CASH.BLIND_COUNT");
        var expectedCash = 200_000m + 50_000m - 30_000m - payment.Amount - 100_000m;
        var x = await GetAsync<CashReportDto>(shop.Supervisor, $"/api/v1/cash/sessions/{session.Id}/report-x");
        x.Totals.Single(t => t.Code == "EFECTIVO").Expected.ShouldBe(expectedCash);
        x.Withdrawals.Single().AuthorizedByName.ShouldBe("Usuario supervisor");
        x.NoSaleOpenings.ShouldBe(1);

        // En producción la tienda ya lleva sellos; aquí se espera al primero (horizonte seguro de 2 s en las pruebas).
        for (var i = 0; i < 100 && await shop.Purchasing.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_seals") == 0; i++)
        {
            await Task.Delay(200, Ct);
        }

        // Cierre ciego con $10.000 de faltante: sin observación no se confirma; con ella queda pendiente de revisión.
        await PostAsync<CashSessionDto>(cashier, $"/api/v1/cash/sessions/{session.Id}/start-closing", null);
        await cashier.PostAsJsonAsync($"/api/v1/cash/sessions/{session.Id}/cash-in", new { amount = 1_000m, reason = "Ingreso tardío" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SESSION_NOT_OPEN");
        var count = new[] { new { paymentMethodId = shop.Cash, denominationId = (Guid?)null, quantity = (int?)null, amount = (decimal?)(expectedCash - 10_000m) } };
        await cashier.PostAsJsonAsync($"/api/v1/cash/sessions/{session.Id}/close", new { count }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CASH.DIFFERENCE_NOTE_REQUIRED");
        var z = await PostAsync<CashReportDto>(cashier, $"/api/v1/cash/sessions/{session.Id}/close", new { count, differenceNote = "Faltó un billete" });
        z.Kind.ShouldBe("Z");
        z.Difference.ShouldBe(-10_000m);
        z.CountedTotal.ShouldBe(expectedCash - 10_000m);
        z.SealNo.ShouldNotBeNull();
        z.Text.ShouldContain($"SELLO #{z.SealNo}");
        z.Text.Split('\n').ShouldAllBe(l => l.Length <= 42);

        // El sello impreso corresponde a la bitácora (D6-08); un código distinto no.
        var check = await GetAsync<AuditSealCheckDto>(shop.Owner, $"/api/v1/audit/seals/{z.SealNo}/check?code={z.SealCode}");
        check.CodeMatches.ShouldBeTrue();
        check.AuditIsValid.ShouldBeTrue();
        (await GetAsync<AuditSealCheckDto>(shop.Owner, $"/api/v1/audit/seals/{z.SealNo}/check?code=0000-0000-0000-0000")).CodeMatches.ShouldBeFalse();

        // Definitivo y en texto de 80 mm; queda pendiente de revisión del supervisor (no de la cajera).
        (await cashier.GetStringAsync($"/api/v1/cash/sessions/{session.Id}/report-z?format=text", Ct)).ShouldContain("REPORTE Z");
        (await GetAsync<List<CashSessionSummaryDto>>(shop.Supervisor, "/api/v1/cash/sessions?pendingReview=true")).Single().Id.ShouldBe(session.Id);
        (await cashier.PostAsJsonAsync($"/api/v1/cash/sessions/{session.Id}/review", new { note = "Revisado" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync<CashSessionDto>(shop.Supervisor, $"/api/v1/cash/sessions/{session.Id}/review", new { note = "Se descuenta al cajero" })).ReviewedAt.ShouldNotBeNull();
        var closed = await GetAsync<CashSessionDto>(cashier, $"/api/v1/cash/sessions/{session.Id}");
        closed.ExpectedHidden.ShouldBeFalse();
        closed.Movements.Select(m => m.MovementType).ShouldBe(
            ["OPENING_FLOAT", "CASH_IN", "EXPENSE", "SUPPLIER_PAYMENT", "CASH_OUT_WITHDRAWAL", "NO_SALE_DRAWER_OPEN"]);
        closed.Movements.Single(m => m.MovementType == "CASH_OUT_WITHDRAWAL").AuthorizedBy.ShouldBe(shop.SupervisorId);
        (await shop.Purchasing.Catalog.ScalarAsync<Guid>("SELECT authorized_by FROM audit.audit_log WHERE action = 'CASH_WITHDRAWAL'")).ShouldBe(shop.SupervisorId);

        // Un gasto de una jornada cerrada ya no se devuelve a esa caja (el cierre es definitivo).
        await shop.Owner.PostAsJsonAsync($"/api/v1/expenses/{expense.Id}/void", new { reason = "Registrado por error" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SESSION_NOT_OPEN");
    }

    [Fact]
    public async Task Cierre_por_supervisor_usuario_con_jornada_abierta_y_anulaciones_que_devuelven_a_la_caja()
    {
        await using var factory = new CashServerFactory();
        var shop = await CreateShopAsync(factory);
        var session = await OpenAsync(shop.Cashier, shop, bills50: 2);

        // RN-SEC-07: no se desactiva a la cajera mientras tenga la jornada abierta.
        var users = await GetAsync<List<UserDto>>(shop.Owner, "/api/v1/identity/users");
        var cashierId = users.Single(u => u.Username == "cajera").Id;
        await shop.Owner.PostAsync($"/api/v1/identity/users/{cashierId}/deactivate", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.USER_HAS_OPEN_SESSION");

        // Un gasto desde la caja anulado con la jornada abierta vuelve al cajón con una corrección.
        var category = (await GetAsync<List<ExpenseCategoryDto>>(shop.Owner, "/api/v1/expenses/categories")).Single(c => c.Name == "Papelería").Id;
        var expense = await PostAsync<ExpenseDto>(shop.Owner, "/api/v1/expenses", new
        {
            categoryId = category, description = "Rollos para la impresora", amount = 20_000m, taxAmount = 3_193.28m, paymentMethodId = shop.Cash,
            cashSessionId = session.Id,
        }, HttpStatusCode.Created);
        (await PostAsync<ExpenseDto>(shop.Owner, $"/api/v1/expenses/{expense.Id}/void", new { reason = "Lo pagó la administración" })).Status.ShouldBe("VOIDED");
        (await GetAsync<List<ExpenseDto>>(shop.Owner, $"/api/v1/expenses?cashSessionId={session.Id}")).Single().Status.ShouldBe("VOIDED");

        // Un gasto no puede sacar más efectivo del que hay.
        await shop.Owner.PostAsJsonAsync("/api/v1/expenses", new
            {
                categoryId = category, description = "Demasiado", amount = 5_000_000m, taxAmount = 0m, paymentMethodId = shop.Cash, cashSessionId = session.Id,
            }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.INSUFFICIENT_CASH");

        // La cajera se fue sin cerrar: el supervisor cierra con motivo y conteo; queda para revisión y como incidente crítico.
        await shop.Supervisor.PostAsJsonAsync($"/api/v1/cash/sessions/{session.Id}/supervisor-close", new { count = Array.Empty<object>(), reason = "no" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CASH.REASON_REQUIRED");
        var z = await PostAsync<CashReportDto>(shop.Supervisor, $"/api/v1/cash/sessions/{session.Id}/supervisor-close", new
        {
            reason = "La cajera salió por una emergencia",
            count = new[] { new { paymentMethodId = shop.Cash, denominationId = (Guid?)shop.Bill50, quantity = (int?)2, amount = (decimal?)null } },
        });
        z.ClosedBySupervisor.ShouldBeTrue();
        z.Difference.ShouldBe(0m);
        z.Text.ShouldContain("CIERRE POR SUPERVISOR");
        (await shop.Purchasing.Catalog.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'CASH_SESSION_CLOSED_BY_SUPERVISOR'"))
            .ShouldBe("CRITICAL");
        (await GetAsync<CashSessionDto>(shop.Supervisor, $"/api/v1/cash/sessions/{session.Id}")).ReviewRequired.ShouldBeTrue();
        (await shop.Owner.PostAsync($"/api/v1/identity/users/{cashierId}/deactivate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Con la caja libre, otra jornada; un pago de contado desde la caja anulado vuelve al cajón.
        var second = await OpenAsync(shop.Supervisor, shop, bills50: 6);
        second.ExpectedHidden.ShouldBeFalse();
        var draft = await shop.Purchasing.DraftAsync("FE-70", [new { productId = shop.Purchasing.Rice, quantity = 10, unitCost = 5_000 }], mode: "Cash", method: shop.Cash);
        var cashPurchase = await PostAsync<PurchaseDto>(shop.Owner, $"/api/v1/purchasing/purchases/{draft.Id}/post", new { cashSessionId = second.Id });
        var paid = (await GetAsync<List<PurchasingDocumentDto>>(shop.Owner, "/api/v1/purchasing/payments")).Single();
        (await PostAsync<PaymentDto>(shop.Owner, $"/api/v1/purchasing/payments/{paid.Id}/void", new { reason = "Se pagó por transferencia" })).Status.ShouldBe("VOIDED");
        var detail = await GetAsync<CashSessionDto>(shop.Supervisor, $"/api/v1/cash/sessions/{second.Id}");
        detail.Movements.Select(m => (m.MovementType, m.Direction)).ShouldBe([("OPENING_FLOAT", 1), ("SUPPLIER_PAYMENT", -1), ("CORRECTION", 1)]);
        detail.Totals.Single(t => t.Code == "EFECTIVO").Expected.ShouldBe(300_000m);
        cashPurchase.Status.ShouldBe("POSTED");

        // Corrección manual (supervisor) y la apertura con un conteo que no suma el fondo.
        (await PostAsync<CashMovementResultDto>(shop.Supervisor, $"/api/v1/cash/sessions/{second.Id}/corrections",
            new { amount = 2_000m, direction = -1, reason = "Error de la jornada anterior" })).ExpectedCash.ShouldBe(298_000m);
    }

    [Fact]
    public async Task Dos_aperturas_simultaneas_de_la_misma_caja_dejan_una_sola_jornada()
    {
        await using var factory = new CashServerFactory();
        var shop = await CreateShopAsync(factory);

        var results = await Task.WhenAll(
            shop.Cashier.PostAsJsonAsync("/api/v1/cash/sessions", new { openingFloat = 100_000m }, Json, Ct),
            shop.Supervisor.PostAsJsonAsync("/api/v1/cash/sessions", new { openingFloat = 100_000m }, Json, Ct));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        (await shop.Purchasing.Catalog.ScalarAsync<long>("SELECT count(*) FROM cash.cash_sessions")).ShouldBe(1);

        // El conteo de apertura debe sumar el fondo; la cajera no ve jornadas ajenas ni la lista de la sucursal.
        await shop.Cashier.PostAsJsonAsync("/api/v1/cash/sessions", new
            {
                openingFloat = 100_000m, openingCount = new[] { new { paymentMethodId = shop.Cash, denominationId = shop.Bill50, quantity = 1 } },
            }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "CASH.SESSION_ALREADY_OPEN");
        (await shop.Cashier.GetAsync("/api/v1/cash/sessions", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
