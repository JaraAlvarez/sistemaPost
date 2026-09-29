using Pos.Modules.Inventory.Application;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.SharedKernel.Domain;

namespace Pos.Modules.Inventory.UnitTests;

public class StockValuationTests
{
    [Fact]
    public void Ejemplo_del_doc_07_entrada_venta_y_averia()
    {
        // Saldo 12 a $2.900 (valor 34.800); compra 48 a $2.950 → 60 unidades, promedio 2.940.
        var start = new StockState(12m, 34_800m, 2_900m);
        var purchase = StockValuation.Inflow(start, 48m, 2_950m, affectsAverage: true);
        purchase.After.ShouldBe(new StockState(60m, 176_400m, 2_940m));
        purchase.TotalCost.ShouldBe(141_600m);

        var sale = StockValuation.Outflow(purchase.After, 2m);
        sale.UnitCost.ShouldBe(2_940m);
        sale.TotalCost.ShouldBe(5_880m);
        sale.After.ShouldBe(new StockState(58m, 170_520m, 2_940m));
    }

    [Fact]
    public void La_ultima_salida_lleva_el_valor_restante_y_el_saldo_queda_en_cero()
    {
        // 3 unidades por $10.000: promedio 3.333,3333 y un residuo que no debe quedar en el saldo.
        var state = StockValuation.Inflow(StockState.Empty, 3m, 3_333.3333m, true).After with { Value = 10_000m };
        var first = StockValuation.Outflow(state, 2m);
        var last = StockValuation.Outflow(first.After, 1m);

        (first.TotalCost + last.TotalCost).ShouldBe(10_000m);
        last.After.Quantity.ShouldBe(0m);
        last.After.Value.ShouldBe(0m);
    }

    [Fact]
    public void Entrada_con_saldo_negativo_toma_el_costo_de_la_entrada()
    {
        var negative = new StockState(-3m, -300m, 100m);
        var entry = StockValuation.Inflow(negative, 10m, 120m, true);

        entry.After.ShouldBe(new StockState(7m, 840m, 120m));
        entry.TotalCost.ShouldBe(1_200m);
    }

    [Fact]
    public void Ajuste_de_entrada_entra_al_promedio_y_no_lo_cambia()
    {
        var state = new StockState(10m, 25_000m, 2_500m);
        var adjustment = StockValuation.Inflow(state, 2m, 9_999m, affectsAverage: false);

        adjustment.UnitCost.ShouldBe(2_500m);
        adjustment.After.ShouldBe(new StockState(12m, 30_000m, 2_500m));
    }

    [Fact]
    public void Cantidades_invalidas()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => StockValuation.Outflow(StockState.Empty, 0m));
        Should.Throw<ArgumentOutOfRangeException>(() => StockValuation.Inflow(StockState.Empty, 1.23456m, 1m, true));
        Should.Throw<ArgumentOutOfRangeException>(() => StockValuation.Inflow(StockState.Empty, 1m, -1m, true));
    }

    [Fact]
    public void Diez_mil_movimientos_aleatorios_conservan_el_valor_al_centavo()
    {
        var random = new Random(20261006);
        var state = StockState.Empty;
        var inflows = 0m;
        var outflows = 0m;
        for (var i = 0; i < 10_000; i++)
        {
            Valuation step;
            if (state.Quantity == 0m || random.Next(3) > 0)
            {
                var quantity = random.Next(1, 500) + (random.Next(4) == 0 ? random.Next(1, 999) / 1000m : 0m);
                var cost = random.Next(100, 20_000) + (random.Next(10_000) / 10_000m);
                step = StockValuation.Inflow(state, quantity, cost, affectsAverage: true);
                inflows += step.TotalCost;
            }
            else
            {
                var quantity = Math.Min(state.Quantity, random.Next(1, 300) + (random.Next(1_000) / 1000m));
                step = StockValuation.Outflow(state, quantity);
                outflows += step.TotalCost;
            }

            state = step.After;
            state.Quantity.ShouldBeGreaterThanOrEqualTo(0m);
            state.Value.ShouldBe(inflows - outflows, $"paso {i}");
            if (state.Quantity == 0m)
            {
                state.Value.ShouldBe(0m);
            }
            else
            {
                Math.Abs((state.AverageCost * state.Quantity) - state.Value).ShouldBeLessThanOrEqualTo((state.Quantity * 0.0001m) + 0.01m);
            }
        }
    }
}

