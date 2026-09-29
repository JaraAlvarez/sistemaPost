using Pos.Modules.Promotions.Domain;

namespace Pos.Modules.Promotions.UnitTests;

public class PromotionTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid BranchA = Guid.NewGuid();
    private static readonly Guid BranchB = Guid.NewGuid();
    private static readonly DateTimeOffset From = new(2026, 9, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 17, 0, 0, TimeSpan.Zero);

    // 2026-09-28 es lunes (hora de Colombia).
    private static readonly DateTime MondayNoon = new(2026, 9, 28, 12, 0, 0);

    private static PromotionItemInput Product(decimal quantity = 1m) => new(Guid.NewGuid(), Guid.NewGuid(), null, null, quantity);

    private static PromotionInput Input(
        PromotionType type = PromotionType.MultiBuy,
        PromotionRuleInput? rule = null,
        IReadOnlyList<PromotionItemInput>? items = null,
        string name = " Lleve 3 pague 2 ",
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        PromotionDays days = PromotionDays.All,
        TimeOnly? start = null,
        TimeOnly? end = null,
        IReadOnlyList<Guid>? branches = null,
        int? max = null,
        string? ticket = " 3x2 gaseosas ") =>
        new(
            name,
            type,
            from ?? From,
            to,
            days,
            start,
            end,
            branches ?? [],
            max,
            ticket,
            rule ?? new PromotionRuleInput(3, 2, null, null, null),
            items ?? [Product()]);

    private static Promotion Create(PromotionInput input) => Promotion.Create(Guid.NewGuid(), Company, "PRM-000001", input, Guid.NewGuid).Value;

    private static Promotion Active(PromotionInput input)
    {
        var promotion = Create(input);
        promotion.Activate(User, Now).IsSuccess.ShouldBeTrue();
        return promotion;
    }

    [Fact]
    public void Creacion_valida_en_borrador()
    {
        var id = Guid.NewGuid();
        var input = Input(to: To, max: 5);
        var promotion = Promotion.Create(id, Company, "PRM-000001", input, Guid.NewGuid).Value;

        promotion.Id.ShouldBe(id);
        promotion.CompanyId.ShouldBe(Company);
        promotion.Number.ShouldBe("PRM-000001");
        promotion.Name.ShouldBe("Lleve 3 pague 2");
        promotion.TicketText.ShouldBe("3x2 gaseosas");
        promotion.Type.ShouldBe(PromotionType.MultiBuy);
        promotion.Status.ShouldBe(PromotionStatus.Draft);
        promotion.ValidFrom.ShouldBe(From);
        promotion.ValidTo.ShouldBe(To);
        promotion.Days.ShouldBe(PromotionDays.All);
        promotion.StartTime.ShouldBeNull();
        promotion.EndTime.ShouldBeNull();
        promotion.AllBranches.ShouldBeTrue();
        promotion.Branches.ShouldBeEmpty();
        promotion.MaxApplications.ShouldBe(5);
        promotion.BuyQuantity.ShouldBe(3);
        promotion.PayQuantity.ShouldBe(2);
        promotion.Price.ShouldBeNull();
        promotion.Percent.ShouldBeNull();
        promotion.MinQuantity.ShouldBeNull();
        promotion.ActivatedAt.ShouldBeNull();
        promotion.ActivatedBy.ShouldBeNull();
        promotion.EndedAt.ShouldBeNull();
        promotion.AuditLabel.ShouldBe("Promoción PRM-000001 · Lleve 3 pague 2");

        var item = promotion.Items.ShouldHaveSingleItem();
        item.ProductId.ShouldBe(input.Items[0].ProductId);
        item.PackagingId.ShouldBe(input.Items[0].PackagingId);
        item.CategoryId.ShouldBeNull();
        item.BrandId.ShouldBeNull();
        item.Quantity.ShouldBe(1m);
    }

    [Fact]
    public void Texto_de_tiquete_en_blanco_queda_nulo() =>
        Create(Input(ticket: "   ")).TicketText.ShouldBeNull();

    [Fact]
    public void Item_por_categoria_o_marca_ignora_la_presentacion()
    {
        var category = new PromotionItemInput(null, Guid.NewGuid(), Guid.NewGuid(), null);
        var brand = new PromotionItemInput(null, null, null, Guid.NewGuid(), 2.5m);
        var promotion = Create(Input(PromotionType.PercentOff, new PromotionRuleInput(null, null, null, 10m, null), [category, brand]));

        promotion.Items[0].CategoryId.ShouldBe(category.CategoryId);
        promotion.Items[0].PackagingId.ShouldBeNull();
        promotion.Items[1].BrandId.ShouldBe(brand.BrandId);
        promotion.Items[1].Quantity.ShouldBe(2.5m);
    }

    [Fact]
    public void Parametros_se_guardan_segun_el_tipo()
    {
        var full = new PromotionRuleInput(3, 2, 5_000m, 15m, 6m);

        var special = Create(Input(PromotionType.SpecialPrice, full));
        (special.BuyQuantity, special.PayQuantity, special.Price, special.Percent, special.MinQuantity).ShouldBe((null, null, 5_000m, null, null));

        var percent = Create(Input(PromotionType.PercentOff, full));
        (percent.BuyQuantity, percent.PayQuantity, percent.Price, percent.Percent, percent.MinQuantity).ShouldBe((null, null, null, 15m, null));

        var quantity = Create(Input(PromotionType.QuantityPrice, full));
        (quantity.BuyQuantity, quantity.PayQuantity, quantity.Price, quantity.Percent, quantity.MinQuantity).ShouldBe((null, null, 5_000m, null, 6m));

        var combo = Create(Input(PromotionType.Combo, full, [Product(), Product(2m)]));
        (combo.BuyQuantity, combo.PayQuantity, combo.Price, combo.Percent, combo.MinQuantity).ShouldBe((null, null, 5_000m, null, null));
        combo.Items.Count.ShouldBe(2);

        var multi = Create(Input(PromotionType.MultiBuy, full));
        (multi.BuyQuantity, multi.PayQuantity, multi.Price, multi.Percent, multi.MinQuantity).ShouldBe((3, 2, null, null, null));
    }

    public static TheoryData<PromotionType, PromotionRuleInput?, bool> Rules => new()
    {
        { PromotionType.MultiBuy, new(3, 2, null, null, null), true },
        { PromotionType.MultiBuy, new(2, 0, null, null, null), true },
        { PromotionType.MultiBuy, new(100, 99, null, null, null), true },
        { PromotionType.MultiBuy, new(1, 0, null, null, null), false },
        { PromotionType.MultiBuy, new(101, 1, null, null, null), false },
        { PromotionType.MultiBuy, new(3, 3, null, null, null), false },
        { PromotionType.MultiBuy, new(3, 4, null, null, null), false },
        { PromotionType.MultiBuy, new(3, -1, null, null, null), false },
        { PromotionType.MultiBuy, new(null, 2, null, null, null), false },
        { PromotionType.MultiBuy, new(3, null, null, null, null), false },
        { PromotionType.MultiBuy, null, false },
        { PromotionType.SpecialPrice, new(null, null, 4_990m, null, null), true },
        { PromotionType.SpecialPrice, new(null, null, 4_990.5m, null, null), true },
        { PromotionType.SpecialPrice, new(null, null, 0m, null, null), false },
        { PromotionType.SpecialPrice, new(null, null, -1m, null, null), false },
        { PromotionType.SpecialPrice, new(null, null, 1.001m, null, null), false },
        { PromotionType.SpecialPrice, new(null, null, null, null, null), false },
        { PromotionType.PercentOff, new(null, null, null, 0.01m, null), true },
        { PromotionType.PercentOff, new(null, null, null, 100m, null), true },
        { PromotionType.PercentOff, new(null, null, null, 0m, null), false },
        { PromotionType.PercentOff, new(null, null, null, 100.01m, null), false },
        { PromotionType.PercentOff, new(null, null, null, 10.005m, null), false },
        { PromotionType.PercentOff, new(null, null, null, null, null), false },
        { PromotionType.QuantityPrice, new(null, null, 2_500m, null, 6m), true },
        { PromotionType.QuantityPrice, new(null, null, 2_500m, null, 0m), false },
        { PromotionType.QuantityPrice, new(null, null, 2_500m, null, null), false },
        { PromotionType.QuantityPrice, new(null, null, 0m, null, 6m), false },
        { PromotionType.QuantityPrice, new(null, null, 2_500.123m, null, 6m), false },
        { PromotionType.Combo, new(null, null, 0m, null, null), false },
        { PromotionType.Combo, new(null, null, 9_900.001m, null, null), false },
        { (PromotionType)99, new(3, 2, 1m, 1m, 1m), false },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Validacion_de_parametros_por_tipo(PromotionType type, PromotionRuleInput? rule, bool valid)
    {
        var input = Input(type) with { Rule = rule! };
        var result = Promotion.Create(Guid.NewGuid(), Company, "PRM-1", input, Guid.NewGuid);
        if (valid)
        {
            result.IsSuccess.ShouldBeTrue();
        }
        else
        {
            result.Error.ShouldBe(PromotionErrors.InvalidRule);
        }
    }

    [Fact]
    public void Combo_exige_dos_componentes_de_producto()
    {
        var rule = new PromotionRuleInput(null, null, 9_900m, null, null);
        Promotion.Create(Guid.NewGuid(), Company, "N", Input(PromotionType.Combo, rule, [Product(), Product()]), Guid.NewGuid).IsSuccess.ShouldBeTrue();
        Promotion.Create(Guid.NewGuid(), Company, "N", Input(PromotionType.Combo, rule, [Product()]), Guid.NewGuid).Error.ShouldBe(PromotionErrors.InvalidRule);
        var withCategory = new PromotionItemInput(null, null, Guid.NewGuid(), null);
        Promotion.Create(Guid.NewGuid(), Company, "N", Input(PromotionType.Combo, rule, [Product(), withCategory]), Guid.NewGuid)
            .Error.ShouldBe(PromotionErrors.InvalidRule);
    }

    public static TheoryData<string, PromotionInput> InvalidInputs
    {
        get
        {
            var branch = Guid.NewGuid();
            return new()
            {
                { "nombre corto", Input(name: " ab ") },
                { "nombre nulo", Input(name: null!) },
                { "nombre largo", Input(name: new string('x', 81)) },
                { "tiquete largo", Input(ticket: new string('x', 41)) },
                { "vigencia al revés", Input(to: From) },
                { "vigencia anterior", Input(to: From.AddDays(-1)) },
                { "solo hora inicial", Input(start: new TimeOnly(8, 0)) },
                { "solo hora final", Input(end: new TimeOnly(8, 0)) },
                { "horario vacío", Input(start: new TimeOnly(8, 0), end: new TimeOnly(8, 0)) },
                { "sin días", Input(days: PromotionDays.None) },
                { "días fuera de la máscara", Input(days: (PromotionDays)128) },
                { "máximo cero", Input(max: 0) },
                { "máximo negativo", Input(max: -3) },
                { "sin ítems", Input(items: []) },
                { "ítems nulos", Input() with { Items = null! } },
                { "ítem sin destino", Input(items: [new PromotionItemInput(null, Guid.NewGuid(), null, null)]) },
                { "ítem con dos destinos", Input(items: [new PromotionItemInput(Guid.NewGuid(), null, Guid.NewGuid(), null)]) },
                { "ítem con tres destinos", Input(items: [new PromotionItemInput(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid())]) },
                { "cantidad cero", Input(items: [Product(0m)]) },
                { "cantidad negativa", Input(items: [Product(-1m)]) },
                { "cantidad con más de 4 decimales", Input(items: [Product(1.00001m)]) },
                { "sucursal repetida", Input(branches: [branch, branch]) },
            };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void Datos_invalidos(string caso, PromotionInput input)
    {
        caso.ShouldNotBeEmpty();
        Promotion.Create(Guid.NewGuid(), Company, "N", input, Guid.NewGuid).Error.ShouldBe(PromotionErrors.Invalid);
    }

    [Fact]
    public void Limites_validos()
    {
        Promotion.Create(Guid.NewGuid(), Company, "N", Input(name: "abc", ticket: new string('x', 40), max: 1, items: [Product(0.0001m)]), Guid.NewGuid)
            .IsSuccess.ShouldBeTrue();
        Promotion.Create(Guid.NewGuid(), Company, "N", Input(name: new string('x', 80), days: PromotionDays.Sunday, to: From.AddTicks(1)), Guid.NewGuid)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Dias_fuera_de_la_mascara_se_descartan()
    {
        var promotion = Create(Input(days: PromotionDays.Monday | (PromotionDays)128));
        promotion.Days.ShouldBe(PromotionDays.Monday);
    }

    [Fact]
    public void Argumentos_nulos()
    {
        Should.Throw<ArgumentNullException>(() => Promotion.Create(Guid.NewGuid(), Company, "N", null!, Guid.NewGuid));
        Should.Throw<ArgumentNullException>(() => Promotion.Create(Guid.NewGuid(), Company, "N", Input(), null!));
    }

    [Fact]
    public void Borrador_se_edita_y_reemplaza_items_y_sucursales()
    {
        var promotion = Create(Input(branches: [BranchA]));
        promotion.AllBranches.ShouldBeFalse();
        promotion.Branches.ShouldHaveSingleItem().BranchId.ShouldBe(BranchA);

        var items = new[] { Product(), Product() };
        var update = Input(PromotionType.PercentOff, new PromotionRuleInput(null, null, null, 20m, null), items, name: "Veinte por ciento", branches: [BranchA, BranchB]);
        promotion.Update(update, Guid.NewGuid).IsSuccess.ShouldBeTrue();

        promotion.Name.ShouldBe("Veinte por ciento");
        promotion.Type.ShouldBe(PromotionType.PercentOff);
        promotion.Percent.ShouldBe(20m);
        promotion.BuyQuantity.ShouldBeNull();
        promotion.Items.Select(i => i.ProductId).ShouldBe(items.Select(i => i.ProductId));
        promotion.Branches.Select(b => b.BranchId).ShouldBe([BranchA, BranchB]);
        promotion.Items.Select(i => i.Id).Concat(promotion.Branches.Select(b => b.Id)).Distinct().Count().ShouldBe(4);

        promotion.Update(Input(name: "x"), Guid.NewGuid).Error.ShouldBe(PromotionErrors.Invalid);
        promotion.Name.ShouldBe("Veinte por ciento");
    }

    [Fact]
    public void Transiciones_borrador_activa_pausada_terminada()
    {
        var promotion = Create(Input(to: To));
        promotion.Pause().Error.ShouldBe(PromotionErrors.InvalidStatus);

        promotion.Activate(User, Now).IsSuccess.ShouldBeTrue();
        promotion.Status.ShouldBe(PromotionStatus.Active);
        promotion.ActivatedAt.ShouldBe(Now);
        promotion.ActivatedBy.ShouldBe(User);
        promotion.Activate(User, Now).Error.ShouldBe(PromotionErrors.InvalidStatus);
        promotion.Update(Input(), Guid.NewGuid).Error.ShouldBe(PromotionErrors.NotEditable);

        promotion.Pause().IsSuccess.ShouldBeTrue();
        promotion.Status.ShouldBe(PromotionStatus.Paused);
        promotion.Pause().Error.ShouldBe(PromotionErrors.InvalidStatus);
        promotion.Update(Input(), Guid.NewGuid).Error.ShouldBe(PromotionErrors.NotEditable);

        // Reactivar conserva quién y cuándo la activó por primera vez.
        var later = Now.AddHours(2);
        promotion.Activate(Guid.NewGuid(), later).IsSuccess.ShouldBeTrue();
        promotion.ActivatedAt.ShouldBe(Now);
        promotion.ActivatedBy.ShouldBe(User);

        promotion.End(later).IsSuccess.ShouldBeTrue();
        promotion.Status.ShouldBe(PromotionStatus.Ended);
        promotion.EndedAt.ShouldBe(later);
        promotion.End(later).Error.ShouldBe(PromotionErrors.InvalidStatus);
        promotion.Activate(User, later).Error.ShouldBe(PromotionErrors.InvalidStatus);
        promotion.Pause().Error.ShouldBe(PromotionErrors.InvalidStatus);
        promotion.Update(Input(), Guid.NewGuid).Error.ShouldBe(PromotionErrors.NotEditable);
    }

    [Fact]
    public void Borrador_y_pausada_se_pueden_terminar()
    {
        var draft = Create(Input());
        draft.End(Now).IsSuccess.ShouldBeTrue();
        draft.Status.ShouldBe(PromotionStatus.Ended);

        var paused = Active(Input());
        paused.Pause().IsSuccess.ShouldBeTrue();
        paused.End(Now).IsSuccess.ShouldBeTrue();
        paused.Status.ShouldBe(PromotionStatus.Ended);
    }

    [Fact]
    public void No_se_activa_con_la_vigencia_vencida()
    {
        var promotion = Create(Input(to: Now));
        promotion.Activate(User, Now).Error.ShouldBe(PromotionErrors.Expired);
        promotion.Activate(User, Now.AddMinutes(1)).Error.ShouldBe(PromotionErrors.Expired);
        promotion.Status.ShouldBe(PromotionStatus.Draft);
        promotion.Activate(User, Now.AddTicks(-1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Solo_rige_activa()
    {
        var promotion = Create(Input());
        promotion.IsInEffect(BranchA, Now, MondayNoon).ShouldBeFalse();
        promotion.Activate(User, Now).IsSuccess.ShouldBeTrue();
        promotion.IsInEffect(BranchA, Now, MondayNoon).ShouldBeTrue();
        promotion.Pause().IsSuccess.ShouldBeTrue();
        promotion.IsInEffect(BranchA, Now, MondayNoon).ShouldBeFalse();
        promotion.End(Now).IsSuccess.ShouldBeTrue();
        promotion.IsInEffect(BranchA, Now, MondayNoon).ShouldBeFalse();
    }

    [Fact]
    public void Vigencia_desde_inclusiva_hasta_exclusiva()
    {
        var promotion = Active(Input(to: To));
        promotion.IsInEffect(BranchA, From.AddTicks(-1), MondayNoon).ShouldBeFalse();
        promotion.IsInEffect(BranchA, From, MondayNoon).ShouldBeTrue();
        promotion.IsInEffect(BranchA, To.AddTicks(-1), MondayNoon).ShouldBeTrue();
        promotion.IsInEffect(BranchA, To, MondayNoon).ShouldBeFalse();

        var open = Active(Input());
        open.IsInEffect(BranchA, From.AddYears(5), MondayNoon).ShouldBeTrue();
    }

    [Fact]
    public void Sucursales_elegidas()
    {
        var promotion = Active(Input(branches: [BranchA]));
        promotion.IsInEffect(BranchA, Now, MondayNoon).ShouldBeTrue();
        promotion.IsInEffect(BranchB, Now, MondayNoon).ShouldBeFalse();

        var all = Active(Input());
        all.IsInEffect(BranchB, Now, MondayNoon).ShouldBeTrue();
        all.IsInEffect(Guid.NewGuid(), Now, MondayNoon).ShouldBeTrue();
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, PromotionDays.Monday)]
    [InlineData(DayOfWeek.Tuesday, PromotionDays.Tuesday)]
    [InlineData(DayOfWeek.Wednesday, PromotionDays.Wednesday)]
    [InlineData(DayOfWeek.Thursday, PromotionDays.Thursday)]
    [InlineData(DayOfWeek.Friday, PromotionDays.Friday)]
    [InlineData(DayOfWeek.Saturday, PromotionDays.Saturday)]
    [InlineData(DayOfWeek.Sunday, PromotionDays.Sunday)]
    public void Bandera_de_cada_dia(DayOfWeek day, PromotionDays flag) => Promotion.DayFlag(day).ShouldBe(flag);

    [Fact]
    public void Dias_de_la_semana()
    {
        MondayNoon.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        var weekend = Active(Input(days: PromotionDays.Saturday | PromotionDays.Sunday));
        for (var offset = 0; offset < 7; offset++)
        {
            var local = MondayNoon.AddDays(offset);
            var expected = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            weekend.IsInEffect(BranchA, Now, local).ShouldBe(expected, local.DayOfWeek.ToString());
        }
    }

    [Theory]
    [InlineData(7, 59, false)]
    [InlineData(8, 0, true)]
    [InlineData(12, 0, true)]
    [InlineData(13, 59, true)]
    [InlineData(14, 0, false)]
    [InlineData(23, 0, false)]
    public void Horario_diurno_inicio_inclusivo_fin_exclusivo(int hour, int minute, bool expected)
    {
        var promotion = Active(Input(start: new TimeOnly(8, 0), end: new TimeOnly(14, 0)));
        promotion.IsInEffect(BranchA, Now, new DateTime(2026, 9, 28, hour, minute, 0)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(0, 0, true)]
    [InlineData(1, 30, true)]
    [InlineData(5, 59, true)]
    [InlineData(6, 0, false)]
    [InlineData(12, 0, false)]
    public void Horario_nocturno_cruza_la_medianoche(int hour, int minute, bool expected)
    {
        var promotion = Active(Input(start: new TimeOnly(22, 0), end: new TimeOnly(6, 0)));
        promotion.StartTime.ShouldBe(new TimeOnly(22, 0));
        promotion.EndTime.ShouldBe(new TimeOnly(6, 0));
        promotion.IsInEffect(BranchA, Now, new DateTime(2026, 9, 28, hour, minute, 0)).ShouldBe(expected);
    }

    [Fact]
    public void Horario_nocturno_usa_el_dia_local_de_la_madrugada()
    {
        // Solo los viernes de 22:00 a 06:00: la madrugada del sábado cae en sábado, que no está marcado.
        var promotion = Active(Input(days: PromotionDays.Friday, start: new TimeOnly(22, 0), end: new TimeOnly(6, 0)));
        promotion.IsInEffect(BranchA, Now, new DateTime(2026, 10, 2, 23, 0, 0)).ShouldBeTrue();
        promotion.IsInEffect(BranchA, Now, new DateTime(2026, 10, 2, 2, 0, 0)).ShouldBeTrue();
        promotion.IsInEffect(BranchA, Now, new DateTime(2026, 10, 3, 2, 0, 0)).ShouldBeFalse();
    }

    [Fact]
    public void Todas_las_condiciones_juntas()
    {
        var promotion = Active(Input(to: To, days: PromotionDays.Monday, start: new TimeOnly(9, 0), end: new TimeOnly(18, 0), branches: [BranchB]));
        promotion.IsInEffect(BranchB, Now, MondayNoon).ShouldBeTrue();
        promotion.IsInEffect(BranchA, Now, MondayNoon).ShouldBeFalse();
        promotion.IsInEffect(BranchB, Now, MondayNoon.AddDays(1)).ShouldBeFalse();
        promotion.IsInEffect(BranchB, Now, MondayNoon.AddHours(7)).ShouldBeFalse();
        promotion.IsInEffect(BranchB, To, MondayNoon).ShouldBeFalse();
    }

    [Fact]
    public void Errores_publicados()
    {
        PromotionErrors.NotFound.Code.ShouldBe("PROMOTIONS.NOT_FOUND");
        PromotionErrors.Invalid.Code.ShouldBe("PROMOTIONS.INVALID");
        PromotionErrors.InvalidRule.Code.ShouldBe("PROMOTIONS.INVALID_RULE");
        PromotionErrors.NotEditable.Code.ShouldBe("PROMOTIONS.NOT_EDITABLE");
        PromotionErrors.InvalidStatus.Code.ShouldBe("PROMOTIONS.INVALID_STATUS");
        PromotionErrors.Expired.Code.ShouldBe("PROMOTIONS.EXPIRED");
    }
}
