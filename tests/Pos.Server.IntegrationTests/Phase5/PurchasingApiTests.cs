using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase5;

/// <summary>Criterios de aceptación de la Fase 5 (propuesta §14): terceros, compras, lotes, cartera y devoluciones.</summary>
public class PurchasingApiTests
{
    [Fact]
    public async Task Terceros_con_DV_identificacion_unica_busqueda_y_consumidor_final()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var owner = scenario.Owner;

        var consumer = await GetAsync<PartyDto>(owner, "/api/v1/parties/by-identification?type=CC&number=222222222222");
        consumer.IsSystem.ShouldBeTrue();
        (await owner.PutAsJsonAsync($"/api/v1/parties/{consumer.Id}",
                new { personType = "Natural", identificationType = "CC", identificationNumber = "222222222222", firstNames = "X", lastNames = "Y" }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await owner.PostAsJsonAsync("/api/v1/parties", new
            {
                personType = "Legal", identificationType = "NIT", identificationNumber = "890903938", checkDigit = "1", legalName = "Bancolombia",
            }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PARTIES.INVALID_NIT_CHECK_DIGIT");
        await owner.PostAsJsonAsync("/api/v1/parties", new
            {
                personType = "Legal", identificationType = "NIT", identificationNumber = "800197268", checkDigit = "4", legalName = "Otra",
            }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "PARTIES.IDENTIFICATION_DUPLICATED");
        await owner.PostAsJsonAsync("/api/v1/parties", new
            {
                personType = "Legal", identificationType = "CC", identificationNumber = "1020304050", legalName = "Empresa con cédula",
            }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PARTIES.PERSON_TYPE_NOT_ALLOWED");

        var farmer = await scenario.CreatePartyAsync(new
        {
            personType = "Natural", identificationType = "CC", identificationNumber = "1020304050", firstNames = "José Ángel", lastNames = "Rodríguez",
            phone = "3101234567", contacts = new[] { new { name = "José", isPrimary = true } },
        });
        farmer.TaxRegime.ShouldBe("49");
        farmer.Contacts.Single().IsPrimary.ShouldBeTrue();

        (await GetAsync<PartyPageDto>(owner, "/api/v1/parties?search=angel rodriguez")).Items.Single().Id.ShouldBe(farmer.Id);
        (await GetAsync<PartyPageDto>(owner, "/api/v1/parties?search=800197268-4")).Items.Single().Id.ShouldBe(scenario.PartyId);

        // Un cambio del tercero viaja a la sincronización con sus campos (D4-09).
        (await scenario.Catalog.ScalarAsync<long>(
                "SELECT count(*) FROM system.outbox_messages WHERE type = 'sync.entity_changed.v1' AND payload->>'entity' = 'parties.parties'"))
            .ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Semana_de_compras_orden_recepcion_parcial_y_final_pago_de_dos_facturas_devolucion_y_nota_credito()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var owner = scenario.Owner;

        // Orden: 10 cajas x24 de yogurt a $60.000 y 100 arroces (sin costo: toma el último del proveedor, 0).
        var order = await scenario.SendAsync<PurchaseOrderDto>("/api/v1/purchasing/orders", new
        {
            supplierId = scenario.SupplierId, warehouseId = scenario.Floor,
            lines = new object[]
            {
                new { productId = scenario.Yogurt, packagingId = scenario.YogurtBox, quantity = 10, unitCost = 60_000 },
                new { productId = scenario.Rice, quantity = 100, unitCost = 2_000 },
            },
        }, HttpStatusCode.Created);
        order.Total.ShouldBe(800_000m);
        await scenario.SendAsync<PurchaseOrderDto>($"/api/v1/purchasing/orders/{order.Id}/approve");
        (await scenario.SendAsync<PurchaseOrderDto>($"/api/v1/purchasing/orders/{order.Id}/send")).Status.ShouldBe("SENT");

        // Recepción 1 (contra la orden, sin líneas → propone lo pendiente): se ajustan a 4 cajas + 100 arroces, con flete y retención.
        var yogurtLine = order.Lines.Single(l => l.ProductId == scenario.Yogurt);
        var riceLine = order.Lines.Single(l => l.ProductId == scenario.Rice);
        var proposed = await scenario.SendAsync<PurchaseDto>("/api/v1/purchasing/purchases", new
        {
            supplierId = scenario.SupplierId, warehouseId = scenario.Floor, purchaseOrderId = order.Id, supplierInvoiceNumber = "FE-1",
            invoiceDate = PurchasingScenario.Today, paymentMode = "Credit", proration = "Value", chargesTotal = 0,
        }, HttpStatusCode.Created);
        proposed.Lines.Count.ShouldBe(2);
        proposed.Lines.Single(l => l.ProductId == scenario.Yogurt).Quantity.ShouldBe(10m);
        var first = await scenario.PostAsync((await scenario.DraftAsync("FE-1b", [
            new { productId = scenario.Yogurt, packagingId = scenario.YogurtBox, quantity = 4, unitCost = 60_000, discount = 12_000,
                  lotNumber = "L-A", expiryDate = PurchasingScenario.Today.AddDays(60), orderLineId = yogurtLine.Id },
            new { productId = scenario.Rice, quantity = 100, unitCost = 2_000, orderLineId = riceLine.Id },
        ], orderId: order.Id, charges: 10_000, withholdings: [new { kind = "Retefuente", @base = 428_000, rate = 2.5 }])).Id);
        (await GetAsync<PurchaseOrderDto>(owner, $"/api/v1/purchasing/orders/{order.Id}")).Status.ShouldBe("PARTIALLY_RECEIVED");
        // Flete de $10.000 prorrateado por valor neto (228.000 y 200.000); el IVA es descontable y no suma al costo.
        var yogurtPurchase = first.Lines.Single(l => l.ProductId == scenario.Yogurt);
        first.Lines.Sum(l => l.ChargesAmount).ShouldBe(10_000m);
        yogurtPurchase.ChargesAmount.ShouldBe(5_327.10m);
        var yogurtCost = yogurtPurchase.NetUnitCost;
        yogurtCost.ShouldBe(decimal.Round((228_000m + 5_327.10m) / 96m, 4));
        first.WithholdingTotal.ShouldBe(10_700m);
        first.PayableTotal.ShouldBe(first.Total - 10_700m);

        // Recibir más de lo pedido falla (tolerancia 0 %, RN-PUR-04); el resto sí se recibe y la orden queda recibida.
        var excess = await scenario.DraftAsync("FE-2", [new
        {
            productId = scenario.Yogurt, packagingId = scenario.YogurtBox, quantity = 7, unitCost = 60_000, lotNumber = "L-B",
            expiryDate = PurchasingScenario.Today.AddDays(20), orderLineId = yogurtLine.Id,
        }], orderId: order.Id);
        await owner.PostAsync($"/api/v1/purchasing/purchases/{excess.Id}/post", null, Ct).ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "PURCHASING.RECEIPT_EXCEEDS_ORDER");
        var second = await scenario.DraftAsync("FE-3", [new
        {
            productId = scenario.Yogurt, packagingId = scenario.YogurtBox, quantity = 6, unitCost = 60_000, lotNumber = "L-B",
            expiryDate = PurchasingScenario.Today.AddDays(20), orderLineId = yogurtLine.Id,
        }], orderId: order.Id);
        await scenario.PostAsync(second.Id);
        (await GetAsync<PurchaseOrderDto>(owner, $"/api/v1/purchasing/orders/{order.Id}")).Status.ShouldBe("RECEIVED");

        // Kardex al costo neto: 96 u al costo con flete + 144 u a 2.500 → el promedio sale del valor.
        var stock = await scenario.StockAsync(scenario.Yogurt);
        stock.Quantity.ShouldBe(240m);
        stock.TotalValue.ShouldBe(decimal.Round(96m * yogurtCost, 4) + 360_000m);
        (await scenario.LotsAsync(scenario.Yogurt)).Select(l => (l.LotNumber, l.Quantity)).ShouldBe([("L-B", 144m), ("L-A", 96m)]);

        // Un pago cubre las dos facturas (la segunda completa, la primera parcial).
        var payables = await GetAsync<List<PayableDto>>(owner, $"/api/v1/purchasing/payables?supplierId={scenario.SupplierId}");
        var p1 = payables.Single(p => p.DocumentNumber == "FE-1B");
        var p2 = payables.Single(p => p.DocumentNumber == "FE-3");
        p1.Balance.ShouldBe(first.PayableTotal);
        p2.DueDate.ShouldBe(PurchasingScenario.Today.AddDays(30));
        var payment = await scenario.SendAsync<PaymentDto>("/api/v1/purchasing/payments", new
        {
            supplierId = scenario.SupplierId, paymentMethodId = scenario.Transfer, reference = "TRF-900",
            allocations = new[] { new { accountId = p1.Id, amount = 100_000m }, new { accountId = p2.Id, amount = p2.Balance } },
        }, HttpStatusCode.Created);
        payment.Amount.ShouldBe(100_000m + p2.Balance);
        (await GetAsync<PayableDto>(owner, $"/api/v1/purchasing/payables/{p2.Id}")).Status.ShouldBe("SETTLED");
        await owner.PostAsJsonAsync("/api/v1/purchasing/payments", new
            {
                supplierId = scenario.SupplierId, paymentMethodId = scenario.Transfer,
                allocations = new[] { new { accountId = p1.Id, amount = 1m } },
            }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.REFERENCE_REQUIRED");

        // Devolución de 10 yogures del lote A: al costo de la compra y del lote original; la nota crédito la liquida.
        var returned = await scenario.SendAsync<SupplierReturnDto>("/api/v1/purchasing/returns", new
        {
            purchaseId = first.Id, reason = "Empaque inflado",
            lines = new[] { new { purchaseLineId = first.Lines.Single(l => l.ProductId == scenario.Yogurt).Id, baseQuantity = 10m } },
        }, HttpStatusCode.Created);
        returned = await scenario.SendAsync<SupplierReturnDto>($"/api/v1/purchasing/returns/{returned.Id}/post");
        returned.Total.ShouldBe(decimal.Round(10m * yogurtCost, 2));
        (await scenario.SendAsync<SupplierReturnDto>($"/api/v1/purchasing/returns/{returned.Id}/settle", new { settlement = "CreditNote", reference = "NC-7" }))
            .Status.ShouldBe("SETTLED");
        (await scenario.LotsAsync(scenario.Yogurt)).Single(l => l.LotNumber == "L-A").Quantity.ShouldBe(86m);
        var kardex = await GetAsync<KardexDto>(owner, $"/api/v1/inventory/kardex?warehouseId={scenario.Floor}&productId={scenario.Yogurt}");
        kardex.Entries[^1].MovementType.ShouldBe("SUPPLIER_RETURN");
        kardex.Entries[^1].UnitCost.ShouldBe(yogurtCost);
        kardex.Entries[^1].LotNumber.ShouldBe("L-A");

        // Cartera: libro y edades cuadran.
        var account = await GetAsync<PayableDto>(owner, $"/api/v1/purchasing/payables/{p1.Id}");
        account.Entries.Select(e => e.EntryType).ShouldBe(["CHARGE", "PAYMENT", "RETURN"]);
        account.Balance.ShouldBe(first.PayableTotal - 100_000m - returned.CreditTotal);
        var aging = await GetAsync<AgingReportDto>(owner, "/api/v1/purchasing/payables/aging");
        aging.Totals.Current.ShouldBe(account.Balance);
        (await GetAsync<SupplierStatementDto>(owner, $"/api/v1/purchasing/suppliers/{scenario.SupplierId}/statement")).Payments.Count.ShouldBe(1);
        (await GetAsync<SupplierDto>(owner, $"/api/v1/purchasing/suppliers/{scenario.SupplierId}")).OpenBalance.ShouldBe(account.Balance);

        // La verificación del kardex (incluidas las cantidades por lote) no encuentra diferencias.
        (await scenario.SendAsync<VerificationDto>("/api/v1/inventory/verification")).Discrepancies.ShouldBe(0);
    }

    [Fact]
    public async Task Anular_una_compra_revierte_kardex_cartera_y_orden_y_se_bloquea_con_pagos_o_salidas()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var owner = scenario.Owner;

        var purchase = await scenario.BuyAsync("FE-10", new { productId = scenario.Rice, quantity = 50, unitCost = 2_000 });
        (await scenario.StockAsync(scenario.Rice)).Quantity.ShouldBe(50m);
        await owner.PostAsJsonAsync($"/api/v1/purchasing/purchases/{purchase.Id}/void", new { reason = "no" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.REASON_REQUIRED");
        var voided = await scenario.SendAsync<PurchaseDto>($"/api/v1/purchasing/purchases/{purchase.Id}/void", new { reason = "Factura de otro proveedor" });
        voided.Status.ShouldBe("VOIDED");
        (await scenario.StockAsync(scenario.Rice)).Quantity.ShouldBe(0m);
        var kardex = await GetAsync<KardexDto>(owner, $"/api/v1/inventory/kardex?warehouseId={scenario.Floor}&productId={scenario.Rice}");
        kardex.Entries.Select(e => e.MovementType).ShouldBe(["PURCHASE_RECEIPT", "REVERSAL"]);
        (await GetAsync<List<PayableDto>>(owner, "/api/v1/purchasing/payables?openOnly=false")).Single(p => p.PurchaseId == purchase.Id).Status.ShouldBe("VOIDED");

        // La misma factura se puede volver a registrar (la anulada no cuenta, RN-PUR-02).
        var again = await scenario.BuyAsync("FE-10", new { productId = scenario.Rice, quantity = 50, unitCost = 2_000 });

        // Con salidas que dejarían el saldo negativo, no se anula (RN-PUR-05).
        var reasons = await GetAsync<List<AdjustmentReasonDto>>(owner, "/api/v1/inventory/reasons");
        var adjustment = await scenario.SendAsync<AdjustmentDto>("/api/v1/inventory/adjustments", new
        {
            warehouseId = scenario.Floor, reasonId = reasons.Single(r => r.Code == "DAMAGE").Id, lines = new[] { new { productId = scenario.Rice, quantity = 5 } },
        }, HttpStatusCode.Created);
        await scenario.SendAsync<AdjustmentDto>($"/api/v1/inventory/adjustments/{adjustment.Id}/post");
        await owner.PostAsJsonAsync($"/api/v1/purchasing/purchases/{again.Id}/void", new { reason = "Factura de otro proveedor" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "INVENTORY.INSUFFICIENT_STOCK");

        // Compra de contado: nace pagada y, con pago, no se anula.
        var cash = await scenario.PostAsync((await scenario.DraftAsync("FE-11", [new { productId = scenario.Rice, quantity = 10, unitCost = 1_000 }],
            mode: "Cash", method: scenario.Cash)).Id);
        (await GetAsync<List<PayableDto>>(owner, "/api/v1/purchasing/payables?openOnly=false")).Single(p => p.PurchaseId == cash.Id).Status.ShouldBe("SETTLED");
        await owner.PostAsJsonAsync($"/api/v1/purchasing/purchases/{cash.Id}/void", new { reason = "Factura de otro proveedor" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "PURCHASING.PURCHASE_HAS_PAYMENTS");

        // Al anular el pago, la cuenta vuelve a quedar abierta y la compra sí se anula.
        var payment = (await GetAsync<List<PurchasingDocumentDto>>(owner, "/api/v1/purchasing/payments")).Single();
        (await scenario.SendAsync<PaymentDto>($"/api/v1/purchasing/payments/{payment.Id}/void", new { reason = "Pago registrado por error" })).Status.ShouldBe("VOIDED");
        (await scenario.SendAsync<PurchaseDto>($"/api/v1/purchasing/purchases/{cash.Id}/void", new { reason = "Factura de otro proveedor" })).Status.ShouldBe("VOIDED");
        (await scenario.SendAsync<VerificationDto>("/api/v1/inventory/verification")).Discrepancies.ShouldBe(0);
    }

    [Fact]
    public async Task Lotes_obligatorios_FEFO_ajuste_por_lote_alertas_y_reintegro()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var owner = scenario.Owner;

        var noLot = await scenario.DraftAsync("FE-20", [new { productId = scenario.Yogurt, quantity = 10, unitCost = 3_000 }]);
        await owner.PostAsync($"/api/v1/purchasing/purchases/{noLot.Id}/post", null, Ct).ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.LOT_REQUIRED");

        await scenario.BuyAsync("FE-21",
            new { productId = scenario.Yogurt, quantity = 20, unitCost = 2_000, lotNumber = "tarde", expiryDate = PurchasingScenario.Today.AddDays(90) },
            new { productId = scenario.Yogurt, quantity = 15, unitCost = 2_000, lotNumber = "pronto", expiryDate = PurchasingScenario.Today.AddDays(5) });
        var expensive = await scenario.BuyAsync("FE-22",
            new { productId = scenario.Yogurt, quantity = 5, unitCost = 3_500, lotNumber = "TARDE", expiryDate = PurchasingScenario.Today.AddDays(90) });

        // Precio de venta 3.500 con IVA 19 % = 2.941 sin IVA: el nuevo costo (3.500) queda por encima → alerta; el costo varió 75 %.
        expensive.Alerts.Select(a => a.Kind).ShouldBe(["COST_VARIATION", "PRICE_BELOW_COST"], ignoreOrder: true);
        var otherExpiry = await scenario.DraftAsync("FE-23", [new
        {
            productId = scenario.Yogurt, quantity = 1, unitCost = 1, lotNumber = "TARDE", expiryDate = PurchasingScenario.Today.AddDays(91),
        }]);
        await owner.PostAsync($"/api/v1/purchasing/purchases/{otherExpiry.Id}/post", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "INVENTORY.LOT_EXPIRY_MISMATCH");

        // Una salida sin lote se reparte FEFO: 18 = 15 del lote "PRONTO" + 3 del "TARDE".
        var reasons = await GetAsync<List<AdjustmentReasonDto>>(owner, "/api/v1/inventory/reasons");
        var damage = await scenario.SendAsync<AdjustmentDto>("/api/v1/inventory/adjustments", new
        {
            warehouseId = scenario.Floor, reasonId = reasons.Single(r => r.Code == "DAMAGE").Id, lines = new[] { new { productId = scenario.Yogurt, quantity = 18 } },
        }, HttpStatusCode.Created);
        await scenario.SendAsync<AdjustmentDto>($"/api/v1/inventory/adjustments/{damage.Id}/post");
        (await scenario.LotsAsync(scenario.Yogurt)).Select(l => (l.LotNumber, l.Quantity)).ShouldBe([("PRONTO", 0m), ("TARDE", 22m)]);
        (await GetAsync<List<LotStockDto>>(owner, "/api/v1/inventory/lots?expiring=true")).ShouldBeEmpty();

        // Ajuste por lote: entrada a un lote nuevo y salida de un lote sin existencias (falla).
        var found = await scenario.SendAsync<AdjustmentDto>("/api/v1/inventory/adjustments", new
        {
            warehouseId = scenario.Floor, reasonId = reasons.Single(r => r.Code == "FOUND").Id,
            lines = new[] { new { productId = scenario.Yogurt, quantity = 4, lotNumber = "hallado", expiryDate = PurchasingScenario.Today.AddDays(10) } },
        }, HttpStatusCode.Created);
        await scenario.SendAsync<AdjustmentDto>($"/api/v1/inventory/adjustments/{found.Id}/post");
        (await GetAsync<List<LotStockDto>>(owner, "/api/v1/inventory/lots?expiring=true")).Single().LotNumber.ShouldBe("HALLADO");
        var fromEmpty = await scenario.SendAsync<AdjustmentDto>("/api/v1/inventory/adjustments", new
        {
            warehouseId = scenario.Floor, reasonId = reasons.Single(r => r.Code == "DAMAGE").Id,
            lines = new[] { new { productId = scenario.Yogurt, quantity = 1, lotNumber = "PRONTO" } },
        }, HttpStatusCode.Created);
        await owner.PostAsync($"/api/v1/inventory/adjustments/{fromEmpty.Id}/post", null, Ct).ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "INVENTORY.LOT_INSUFFICIENT");

        // Devolución pagada de contado y liquidada con reintegro: la cuenta termina en cero.
        var paid = await scenario.PostAsync((await scenario.DraftAsync("FE-24",
            [new { productId = scenario.Rice, quantity = 10, unitCost = 1_000 }], mode: "Cash", method: scenario.Cash)).Id);
        var back = await scenario.SendAsync<SupplierReturnDto>("/api/v1/purchasing/returns",
            new { purchaseId = paid.Id, reason = "Arroz con gorgojo", lines = new[] { new { purchaseLineId = paid.Lines[0].Id, baseQuantity = 10m } } },
            HttpStatusCode.Created);
        await scenario.SendAsync<SupplierReturnDto>($"/api/v1/purchasing/returns/{back.Id}/post");
        var balance = (await GetAsync<List<PayableDto>>(owner, "/api/v1/purchasing/payables?openOnly=false")).Single(p => p.PurchaseId == paid.Id);
        balance.Balance.ShouldBe(-back.CreditTotal);
        await scenario.SendAsync<SupplierReturnDto>($"/api/v1/purchasing/returns/{back.Id}/settle", new { settlement = "Refund", reference = "RC-1" });
        (await GetAsync<PayableDto>(owner, $"/api/v1/purchasing/payables/{balance.Id}")).Balance.ShouldBe(0m);
        (await scenario.SendAsync<VerificationDto>("/api/v1/inventory/verification")).Discrepancies.ShouldBe(0);
    }

    [Fact]
    public async Task Dos_usuarios_contabilizan_la_misma_compra_y_solo_uno_lo_logra()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var draft = await scenario.DraftAsync("FE-30", [new { productId = scenario.Rice, quantity = 10, unitCost = 1_000 }]);
        var second = factory.CreateClient();
        second.DefaultRequestHeaders.Authorization = scenario.Owner.DefaultRequestHeaders.Authorization;

        var results = await Task.WhenAll(
            scenario.Owner.PostAsync($"/api/v1/purchasing/purchases/{draft.Id}/post", null, Ct),
            second.PostAsync($"/api/v1/purchasing/purchases/{draft.Id}/post", null, Ct));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        (await scenario.StockAsync(scenario.Rice)).Quantity.ShouldBe(10m);
        (await scenario.Catalog.ScalarAsync<long>($"SELECT count(*) FROM purchasing.accounts_payable WHERE purchase_id = '{draft.Id}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Permisos_de_compras_y_medios_de_pago()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var security = SecurityScenario.ForExisting(factory, scenario.Owner, scenario.Catalog.Setup);
        await security.CreateUserAsync("cajera", "CASHIER");
        await security.CreateUserAsync("compras", "PURCHASING");
        await security.CreateUserAsync("contador", "ACCOUNTANT");
        var cashier = await security.LocalClientAsync("cajera");
        var buyer = await security.LocalClientAsync("compras");
        var accountant = await security.LocalClientAsync("contador");

        (await cashier.GetAsync("/api/v1/parties?search=lactea", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cashier.GetAsync("/api/v1/purchasing/purchases", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // Fase 8 (D8-04): la cajera ya no crea ni modifica terceros; crea clientes con el alta rápida (/customers/quick).
        (await cashier.PostAsJsonAsync("/api/v1/parties", new { }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Compras registra borradores pero no contabiliza (lo hace el administrador).
        var draft = await scenario.DraftAsync("FE-40", [new { productId = scenario.Rice, quantity = 1, unitCost = 1_000 }], client: buyer);
        (await buyer.PostAsync($"/api/v1/purchasing/purchases/{draft.Id}/post", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await scenario.PostAsync(draft.Id);

        (await accountant.GetAsync("/api/v1/purchasing/payables/aging", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await accountant.PostAsJsonAsync("/api/v1/purchasing/payments", new { }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await accountant.PostAsJsonAsync($"/api/v1/purchasing/purchases/{draft.Id}/void", new { reason = "Sin permiso" }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Medios de pago: los ve cualquiera; el efectivo del sistema no se inactiva.
        (await GetAsync<List<PaymentMethodDto>>(cashier, "/api/v1/cash/payment-methods")).Count.ShouldBe(8); // Fase 7: medio del sistema CAMBIO
        (await scenario.Owner.PutAsJsonAsync($"/api/v1/cash/payment-methods/{scenario.Cash}",
                new { name = "Efectivo", dianCode = "10", requiresReference = false, sortOrder = 10, isActive = false }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await scenario.Owner.PostAsJsonAsync("/api/v1/cash/payment-methods",
                new { code = "QR_BANCO", name = "QR Bancolombia", kind = "Transfer", dianCode = "47", requiresReference = true, sortOrder = 90 }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // Proveedor bloqueado: no admite compras nuevas.
        (await scenario.SendAsync<SupplierDto>($"/api/v1/purchasing/suppliers/{scenario.SupplierId}/status", new { status = "Blocked" })).Status.ShouldBe("BLOCKED");
        await scenario.Owner.PostAsJsonAsync("/api/v1/purchasing/purchases", new
            {
                supplierId = scenario.SupplierId, warehouseId = scenario.Floor, supplierInvoiceNumber = "FE-41", invoiceDate = PurchasingScenario.Today,
                paymentMode = "Credit", proration = "Value", chargesTotal = 0, lines = new[] { new { productId = scenario.Rice, quantity = 1, unitCost = 1 } },
            }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "PURCHASING.SUPPLIER_BLOCKED");
    }
}
