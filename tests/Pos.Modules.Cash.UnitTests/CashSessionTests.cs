using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Cash.UnitTests;

internal static class Cash
{
    public static readonly Guid Company = Guid.NewGuid();
    public static readonly Guid Branch = Guid.NewGuid();
    public static readonly Guid Terminal = Guid.NewGuid();
    public static readonly Guid Cashier = Guid.NewGuid();
    public static readonly Guid Supervisor = Guid.NewGuid();
    public static readonly Guid Efectivo = Guid.NewGuid();
    public static readonly Guid Datafono = Guid.NewGuid();
    public static readonly Guid Bill50 = Guid.NewGuid();

    // 23:30 en Bogotá (04:30 UTC del día siguiente): un turno nocturno.
    public static readonly DateTimeOffset Night = new(2026, 9, 29, 4, 30, 0, TimeSpan.Zero);
    public static readonly DateOnly OpeningDate = new(2026, 9, 28);

    public static CashSession Session(decimal openingFloat = 200_000m, bool blind = true) =>
        CashSession.Open(Guid.NewGuid(), Company, Branch, Terminal, null, Cashier, "S01C01-000001", Night, OpeningDate, openingFloat, blind).Value;

    public static CashMovement Move(CashSession session, int line, CashMovementType type, decimal amount, Guid? method = null, string? reason = "Motivo del movimiento", int? direction = null) =>
        CashMovement.Create(Guid.NewGuid(), session, line, new CashMovementData(type, method ?? Efectivo, amount, reason, CorrectionDirection: direction), Cashier, Night).Value;
}

public class CashSessionTests
{
    [Fact]
    public void El_esperado_sale_de_los_movimientos_por_medio_de_pago()
    {
        var session = Cash.Session();
        CashMovement[] movements =
        [
            Cash.Move(session, 1, CashMovementType.OpeningFloat, 200_000m),
            Cash.Move(session, 2, CashMovementType.Sale, 85_300m),
            Cash.Move(session, 3, CashMovementType.Sale, 40_000m, Cash.Datafono),
            Cash.Move(session, 4, CashMovementType.CashIn, 50_000m),
            Cash.Move(session, 5, CashMovementType.Expense, 30_000m),
            Cash.Move(session, 6, CashMovementType.SupplierPayment, 60_000m),
            Cash.Move(session, 7, CashMovementType.CashOutWithdrawal, 100_000m),
            Cash.Move(session, 8, CashMovementType.NoSaleDrawerOpen, 0m),
            Cash.Move(session, 9, CashMovementType.CustomerRefund, 5_300m),
            Cash.Move(session, 10, CashMovementType.Correction, 1_000m, direction: 1),
        ];

        var expected = CashCalculator.Expected(movements.Select(m => (m.PaymentMethodId, (int)m.Direction, m.Amount)));
        var cash = expected.Single(e => e.PaymentMethodId == Cash.Efectivo);
        cash.Expected.ShouldBe(200_000m + 85_300m + 50_000m - 30_000m - 60_000m - 100_000m - 5_300m + 1_000m);
        cash.Transactions.ShouldBe(8);
        expected.Single(e => e.PaymentMethodId == Cash.Datafono).Expected.ShouldBe(40_000m);
        movements[7].SignedAmount.ShouldBe(0m);
    }