public class ValuedOutflowTests
{
    [Fact]
    public void Devolucion_al_costo_de_la_compra_recalcula_el_promedio()
    {
        // 100 a $2.000 + 100 a $3.000 → promedio 2.500; devolver 50 de la compra a $3.000 deja 150 con valor 350.000.
        var state = new StockState(200m, 500_000m, 2_500m);
        var result = StockValuation.ValuedOutflow(state, 50m, 3_000m);
        result.TotalCost.ShouldBe(150_000m);
        result.UnitCost.ShouldBe(3_000m);
        result.After.ShouldBe(new StockState(150m, 350_000m, 2_333.3333m));
    }

    [Fact]
    public void El_valor_nunca_queda_negativo_y_la_ultima_salida_lleva_el_resto()
    {
        var state = new StockState(10m, 1_000m, 100m);
        var capped = StockValuation.ValuedOutflow(state, 5m, 500m);
        capped.TotalCost.ShouldBe(1_000m);
        capped.After.Value.ShouldBe(0m);
        capped.After.AverageCost.ShouldBe(0m);

        var last = StockValuation.ValuedOutflow(state, 10m, 300m);
        last.TotalCost.ShouldBe(1_000m);
        last.After.ShouldBe(new StockState(0m, 0m, 100m));
    }

    [Fact]
    public void Con_saldo_resultante_negativo_sale_al_promedio()
    {
        var result = StockValuation.ValuedOutflow(new StockState(2m, 200m, 100m), 5m, 999m);
        result.UnitCost.ShouldBe(100m);
        result.After.Quantity.ShouldBe(-3m);
        Should.Throw<ArgumentOutOfRangeException>(() => StockValuation.ValuedOutflow(StockState.Empty, 1m, -1m));
    }
}

public class MovementRulesTests
{
    [Fact]
    public void La_reversion_no_tiene_direccion_fija_y_la_devolucion_a_proveedor_es_valorizada()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => MovementRules.Direction(MovementType.Reversal));
        MovementRules.Db(MovementType.Reversal).ShouldBe("REVERSAL");
        MovementRules.IsValuedOutflow(MovementType.SupplierReturn).ShouldBeTrue();
        MovementRules.IsValuedOutflow(MovementType.Sale).ShouldBeFalse();
        MovementRules.Direction(MovementType.SupplierReturn).ShouldBe(-1);
    }

    [Theory]
    [InlineData(MovementType.InitialBalance, 1, true)]
    [InlineData(MovementType.PurchaseReceipt, 1, true)]
    [InlineData(MovementType.TransferIn, 1, true)]
    [InlineData(MovementType.AdjustmentIn, 1, false)]
    [InlineData(MovementType.CountAdjustmentIn, 1, false)]
    [InlineData(MovementType.Sale, -1, false)]
    [InlineData(MovementType.TransferOut, -1, false)]
    [InlineData(MovementType.Loss, -1, false)]
    public void Direccion_y_valorizacion(MovementType type, int direction, bool valued)
    {
        MovementRules.Direction(type).ShouldBe(direction);
        MovementRules.IsValuedInflow(type).ShouldBe(valued);
    }

    [Fact]
    public void Nombres_en_la_BD_y_tipo_segun_el_motivo()
    {
        foreach (var type in Enum.GetValues<MovementType>())
        {
            MovementRules.Db(type).ShouldMatch("^[A-Z_]+$");
        }

        MovementRules.Db(MovementType.CountAdjustmentOut).ShouldBe("COUNT_ADJUSTMENT_OUT");
        MovementRules.ForAdjustment(ReasonKind.Adjustment, 5m).ShouldBe(MovementType.AdjustmentIn);
        MovementRules.ForAdjustment(ReasonKind.Adjustment, -5m).ShouldBe(MovementType.AdjustmentOut);
        MovementRules.ForAdjustment(ReasonKind.Damage, -1m).ShouldBe(MovementType.Damage);
        MovementRules.ForAdjustment(ReasonKind.InitialBalance, 1m).ShouldBe(MovementType.InitialBalance);
        MovementRules.ForAdjustment(ReasonKind.Expiry, -1m).ShouldBe(MovementType.Expiry);
        MovementRules.ForAdjustment(ReasonKind.Loss, -1m).ShouldBe(MovementType.Loss);
        MovementRules.ForAdjustment(ReasonKind.InternalUse, -1m).ShouldBe(MovementType.InternalUse);
    }
}

