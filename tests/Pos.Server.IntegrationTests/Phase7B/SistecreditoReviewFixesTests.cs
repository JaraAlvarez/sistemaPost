using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;
using static Pos.Server.IntegrationTests.Phase7B.SistecreditoTests;

namespace Pos.Server.IntegrationTests.Phase7B;

/// <summary>
/// Correcciones de la revisión de código de Sistecrédito (A1, M1, M3, M4, M5, B4, B5) de punta a punta, con Sistecrédito simulado: nunca
/// se cobra dos veces, un cobro en revisión se puede usar, anular deja el cobro en revisión, la revisión automática no se atasca, la
/// llamada a Sistecrédito va por fuera de la transacción, un aprobado se vuelve a consultar antes de usarlo y los centavos se avisan.
/// </summary>
public class SistecreditoReviewFixesTests
{
    private static readonly object Settings = new
    {
        subscriptionKey = new string('k', 32),
        applicationKey = new string('a', 24),
        applicationToken = new string('t', 24),
        environment = "STAGING",
        enabled = true,
    };

    [Fact]
    public async Task Pago_mixto_aprobado_no_se_cobra_otra_vez_al_reabrir_el_cobro()
    {
        // A1: Sistecrédito aprueba 6.000 de 9.000, la cajera cierra y vuelve a abrir el cobro.
        var (factory, shop, method) = await ShopAsync();
        await using var _ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 3);
        sale.AmountDue.ShouldBe(9_000m);
        var part = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, 6_000m));
        factory.Gateway.SetStatus(part.TransactionId!, "Approved");
        (await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{part.Id}/check")).Usable.ShouldBeTrue();
        var created = factory.Gateway.Created.Count;

        // Al reabrir, la caja recupera el aprobado sin usar y lo pone como pago.
        var restored = await GetAsync<List<SistecreditoPaymentDto>>(shop.Cashier, $"/api/v1/cash/sistecredito/sales/{sale.Id}/open");
        restored.Single().Id.ShouldBe(part.Id);
        restored.Single().Amount.ShouldBe(6_000m);

        // Si igual pide el total, el servidor no crea otro cobro: lo aprobado sin usar cuenta contra lo que falta.
        var detail = await ProblemDetailAsync(
            await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/cash/sistecredito/payments", Start(sale.Id, 9_000m)), "CASH.SISTECREDITO_ALREADY_APPROVED");
        detail.ShouldContain("3.000");
        (await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, 6_000m))).Id.ShouldBe(part.Id);
        factory.Gateway.Created.Count.ShouldBe(created); // ninguna transacción nueva en Sistecrédito

        // N6: si la caja ya tiene ese cobro como pago, pedir otro por el mismo valor crea uno nuevo (no devuelve el mismo).
        var another = await PostAsync<SistecreditoPaymentDto>(
            shop.Cashier, "/api/v1/cash/sistecredito/payments",
            new { saleId = sale.Id, amount = 3_000m, documentType = "CC", documentNumber = "1020304050", heldTransactionIds = new[] { part.TransactionId } });
        another.Id.ShouldNotBe(part.Id);
        factory.Gateway.SetStatus(another.TransactionId!, "Approved");
        await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{another.Id}/check");

        await shop.CompleteAsync(sale.Id, [Pay(method, 6_000m, part.TransactionId), Pay(method, 3_000m, another.TransactionId)]);
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{part.Id}")).CompletedSaleId.ShouldBe(sale.Id);
    }

    [Fact]
    public async Task No_se_completa_la_venta_sin_el_credito_aprobado_y_los_cobros_abiertos_solo_los_ve_su_caja()
    {
        // N1 (defensa en el servidor): con 6.000 aprobados, cobrar los 9.000 en efectivo haría pagar dos veces al cliente.
        var (factory, shop, method) = await ShopAsync();
        await using var _ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 3);
        var part = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, 6_000m));
        factory.Gateway.SetStatus(part.TransactionId!, "Approved");
        await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{part.Id}/check");
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/complete", new { payments = new[] { Pay(shop.Cash, 9_000m) } })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_APPROVED_NOT_INCLUDED");

        // N9: los cobros abiertos (con transacción y enlace de pago) solo los ve la caja de la venta.
        await RawAsync(shop.Owner, HttpMethod.Get, $"/api/v1/cash/sistecredito/sales/{sale.Id}/open")
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_SALE_NOT_OPEN");

        // N1: un cobro que seguía pendiente al cerrar el panel también vuelve (para mostrarlo y seguir consultándolo).
        var pending = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, 3_000m));
        var open = await GetAsync<List<SistecreditoPaymentDto>>(shop.Cashier, $"/api/v1/cash/sistecredito/sales/{sale.Id}/open");
        open.Select(o => o.Status).Order(StringComparer.Ordinal).ShouldBe(["APPROVED", "PENDING"]);
        open.Single(o => o.Status == "PENDING").Id.ShouldBe(pending.Id);

        // B4 sin respuesta de Sistecrédito: se usa el último estado conocido y la venta se completa.
        factory.Gateway.SetStatus(pending.TransactionId!, "Approved");
        await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{pending.Id}/check");
        factory.Gateway.Offline = true;
        var receipt = await shop.CompleteAsync(sale.Id, [Pay(method, 6_000m, part.TransactionId), Pay(method, 3_000m, pending.TransactionId)]);
        receipt.Sale.Status.ShouldBe("COMPLETED");
        factory.Gateway.Offline = false;
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{part.Id}")).CompletedSaleId.ShouldBe(sale.Id);
    }

    [Fact]
    public async Task Un_cobro_en_revision_aprobado_completa_la_venta_y_uno_que_ya_no_esta_aprobado_no()
    {
        var (factory, shop, method) = await ShopAsync();
        await using var _ = factory;

        // M1: aprobado y olvidado 20 minutos → "Requiere revisión"; generar de nuevo lo devuelve y completa la venta.
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 2);
        var payment = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        factory.Gateway.SetStatus(payment.TransactionId!, "Approved");
        await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}/check");
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddMinutes(20));
        (await GetAsync<MeDto>(shop.Owner, "/api/v1/auth/me")).SistecreditoReviews.ShouldBe(1);

        var again = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        again.Id.ShouldBe(payment.Id);
        again.Status.ShouldBe("REVIEW_REQUIRED");
        again.Usable.ShouldBeTrue();
        await shop.CompleteAsync(sale.Id, [Pay(method, sale.AmountDue, payment.TransactionId)]);
        var used = await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}");
        used.Status.ShouldBe("APPROVED");
        used.CompletedSaleId.ShouldBe(sale.Id);
        (await GetAsync<MeDto>(shop.Owner, "/api/v1/auth/me")).SistecreditoReviews.ShouldBe(0);

        // B4: aprobado, pero antes de completar se reversó en Sistecrédito → la venta no se completa con él.
        var second = await shop.StartAsync();
        second = await shop.AddAsync(second.Id, shop.Rice, 1);
        var reversed = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(second.Id, second.AmountDue));
        factory.Gateway.SetStatus(reversed.TransactionId!, "Approved");
        (await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{reversed.Id}/check")).Status.ShouldBe("APPROVED");
        factory.Gateway.SetStatus(reversed.TransactionId!, "Cancelled");
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{second.Id}/complete", new { payments = new[] { Pay(method, second.AmountDue, reversed.TransactionId) } })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_NO_LONGER_APPROVED");

        // La nueva consulta va ANTES de la transacción de la venta: el "Requiere revisión" quedó guardado aunque la venta falló.
        var review = await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{reversed.Id}");
        review.Status.ShouldBe("REVIEW_REQUIRED");
        review.Usable.ShouldBeFalse();
        review.Message!.ShouldContain("ya no reporta aprobado");
        (await GetAsync<List<SistecreditoPaymentDto>>(shop.Cashier, $"/api/v1/cash/sistecredito/sales/{second.Id}/open")).ShouldBeEmpty();
        await shop.CompleteAsync(second.Id, [Pay(shop.Cash, second.AmountDue)]);
    }

    [Fact]
    public async Task Anular_una_venta_pagada_con_Sistecredito_deja_el_cobro_en_revision()
    {
        // M3.
        var (factory, shop, method) = await ShopAsync();
        await using var _ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 2);
        var payment = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        factory.Gateway.SetStatus(payment.TransactionId!, "Approved");
        await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}/check");
        await shop.CompleteAsync(sale.Id, [Pay(method, sale.AmountDue, payment.TransactionId)]);

        await shop.AuthorizedAsync<SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{sale.Id}/void", new { reason = "Cliente desistió" });

        var voided = await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}");
        voided.Status.ShouldBe("REVIEW_REQUIRED");
        voided.Message.ShouldBe("Venta anulada: reverse el pago en el portal de Sistecrédito");
        voided.Usable.ShouldBeFalse();
        (await GetAsync<MeDto>(shop.Owner, "/api/v1/auth/me")).SistecreditoReviews.ShouldBe(1);
        (await shop.Catalog.ScalarAsync<long>(
                "SELECT count(*) FROM audit.audit_log WHERE action = 'SISTECREDITO_REVIEW_REQUIRED' AND summary LIKE '%anulada%portal de Sistecrédito%'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task La_revision_automatica_vence_los_viejos_atiende_el_708_y_consulta_con_el_ambiente_de_cada_cobro()
    {
        // M4.
        var (factory, shop, _) = await ShopAsync();
        await using var __ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);

        // Pendiente de más de 24 h: última consulta (sigue pendiente) y se da por vencido.
        var old = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddHours(25));
        factory.Gateway.Queries.ShouldContain(q => q.TransactionId == old.TransactionId);
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{old.Id}")).Status.ShouldBe("EXPIRED");

        // Sistecrédito no lo encuentra (708): recién creado se espera; después vence.
        var missing = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        factory.Gateway.NotFound[missing.TransactionId!] = true;
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddMinutes(2));
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{missing.Id}")).Status.ShouldBe("PENDING");
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddMinutes(30));
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{missing.Id}")).Status.ShouldBe("EXPIRED");

        // Aprobado que Sistecrédito ya no encuentra: a revisión (no vence: la plata pudo moverse).
        var approved = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        factory.Gateway.SetStatus(approved.TransactionId!, "Approved");
        await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{approved.Id}/check");
        factory.Gateway.NotFound[approved.TransactionId!] = true;
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddMinutes(2));
        var review = await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{approved.Id}");
        review.Status.ShouldBe("REVIEW_REQUIRED");
        review.Usable.ShouldBeFalse();

        // Cobro creado en pruebas (Staging); luego la tienda pasa a producción: se sigue consultando con su propio ambiente.
        var staging = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        staging.Environment.ShouldBe("STAGING");
        await SendAsync<SistecreditoSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/cash/sistecredito/settings", new { environment = "PRODUCTION", enabled = true });
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddMinutes(2));
        factory.Gateway.Queries.Where(q => q.TransactionId == staging.TransactionId).Select(q => q.Environment).Distinct()
            .ShouldBe([SistecreditoEnvironment.Staging]);
    }

    [Fact]
    public async Task El_cobro_se_guarda_antes_de_llamar_a_Sistecredito_y_un_fallo_no_borra_el_registro()
    {
        // M5: la llamada a Sistecrédito va por fuera de la transacción: en ese momento el cobro ya está guardado (y confirmado).
        var (factory, shop, _) = await ShopAsync();
        await using var _ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);
        string? seen = null;
        factory.Gateway.BeforeCreate = async request =>
        {
            await using var connection = new NpgsqlConnection(factory.ConnectionString);
            seen = await connection.ExecuteScalarAsync<string?>("SELECT status FROM cash.sistecredito_attempts WHERE invoice = @invoice", new { invoice = request.Invoice });
        };

        var payment = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        seen.ShouldBe("PENDING");
        payment.TransactionId.ShouldNotBeNull();

        // Sin internet al crear: error claro y el intento queda registrado (cancelado, con el motivo); el pendiente anterior sigue vivo.
        factory.Gateway.Offline = true;
        await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_OFFLINE");
        factory.Gateway.Offline = false;
        (await shop.Catalog.ScalarAsync<long>(
                "SELECT count(*) FROM cash.sistecredito_attempts WHERE status = 'CANCELLED' AND transaction_id IS NULL AND message LIKE 'No se pudo crear el cobro%'"))
            .ShouldBe(1);
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}")).Status.ShouldBe("PENDING");
    }

    [Fact]
    public async Task Con_centavos_Sistecredito_avisa_cuanto_cobrar_en_pesos_enteros()
    {
        // B5.
        var (factory, shop, _) = await ShopAsync();
        await using var _ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);
        var response = await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/cash/sistecredito/payments", Start(sale.Id, 2_500.50m));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await Phase3.SecurityScenario.ProblemAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("CASH.SISTECREDITO_INVALID_AMOUNT");
        problem.GetProperty("detail").GetString()!.ShouldContain("pesos enteros");
        factory.Gateway.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_pendiente_de_mas_de_24_horas_solo_vence_si_Sistecredito_responde_y_si_aprueba_despues_se_revisa()
    {
        // N3.
        var (factory, shop, _) = await ShopAsync();
        await using var __ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);
        var payment = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        var url = $"/api/v1/cash/sistecredito/payments/{payment.Id}";

        // Sin internet o con un error de Sistecrédito no vence: el cliente pudo aprobar mientras tanto.
        factory.Gateway.Offline = true;
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddHours(25));
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, url)).Status.ShouldBe("PENDING");
        factory.Gateway.Offline = false;

        // Sistecrédito responde que sigue pendiente: vence.
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddHours(25).AddMinutes(10));
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, url)).Status.ShouldBe("EXPIRED");

        // El cliente aprobó después: la revisión automática sigue mirando los vencidos en la caja (72 h) y lo deja en revisión.
        factory.Gateway.SetStatus(payment.TransactionId!, "Approved");
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddHours(26));
        var review = await GetAsync<SistecreditoPaymentDto>(shop.Cashier, url);
        review.Status.ShouldBe("REVIEW_REQUIRED");
        review.Usable.ShouldBeTrue();
    }

    [Fact]
    public async Task La_revision_automatica_le_da_turno_a_todos_aunque_haya_mas_de_50_abiertos()
    {
        // M4/N7: más de 50 abiertos: primero los que llevan más tiempo sin consultarse; cada uno se guarda aparte.
        var (factory, shop, _) = await ShopAsync();
        await using var __ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var company = scope.ServiceProvider.GetRequiredService<IInstallationContext>().CompanyId!.Value;
            var store = scope.ServiceProvider.GetRequiredService<ISistecreditoStore>();
            for (var i = 0; i < 55; i++)
            {
                var attempt = SistecreditoAttempt.Create(Guid.NewGuid(), company, sale.Id, null, 1_000m, "CC", "123456", SistecreditoEnvironment.Staging, started).Value;
                attempt.Started($"bulk-{i:D2}", "Pending", started.AddSeconds(i));
                store.Add(attempt);
            }

            (await scope.ServiceProvider.GetRequiredService<IShortTransactions>().SaveAsync(cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        }

        (await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddMinutes(2))).Checked.ShouldBe(50);
        Bulk(factory).Count.ShouldBe(50);
        await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddMinutes(3));
        Bulk(factory).Count.ShouldBe(55); // los 5 que faltaban entran primero en la segunda vuelta

        static HashSet<string> Bulk(SistecreditoServerFactory factory) =>
            factory.Gateway.Queries.Select(q => q.TransactionId).Where(t => t.StartsWith("bulk-", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public async Task Si_la_caja_deja_de_esperar_despues_de_crear_el_cobro_el_numero_de_transaccion_queda_guardado()
    {
        // M5: la caja se desconecta justo después de que Sistecrédito crea la transacción.
        var (factory, shop, _) = await ShopAsync();
        await using var __ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);
        using var cts = new CancellationTokenSource();
        string? created = null;
        factory.Gateway.AfterCreate = id =>
        {
            created = id;
            cts.Cancel(); // la caja deja de esperar (se cierra la petición HTTP)
        };

        await Should.ThrowAsync<OperationCanceledException>(() => shop.Cashier.PostAsJsonAsync(
            "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue), cts.Token));
        factory.Gateway.AfterCreate = null;
        created.ShouldNotBeNull();

        // El servidor termina de guardar por su cuenta (sin la caja esperando).
        string? status = null;
        for (var i = 0; i < 50 && status is null; i++)
        {
            status = await shop.Catalog.ScalarAsync<string?>($"SELECT status FROM cash.sistecredito_attempts WHERE transaction_id = '{created}'");
            if (status is null)
            {
                await Task.Delay(100, Ct);
            }
        }

        status.ShouldBe("PENDING");
    }

    [Fact]
    public async Task Con_un_credito_aprobado_la_venta_no_baja_de_su_valor_y_el_supervisor_autoriza_no_usarlo()
    {
        // TP1 + TP2: aprobados 6.000 de 9.000; el cliente quiere llevar menos y pagar en efectivo.
        var (factory, shop, _) = await ShopAsync();
        await using var __ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 3);
        var part = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, 6_000m));
        factory.Gateway.SetStatus(part.TransactionId!, "Approved");
        await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{part.Id}/check");
        var line = sale.Lines.Single().Id;

        // Bajar la cantidad dejaría la venta (3.000) por debajo del crédito (6.000): no se puede.
        var detail = await ProblemDetailAsync(
            await RawAsync(shop.Cashier, HttpMethod.Patch, $"/api/v1/sales/{sale.Id}/lines/{line}", new { quantity = 1 }), "SALES.SISTECREDITO_CREDIT_ABOVE_TOTAL");
        detail.ShouldContain("No usar este crédito");
        (await GetAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{sale.Id}")).Total.ShouldBe(9_000m);

        // «No usar este crédito» exige supervisor; con su autorización el crédito queda en revisión y deja de ofrecerse.
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/sistecredito/release", new { transactionId = part.TransactionId })
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.AUTHORIZATION_REQUIRED");
        await shop.AuthorizedAsync<SaleDto>(HttpMethod.Post, $"/api/v1/sales/{sale.Id}/sistecredito/release", new { transactionId = part.TransactionId });
        var released = await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{part.Id}");
        released.Status.ShouldBe("REVIEW_REQUIRED");
        released.Message.ShouldBe("No usado en la venta: reverse el pago en el portal de Sistecrédito");
        released.Usable.ShouldBeFalse();
        (await GetAsync<List<SistecreditoPaymentDto>>(shop.Cashier, $"/api/v1/cash/sistecredito/sales/{sale.Id}/open")).ShouldBeEmpty();
        (await shop.Catalog.ScalarAsync<long>(
                "SELECT count(*) FROM audit.audit_log WHERE action = 'SISTECREDITO_REVIEW_REQUIRED' AND authorized_by IS NOT NULL AND summary LIKE '%no usado en la venta%'"))
            .ShouldBe(1);

        // Ahora sí: baja la cantidad y se completa en efectivo; la administración tiene el aviso para reversarlo.
        await SendAsync<SaleDto>(shop.Cashier, HttpMethod.Patch, $"/api/v1/sales/{sale.Id}/lines/{line}", new { quantity = 1 });
        (await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 3_000m)])).Sale.Status.ShouldBe("COMPLETED");
        (await GetAsync<MeDto>(shop.Owner, "/api/v1/auth/me")).SistecreditoReviews.ShouldBe(1);
    }

    [Fact]
    public async Task Al_completar_con_otro_medio_los_cobros_pendientes_se_cancelan_o_se_revisan()
    {
        // TP8.
        var (factory, shop, _) = await ShopAsync();
        await using var __ = factory;

        // Seguía pendiente: se cancela en la caja.
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);
        var pending = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, sale.AmountDue)]);
        var cancelled = await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{pending.Id}");
        cancelled.Status.ShouldBe("CANCELLED");
        cancelled.Message.ShouldBe("La venta se completó con otro medio de pago.");

        // El cliente lo aprobó justo antes (la caja no lo había visto): queda en revisión para reversarlo.
        var second = await shop.StartAsync();
        second = await shop.AddAsync(second.Id, shop.Rice, 1);
        var late = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(second.Id, second.AmountDue));
        factory.Gateway.SetStatus(late.TransactionId!, "Approved");
        await shop.CompleteAsync(second.Id, [Pay(shop.Cash, second.AmountDue)]);
        var review = await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{late.Id}");
        review.Status.ShouldBe("REVIEW_REQUIRED");
        review.Message!.ShouldContain("después de completar la venta");
    }

    [Fact]
    public async Task Otra_caja_no_ve_ni_consulta_los_cobros_de_esta_venta()
    {
        // TP10/N9 con dos cajas.
        var factory = new SistecreditoServerFactory("MULTI");
        await using var __ = factory;
        var shop = await SalesScenario.CreateAsync(factory, withTerminalUsers: false);
        await SendAsync<SistecreditoSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/cash/sistecredito/settings", Settings);
        var second = (await PostAsync<TerminalDto>(shop.Owner, $"/api/v1/organization/branches/{shop.Catalog.Setup.BranchId}/terminals",
            new { code = "C02", name = "Caja C02" }, HttpStatusCode.Created)).Id;

        async Task<HttpClient> TillAsync(Guid terminal, string user, string code, string pin, int index)
        {
            await shop.Security.CreateUserAsync(user, "CASHIER", posCode: code, pin: pin);
            var (client, _) = await shop.Security.PairTerminalAsync(terminal, ip: $"192.168.1.{21 + index}", hostname: $"CAJA-0{index + 1}", fingerprint: (char)('a' + index));
            await SecurityScenario.PosLoginAsync(client, code, pin);
            await shop.OpenSessionAsync(client);
            return client;
        }

        var mine = await TillAsync(shop.Catalog.Setup.PosTerminalId, "cajera1", "201", "5937", 0);
        var other = await TillAsync(second, "cajera2", "202", "6284", 1);
        var sale = await shop.StartAsync(mine);
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1, mine);
        var payment = await PostAsync<SistecreditoPaymentDto>(mine, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        (await GetAsync<List<SistecreditoPaymentDto>>(mine, $"/api/v1/cash/sistecredito/sales/{sale.Id}/open")).Single().Id.ShouldBe(payment.Id);

        await RawAsync(other, HttpMethod.Get, $"/api/v1/cash/sistecredito/sales/{sale.Id}/open")
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_SALE_NOT_OPEN");
        await RawAsync(other, HttpMethod.Post, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_SALE_NOT_OPEN");
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, $"/api/v1/cash/sistecredito/payments/{payment.Id}"), (HttpMethod.Get, $"/api/v1/cash/sistecredito/payments/{payment.Id}/qr"),
                     (HttpMethod.Post, $"/api/v1/cash/sistecredito/payments/{payment.Id}/check"),
                     (HttpMethod.Post, $"/api/v1/cash/sistecredito/payments/{payment.Id}/cancel"),
                 })
        {
            await RawAsync(other, method, url).ShouldFailWithAsync(HttpStatusCode.NotFound, "CASH.SISTECREDITO_NOT_FOUND");
        }

        (await GetAsync<SistecreditoPaymentDto>(mine, $"/api/v1/cash/sistecredito/payments/{payment.Id}")).Status.ShouldBe("PENDING");
    }

    [Fact]
    public async Task Un_conflicto_de_concurrencia_se_recarga_y_reintenta_sin_perder_ni_atascar_cobros()
    {
        // N5/N7/TP4/TP5: otro proceso cambia la fila (xmin) entre la lectura y el guardado.
        var (factory, shop, _) = await ShopAsync();
        await using var __ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);
        var payment = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ISistecreditoStore>();
            var transactions = scope.ServiceProvider.GetRequiredService<IShortTransactions>();
            var attempt = (await store.GetAttemptAsync(payment.Id, Ct))!;

            // Otro proceso la toca: el guardado choca, recarga y reaplica UNA vez sobre lo recargado.
            await shop.Catalog.ExecuteAsync($"UPDATE cash.sistecredito_attempts SET message = 'otro proceso' WHERE id = '{payment.Id}'");
            attempt.Apply("Approved", null, 6, null, DateTimeOffset.UtcNow);
            var reapplied = 0;
            (await transactions.SaveAsync(
                () =>
                {
                    reapplied++;
                    attempt.Apply("Approved", null, 6, null, DateTimeOffset.UtcNow);
                },
                Ct)).IsSuccess.ShouldBeTrue();
            reapplied.ShouldBe(1);

            // Sin reaplicar: conflicto → error de negocio (no 500), lo pendiente se descarta y el contexto sigue sirviendo.
            await shop.Catalog.ExecuteAsync($"UPDATE cash.sistecredito_attempts SET message = 'otra vez' WHERE id = '{payment.Id}'");
            attempt.Checked(DateTimeOffset.UtcNow);
            var conflict = await transactions.SaveAsync(cancellationToken: Ct);
            conflict.IsFailure.ShouldBeTrue();
            conflict.Error.Code.ShouldBe("DATABASE.CONCURRENT_UPDATE");
            attempt.Message.ShouldBe("otra vez"); // recargado desde la base de datos
            attempt.CheckFailed(DateTimeOffset.UtcNow);
            (await transactions.SaveAsync(cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        }

        var saved = await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}");
        saved.Status.ShouldBe("APPROVED");
        (await shop.Catalog.ScalarAsync<int>($"SELECT check_failures FROM cash.sistecredito_attempts WHERE id = '{payment.Id}'")).ShouldBe(1);

        // Con espera (next_check_at en el futuro) la revisión automática no lo toma: no ocupa el primer turno en cada vuelta.
        (await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddSeconds(30))).Checked.ShouldBe(0);
    }


    [Fact]
    public async Task Consultar_o_cancelar_un_cobro_usa_el_ambiente_en_que_se_creo()
    {
        // N8.
        var (factory, shop, _) = await ShopAsync();
        await using var __ = factory;
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);
        var payment = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, sale.AmountDue));
        await SendAsync<SistecreditoSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/cash/sistecredito/settings", new { environment = "PRODUCTION", enabled = true });
        await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}/check");
        (await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}/cancel")).Status.ShouldBe("CANCELLED");
        factory.Gateway.Queries.Where(q => q.TransactionId == payment.TransactionId).Select(q => q.Environment).Distinct()
            .ShouldBe([SistecreditoEnvironment.Staging]);
    }

    private static async Task<(SistecreditoServerFactory Factory, SalesScenario Shop, Guid Method)> ShopAsync()
    {
        var factory = new SistecreditoServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await SendAsync<SistecreditoSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/cash/sistecredito/settings", Settings);
        var method = (await GetAsync<List<PaymentMethodDto>>(shop.Owner, "/api/v1/cash/payment-methods")).Single(m => m.Code == "SISTECREDITO").Id;
        await shop.OpenSessionAsync();
        return (factory, shop, method);
    }
}