    [Fact]
    public void Cierre_ciego_con_diferencia_exige_observacion_y_queda_para_revision()
    {
        var session = Cash.Session();
        session.BusinessDate.ShouldBe(Cash.OpeningDate);
        MethodMovements[] expected = [new(Cash.Efectivo, 150_000m, 4), new(Cash.Datafono, 40_000m, 1)];
        CountLineInput[] count =
        [
            new(Cash.Efectivo, Cash.Bill50, 50_000m, 2, null),
            new(Cash.Efectivo, null, null, null, 40_000m),
            new(Cash.Datafono, null, null, null, 40_000m),
        ];

        session.Close(Closing(count, expected, note: null), Guid.NewGuid).Error.ShouldBe(CashErrors.InvalidStatus);
        session.StartClosing(Cash.Night).IsSuccess.ShouldBeTrue();
        session.StartClosing(Cash.Night).Error.ShouldBe(CashErrors.InvalidStatus);
        session.Close(Closing(count, expected, note: null), Guid.NewGuid).Error.ShouldBe(CashErrors.DifferenceNoteRequired);

        var totals = session.Close(Closing(count, expected, note: "Faltó un billete de 10 mil"), Guid.NewGuid).Value;
        totals.Single(t => t.PaymentMethodId == Cash.Efectivo).Difference.ShouldBe(-10_000m);
        session.Status.ShouldBe(CashSessionStatus.Closed);
        session.ExpectedTotal.ShouldBe(190_000m);
        session.CountedTotal.ShouldBe(180_000m);
        session.Difference.ShouldBe(-10_000m);
        session.ReviewRequired.ShouldBeTrue();
        session.Totals.Count.ShouldBe(2);
        session.Counts.Single().Kind.ShouldBe(CashCountKind.Closing);
        session.Counts.Single().Total.ShouldBe(180_000m);
        session.BusinessDate.ShouldBe(Cash.OpeningDate);

        // Definitivo: ni se reabre ni se vuelve a cerrar; los movimientos se rechazan (RN-CSH-02, RN-CSH-08).
        session.CancelClosing().Error.ShouldBe(CashErrors.InvalidStatus);
        session.Close(Closing(count, expected, note: "x"), Guid.NewGuid).Error.ShouldBe(CashErrors.InvalidStatus);
        CashMovement.Create(Guid.NewGuid(), session, 99, new CashMovementData(CashMovementType.CashIn, Cash.Efectivo, 1m, "Cambio"), Cash.Cashier, Cash.Night)
            .Error.ShouldBe(CashErrors.SessionNotOpen);

        session.Review(Cash.Cashier, "Revisado por mí", Cash.Night).Error.ShouldBe(CashErrors.SelfReview);
        session.Review(Cash.Supervisor, "no", Cash.Night).Error.ShouldBe(CashErrors.ReasonRequired);
        session.Review(Cash.Supervisor, "Se descontará de nómina", Cash.Night).IsSuccess.ShouldBeTrue();
        session.Review(Cash.Supervisor, "Otra vez revisado", Cash.Night).Error.ShouldBe(CashErrors.InvalidStatus);

        session.SetSeal(1234, "7F3A-91C2-0B44-E1D8");
        session.ZSealNo.ShouldBe(1234);
    }

    [Fact]
    public void Cierre_cuadrado_sin_revision_y_cierre_por_supervisor()
    {
        var session = Cash.Session();
        session.StartClosing(Cash.Night);
        session.CancelClosing().IsSuccess.ShouldBeTrue();
        session.Status.ShouldBe(CashSessionStatus.Open);
        session.StartClosing(Cash.Night);
        session.Close(Closing([new(Cash.Efectivo, null, null, null, 200_000m)], [new(Cash.Efectivo, 200_000m, 1)], null), Guid.NewGuid).IsSuccess.ShouldBeTrue();
        session.ReviewRequired.ShouldBeFalse();
        session.Review(Cash.Supervisor, "Nada que revisar", Cash.Night).Error.ShouldBe(CashErrors.InvalidStatus);

        // Cajero ausente: el supervisor cierra una jornada abierta, con motivo; siempre queda para revisión.
        var forgotten = Cash.Session();
        forgotten.Close(Closing([], [new(Cash.Efectivo, 200_000m, 1)], null, bySupervisor: true, reason: null), Guid.NewGuid)
            .Error.ShouldBe(CashErrors.ReasonRequired);
        forgotten.Close(Closing([], [new(Cash.Efectivo, 200_000m, 1)], null, bySupervisor: true, reason: "El cajero se fue sin cerrar"), Guid.NewGuid)
            .IsSuccess.ShouldBeTrue();
        forgotten.ClosedBySupervisor.ShouldBeTrue();
        forgotten.ClosedBy.ShouldBe(Cash.Supervisor);
        forgotten.ReviewRequired.ShouldBeTrue();
        forgotten.Difference.ShouldBe(-200_000m);
        Should.Throw<DomainException>(() => Cash.Session().SetSeal(1, "X"));
    }