public class AdjustmentTests
{
    private static readonly Guid Company = Guid.CreateVersion7();
    private static readonly Guid Creator = Guid.CreateVersion7();
    private static readonly Guid ProductA = Guid.CreateVersion7();
    private static readonly Guid ProductB = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);

    private static AdjustmentReason Reason(ReasonKind kind, bool note = false) =>
        AdjustmentReason.Create(Guid.CreateVersion7(), Company, "R_" + kind.ToString().ToUpperInvariant(), kind.ToString(), kind, note).Value;

    private static Pos.SharedKernel.Results.Result<InventoryAdjustment> Create(AdjustmentReason reason, params AdjustmentLineInput[] lines) =>
        InventoryAdjustment.Create(Guid.CreateVersion7(), Company, Guid.CreateVersion7(), Guid.CreateVersion7(), "S01-000001", new DateOnly(2026, 10, 6),
            reason, "nota", lines, Guid.CreateVersion7, Creator);

    [Fact]
    public void Las_salidas_tipificadas_se_guardan_negativas_y_el_saldo_inicial_exige_costo()
    {
        var damage = Create(Reason(ReasonKind.Damage), new AdjustmentLineInput(ProductA, 3m)).Value;
        damage.Lines.Single().Quantity.ShouldBe(-3m);

        Create(Reason(ReasonKind.InitialBalance), new AdjustmentLineInput(ProductA, 5m)).Error.ShouldBe(InventoryErrors.UnitCostRequired);
        Create(Reason(ReasonKind.InitialBalance), new AdjustmentLineInput(ProductA, -5m, 10m)).Error.ShouldBe(InventoryErrors.QuantitySignMismatch);
        var initial = Create(Reason(ReasonKind.InitialBalance), new AdjustmentLineInput(ProductA, 5m, 2_500m)).Value;
        initial.Lines.Single().UnitCost.ShouldBe(2_500m);

        var correction = Create(Reason(ReasonKind.Adjustment), new AdjustmentLineInput(ProductA, 2m, 99m, " x "), new AdjustmentLineInput(ProductB, -1m)).Value;
        correction.Lines[0].UnitCost.ShouldBeNull();
        correction.Lines[0].Notes.ShouldBe("x");
        correction.Lines[1].Quantity.ShouldBe(-1m);
    }

    [Fact]
    public void Validaciones_de_lineas_y_observacion()
    {
        var reason = Reason(ReasonKind.Adjustment);
        Create(reason).Error.ShouldBe(InventoryErrors.NoLines);
        Create(reason, new AdjustmentLineInput(ProductA, 1m), new AdjustmentLineInput(ProductA, 2m)).Error.ShouldBe(InventoryErrors.DuplicatedProduct);
        Create(reason, new AdjustmentLineInput(ProductA, 0m)).Error.ShouldBe(InventoryErrors.InvalidQuantity);
        Create(reason, new AdjustmentLineInput(ProductA, 1.00001m)).Error.ShouldBe(InventoryErrors.InvalidQuantity);

        var noteRequired = Reason(ReasonKind.Loss, note: true);
        InventoryAdjustment.Create(Guid.CreateVersion7(), Company, Guid.CreateVersion7(), Guid.CreateVersion7(), "N", new DateOnly(2026, 10, 6), noteRequired, " ",
            [new AdjustmentLineInput(ProductA, 1m)], Guid.CreateVersion7, Creator).Error.ShouldBe(InventoryErrors.NoteRequired);
    }

    [Fact]
    public void Umbral_de_aprobacion_y_quien_aprueba_no_es_quien_crea()
    {
        var adjustment = Create(Reason(ReasonKind.Adjustment), new AdjustmentLineInput(ProductA, -10m)).Value;

        adjustment.RequestPosting(600_000m, 500_000m, Now).Value.ShouldBe(PostingDecision.NeedsApproval);
        adjustment.Status.ShouldBe(AdjustmentStatus.PendingApproval);
        Should.Throw<DomainException>(() => adjustment.MarkPosted(Creator, Now));
        adjustment.Approve(Creator, Now).Error.ShouldBe(InventoryErrors.SelfApproval);

        var supervisor = Guid.CreateVersion7();
        adjustment.Approve(supervisor, Now).IsSuccess.ShouldBeTrue();
        adjustment.RequestPosting(600_000m, 500_000m, Now).Value.ShouldBe(PostingDecision.ReadyToPost);
        adjustment.MarkPosted(supervisor, Now);
        adjustment.Status.ShouldBe(AdjustmentStatus.Posted);
        adjustment.Cancel(Creator, Now).Error.ShouldBe(InventoryErrors.InvalidStatus);
        adjustment.Approve(supervisor, Now).Error.ShouldBe(InventoryErrors.InvalidStatus);
        adjustment.RequestPosting(1m, 5m, Now).Error.ShouldBe(InventoryErrors.InvalidStatus);
        adjustment.Edit(Reason(ReasonKind.Adjustment), null, [new AdjustmentLineInput(ProductA, 1m)], Guid.CreateVersion7).Error.ShouldBe(InventoryErrors.InvalidStatus);
    }

    [Fact]
    public void Bajo_el_umbral_se_publica_directo_y_se_puede_cancelar_un_borrador()
    {
        var small = Create(Reason(ReasonKind.Adjustment), new AdjustmentLineInput(ProductA, 1m)).Value;
        small.RequestPosting(1_000m, 500_000m, Now).Value.ShouldBe(PostingDecision.ReadyToPost);
        small.MarkPosted(Creator, Now);
        small.PostedBy.ShouldBe(Creator);

        var draft = Create(Reason(ReasonKind.Adjustment), new AdjustmentLineInput(ProductA, 1m)).Value;
        draft.Cancel(Creator, Now).IsSuccess.ShouldBeTrue();
        draft.Status.ShouldBe(AdjustmentStatus.Cancelled);
        draft.AuditLabel.ShouldContain("S01-000001");
    }

    [Fact]
    public void Motivos_y_politicas()
    {
        AdjustmentReason.Create(Guid.CreateVersion7(), Company, "x", "X", ReasonKind.Loss, false).IsFailure.ShouldBeTrue();
        var reason = AdjustmentReason.Create(Guid.CreateVersion7(), Company, "merma", "Merma", ReasonKind.Loss, false).Value;
        reason.Code.ShouldBe("MERMA");
        reason.IsOutboundOnly.ShouldBeTrue();
        reason.IsInboundOnly.ShouldBeFalse();
        reason.Update("Merma de fruver", true, false).IsSuccess.ShouldBeTrue();
        reason.Status.ShouldBe(MasterStatus.Inactive);
        reason.Update("", true, true).IsFailure.ShouldBeTrue();
        reason.AuditLabel.ShouldContain("MERMA");

        StockPolicy.Create(Guid.CreateVersion7(), Company, Guid.CreateVersion7(), Guid.CreateVersion7(), ProductA, 10m, 5m, null, null)
            .Error.ShouldBe(InventoryErrors.InvalidPolicy);
        var policy = StockPolicy.Create(Guid.CreateVersion7(), Company, Guid.CreateVersion7(), Guid.CreateVersion7(), ProductA, 5m, 20m, 8m, 12m).Value;
        policy.Update(1m, null, null, 0m).Error.ShouldBe(InventoryErrors.InvalidPolicy);
        policy.MinQty.ShouldBe(5m);
        policy.AuditLabel.ShouldNotBeEmpty();
    }
}

public class CountTests
{
    private static readonly Guid ProductA = Guid.CreateVersion7();
    private static readonly Guid ProductB = Guid.CreateVersion7();
    private static readonly Guid ProductC = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);

    private static InventoryCount Started()
    {
        var count = InventoryCount.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "S01-000007",
            CountType.Full, isBlind: true, "{}", " anual ");
        count.Start([(ProductA, 10m, 100L), (ProductB, 5m, 101L), (ProductC, 0m, 0L)], Guid.CreateVersion7, Now).IsSuccess.ShouldBeTrue();
        return count;
    }

    [Fact]
    public void Varios_contadores_suman_y_el_reconteo_reemplaza_la_primera_ronda()
    {
        var count = Started();
        var user = Guid.CreateVersion7();
        count.Register(ProductA, 4m, "pasillo 1", user, Now, Guid.CreateVersion7).IsSuccess.ShouldBeTrue();
        count.Register(ProductA, 5m, "bodega", user, Now, Guid.CreateVersion7).IsSuccess.ShouldBeTrue();
        count.CountedOf(ProductA).ShouldBe(9m);
        count.CountedOf(ProductC).ShouldBeNull();

        // Durante el conteo se vendió 1 unidad de A: el teórico actualizado es 9 → sin diferencia.
        count.Review(new Dictionary<Guid, decimal> { [ProductA] = 9m, [ProductB] = 5m, [ProductC] = 0m }, Now).IsSuccess.ShouldBeTrue();
        count.Lines.Single(l => l.ProductId == ProductA).Difference.ShouldBe(0m);
        count.Lines.Single(l => l.ProductId == ProductB).Difference.ShouldBeNull();

        count.Register(ProductB, 3m, null, user, Now, Guid.CreateVersion7).IsSuccess.ShouldBeTrue();
        count.Entries[^1].Round.ShouldBe((short)2);
        count.Register(ProductA, 8m, null, user, Now, Guid.CreateVersion7).IsSuccess.ShouldBeTrue();
        count.CountedOf(ProductA).ShouldBe(8m);

        var differences = count.Approve(new Dictionary<Guid, decimal> { [ProductA] = 9m, [ProductB] = 5m, [ProductC] = 0m }, uncountedAsZero: false, user, Now).Value;
        differences.ShouldBe([new CountDifference(ProductA, -1m), new CountDifference(ProductB, -2m)], ignoreOrder: true);
        count.Status.ShouldBe(CountStatus.Posted);
        count.Notes.ShouldBe("anual");
    }

    [Fact]
    public void No_contados_en_cero_si_la_empresa_lo_decide()
    {
        var count = Started();
        count.Review(new Dictionary<Guid, decimal>(), Now).IsSuccess.ShouldBeTrue();
        var differences = count.Approve(new Dictionary<Guid, decimal>(), uncountedAsZero: true, Guid.CreateVersion7(), Now).Value;

        differences.ShouldBe([new CountDifference(ProductA, -10m), new CountDifference(ProductB, -5m)], ignoreOrder: true);
    }

    [Fact]
    public void Estados_y_validaciones()
    {
        var draft = InventoryCount.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "N", CountType.Partial, false, "{}", null);
        draft.Register(ProductA, 1m, null, Guid.CreateVersion7(), Now, Guid.CreateVersion7).Error.ShouldBe(InventoryErrors.InvalidStatus);
        draft.Start([], Guid.CreateVersion7, Now).Error.ShouldBe(InventoryErrors.CountEmpty);
        draft.Review(new Dictionary<Guid, decimal>(), Now).Error.ShouldBe(InventoryErrors.InvalidStatus);
        draft.Approve(new Dictionary<Guid, decimal>(), false, Guid.CreateVersion7(), Now).Error.ShouldBe(InventoryErrors.InvalidStatus);

        var count = Started();
        count.Start([(ProductA, 1m, 1L)], Guid.CreateVersion7, Now).Error.ShouldBe(InventoryErrors.InvalidStatus);
        count.Register(Guid.CreateVersion7(), 1m, null, Guid.CreateVersion7(), Now, Guid.CreateVersion7).Error.ShouldBe(InventoryErrors.ProductNotInCount);
        count.Register(ProductA, -1m, null, Guid.CreateVersion7(), Now, Guid.CreateVersion7).Error.ShouldBe(InventoryErrors.InvalidQuantity);
        count.Cancel(Now).IsSuccess.ShouldBeTrue();
        count.Cancel(Now).Error.ShouldBe(InventoryErrors.InvalidStatus);
        count.AuditLabel.ShouldContain("S01-000007");
    }
}