    [Fact]
    public void Movimientos_validan_motivo_valor_y_direccion()
    {
        var session = Cash.Session();
        Create(session, CashMovementType.CashOutWithdrawal, 1m, null).ShouldBe(CashErrors.ReasonRequired);
        Create(session, CashMovementType.CashIn, 0m, "Cambio del banco").ShouldBe(CashErrors.InvalidMovement);
        Create(session, CashMovementType.CashIn, 1.001m, "Cambio del banco").ShouldBe(CashErrors.InvalidMovement);
        Create(session, CashMovementType.NoSaleDrawerOpen, 5m, "Cambio a un cliente").ShouldBe(CashErrors.InvalidMovement);
        Create(session, CashMovementType.Correction, 5m, "Error del día anterior").ShouldBe(CashErrors.InvalidMovement);
        Create(session, CashMovementType.Expense, 5m, new string('x', 301)).ShouldBe(CashErrors.ReasonRequired);

        var withdrawal = Cash.Move(session, 1, CashMovementType.CashOutWithdrawal, 100_000m);
        withdrawal.Direction.ShouldBe((short)-1);
        Cash.Move(session, 2, CashMovementType.Correction, 3_000m, direction: -1).SignedAmount.ShouldBe(-3_000m);
        Cash.Move(session, 3, CashMovementType.OpeningFloat, 0m, reason: null).Amount.ShouldBe(0m);
        Should.Throw<ArgumentOutOfRangeException>(() => CashMovementRules.Direction(CashMovementType.Correction));
        CashMovementRules.RequiresReason(CashMovementType.Sale).ShouldBeFalse();
    }

    [Fact]
    public void Conteo_por_denominacion_y_por_total()
    {
        var counted = CashCalculator.Counted([
            new(Cash.Efectivo, Cash.Bill50, 50_000m, 3, null),
            new(Cash.Efectivo, Guid.NewGuid(), 1_000m, 7, null),
            new(Cash.Datafono, null, null, null, 120_500.5m),
        ]).Value;
        counted[Cash.Efectivo].ShouldBe(157_000m);
        counted[Cash.Datafono].ShouldBe(120_500.5m);

        CashCalculator.Counted([new(Cash.Efectivo, Cash.Bill50, 50_000m, 1, null), new(Cash.Efectivo, Cash.Bill50, 50_000m, 2, null)])
            .Error.ShouldBe(CashErrors.InvalidCount);
        CashCalculator.Counted([new(Cash.Efectivo, Cash.Bill50, 50_000m, -1, null)]).Error.ShouldBe(CashErrors.InvalidCount);
        CashCalculator.Counted([new(Cash.Datafono, null, null, null, -1m)]).Error.ShouldBe(CashErrors.InvalidCount);

        var session = Cash.Session();
        session.RegisterCount(CashCountKind.Opening, [new(Cash.Efectivo, Cash.Bill50, 50_000m, 4, null)], Cash.Cashier, Cash.Night, Guid.NewGuid)
            .Value.Total.ShouldBe(200_000m);
        session.RegisterCount(CashCountKind.Closing, [], Cash.Cashier, Cash.Night, Guid.NewGuid).Error.ShouldBe(CashErrors.InvalidStatus);
        session.RegisterCount(CashCountKind.Partial, [new(Cash.Datafono, null, null, null, -5m)], Cash.Cashier, Cash.Night, Guid.NewGuid)
            .Error.ShouldBe(CashErrors.InvalidCount);

        // Un medio contado sin movimientos (o con movimientos sin contar) aparece en los totales.
        var totals = CashCalculator.Totals([new(Cash.Efectivo, 10m, 1)], new Dictionary<Guid, decimal> { [Cash.Datafono] = 5m });
        totals.Count.ShouldBe(2);
        totals.Single(t => t.PaymentMethodId == Cash.Efectivo).Difference.ShouldBe(-10m);
        totals.Single(t => t.PaymentMethodId == Cash.Datafono).Difference.ShouldBe(5m);
    }

    [Fact]
    public void Apertura_y_denominaciones_validas()
    {
        CashSession.Open(Guid.NewGuid(), Cash.Company, Cash.Branch, Cash.Terminal, null, Cash.Cashier, "N", Cash.Night, Cash.OpeningDate, -1m, true)
            .Error.ShouldBe(CashErrors.InvalidMovement);
        var denomination = Denomination.Create(Guid.NewGuid(), Cash.Company, "cop", 50_000m, DenominationKind.Bill, 20).Value;
        denomination.CurrencyCode.ShouldBe("COP");
        denomination.AuditLabel.ShouldContain("COP");
        denomination.SetActive(false);
        denomination.Status.ShouldBe(MasterStatus.Inactive);
        Denomination.Create(Guid.NewGuid(), Cash.Company, "COP", 0m, DenominationKind.Coin, 1).Error.ShouldBe(CashErrors.InvalidDenomination);
        Denomination.Create(Guid.NewGuid(), Cash.Company, "PESOS", 50m, DenominationKind.Coin, 1).Error.ShouldBe(CashErrors.InvalidDenomination);
    }