public class TransferTests
{
    private static readonly Guid Origin = Guid.CreateVersion7();
    private static readonly Guid Destination = Guid.CreateVersion7();
    private static readonly Guid ProductA = Guid.CreateVersion7();
    private static readonly Guid ProductB = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);

    private static StockTransfer Create() =>
        StockTransfer.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "S01-000003", Origin, Destination, " a piso ",
            [(ProductA, 10m), (ProductB, 4m)], Guid.CreateVersion7).Value;

    [Fact]
    public void Despacho_y_recepcion_con_faltante()
    {
        var transfer = Create();
        transfer.Dispatch(new Dictionary<Guid, decimal> { [ProductA] = 2_500m }, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        transfer.Lines.Single(l => l.ProductId == ProductA).UnitCost.ShouldBe(2_500m);
        transfer.Lines.Single(l => l.ProductId == ProductB).UnitCost.ShouldBe(0m);

        transfer.Receive(new Dictionary<Guid, decimal> { [ProductA] = 11m }, Guid.CreateVersion7(), Now).Error.ShouldBe(InventoryErrors.ReceivedExceedsSent);
        transfer.Receive(new Dictionary<Guid, decimal> { [Guid.CreateVersion7()] = 1m }, Guid.CreateVersion7(), Now).Error.ShouldBe(InventoryErrors.ProductNotFound);
        transfer.Receive(new Dictionary<Guid, decimal> { [ProductA] = 9m }, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        transfer.Status.ShouldBe(TransferStatus.ReceivedWithDifferences);
        transfer.Lines.Single(l => l.ProductId == ProductB).QuantityReceived.ShouldBe(4m);
        transfer.Notes.ShouldBe("a piso");
        transfer.Cancel(Now).Error.ShouldBe(InventoryErrors.InvalidStatus);
    }

    [Fact]
    public void Recepcion_completa_y_validaciones()
    {
        var transfer = Create();
        transfer.Receive(new Dictionary<Guid, decimal>(), Guid.CreateVersion7(), Now).Error.ShouldBe(InventoryErrors.InvalidStatus);
        transfer.Dispatch(new Dictionary<Guid, decimal>(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        transfer.Dispatch(new Dictionary<Guid, decimal>(), Guid.CreateVersion7(), Now).Error.ShouldBe(InventoryErrors.InvalidStatus);
        transfer.Receive(new Dictionary<Guid, decimal>(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        transfer.Status.ShouldBe(TransferStatus.Received);

        StockTransfer.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "N", Origin, Origin, null, [(ProductA, 1m)], Guid.CreateVersion7)
            .Error.ShouldBe(InventoryErrors.SameWarehouse);
        StockTransfer.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "N", Origin, Destination, null, [], Guid.CreateVersion7)
            .Error.ShouldBe(InventoryErrors.NoLines);
        StockTransfer.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "N", Origin, Destination, null, [(ProductA, 1m), (ProductA, 2m)],
            Guid.CreateVersion7).Error.ShouldBe(InventoryErrors.DuplicatedProduct);
        StockTransfer.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "N", Origin, Destination, null, [(ProductA, 0m)],
            Guid.CreateVersion7).Error.ShouldBe(InventoryErrors.InvalidQuantity);

        var cancelled = Create();
        cancelled.Cancel(Now).IsSuccess.ShouldBeTrue();
        cancelled.Status.ShouldBe(TransferStatus.Cancelled);
        cancelled.AuditLabel.ShouldContain("S01-000003");
    }
}