    [Fact]
    public void Reporte_Z_de_80_mm_con_sello()
    {
        var session = Cash.Session();
        session.StartClosing(Cash.Night);
        session.Close(Closing([new(Cash.Efectivo, null, null, null, 190_000m)], [new(Cash.Efectivo, 200_000m, 2)], "Faltante en monedas"), Guid.NewGuid);
        session.SetSeal(1234, "7F3A-91C2-0B44-E1D8");
        CashMovementDto[] movements =
        [
            new(Guid.NewGuid(), 1, "OPENING_FLOAT", Cash.Efectivo, "Efectivo", 1, 200_000m, "Fondo inicial", null, null, null, Cash.Cashier, "Ana", null, null, Cash.Night),
            new(Guid.NewGuid(), 2, "CASH_OUT_WITHDRAWAL", Cash.Efectivo, "Efectivo", -1, 50_000m, "Sangría a la caja fuerte", null, null, null, Cash.Cashier, "Ana",
                Cash.Supervisor, "Carlos Supervisor", Cash.Night),
            new(Guid.NewGuid(), 3, "NO_SALE_DRAWER_OPEN", Cash.Efectivo, "Efectivo", 0, 0m, "Cambio", null, null, null, Cash.Cashier, "Ana", Cash.Supervisor, "Carlos", Cash.Night),
        ];
        CashMethodTotalDto[] totals = [new(Cash.Efectivo, "EFECTIVO", "Efectivo", true, 200_000m, 190_000m, -10_000m, 2)];
        var report = CashReports.Build("Z", session, new SessionHeader("Súper La Esquina", "Centro", "C01", "Ana Cajera"), movements, totals, Cash.Night, new BogotaClock());

        report.NoSaleOpenings.ShouldBe(1);
        report.Withdrawals.Single().AuthorizedByName.ShouldBe("Carlos Supervisor");
        report.Difference.ShouldBe(-10_000m);
        report.Text.ShouldContain("REPORTE Z - CIERRE DE CAJA");
        report.Text.ShouldContain("SELLO #1234 · 7F3A-91C2-0B44-E1D8");
        report.Text.ShouldContain("Fecha de negocio");
        report.Text.ShouldContain("2026-09-28");
        report.Text.ShouldContain("Faltante en monedas");
        report.Text.Split('\n').ShouldAllBe(l => l.Length <= CashReports.Width);

        var x = CashReports.Build("X", Cash.Session(), new SessionHeader("S", "B", "C01", "Ana"), [], [new(Cash.Efectivo, "EFECTIVO", "Efectivo", true, null, null, null, 0)],
            Cash.Night, new BogotaClock());
        x.Text.ShouldContain("REPORTE X - PARCIAL");
        x.Text.ShouldContain("***");
        x.Text.ShouldNotContain("SELLO");
        CashReports.Label("SUPPLIER_PAYMENT").ShouldBe("Pagos a proveedor");
        CashReports.Label("OTRO").ShouldBe("OTRO");
    }

    [Fact]
    public void Configuraciones_y_permisos_publicados()
    {
        CashSettings.BlindCount.DefaultValue.ShouldBeTrue();
        CashSettings.DifferenceThreshold.DefaultValue.ShouldBe(5_000m);
        CashSettings.MaxCashInDrawer.DefaultValue.ShouldBe(2_000_000m);
        CashSettings.OpenSessionAlertHours.DefaultValue.ShouldBe(14);
        new CashSettingsProvider().GetDefinitions().Count().ShouldBe(4);
        new CashPermissionCatalog().GetPermissions().Count().ShouldBe(7);
    }

    private static SessionClosing Closing(
        IReadOnlyList<CountLineInput> count, IReadOnlyList<MethodMovements> expected, string? note, bool bySupervisor = false, string? reason = null) =>
        new(count, expected, 5_000m, note, bySupervisor ? Cash.Supervisor : Cash.Cashier, bySupervisor, reason, Cash.Night);

    private static Pos.SharedKernel.Results.Error Create(CashSession session, CashMovementType type, decimal amount, string? reason) =>
        CashMovement.Create(Guid.NewGuid(), session, 1, new CashMovementData(type, Cash.Efectivo, amount, reason), Cash.Cashier, Cash.Night).Error;

    private sealed class BogotaClock : IClock
    {
        public DateTimeOffset UtcNow => Cash.Night;

        public TimeZoneInfo BusinessTimeZone => BusinessTimeZones.Colombia;
    }
}
