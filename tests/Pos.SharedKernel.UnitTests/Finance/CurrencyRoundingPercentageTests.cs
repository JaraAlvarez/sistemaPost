using Pos.SharedKernel.Finance;

namespace Pos.SharedKernel.UnitTests.Finance;

public class CurrencyTests
{
    [Fact]
    public void Cop_tiene_dos_decimales_ISO()
    {
        Currency.Cop.Code.ShouldBe("COP");
        Currency.Cop.Decimals.ShouldBe(2);
        Currency.Cop.ToString().ShouldBe("COP");
    }

    [Fact]
    public void Create_valida_codigo_y_decimales()
    {
        Currency.Create("EUR", 2).ShouldBe(Currency.Create("EUR", 2));
        Should.Throw<ArgumentException>(() => Currency.Create("eur", 2));
        Should.Throw<ArgumentException>(() => Currency.Create("EURO", 2));
        Should.Throw<ArgumentException>(() => Currency.Create(" ", 2));
        Should.Throw<ArgumentOutOfRangeException>(() => Currency.Create("EUR", -1));
        Should.Throw<ArgumentOutOfRangeException>(() => Currency.Create("EUR", 5));
    }
}

public class RoundingPolicyTests
{
    [Fact]
    public void Politica_Colombia()
    {
        RoundingPolicy.Colombia.MoneyDecimals.ShouldBe(2);
        RoundingPolicy.Colombia.CashIncrement.ShouldBe(50m);
        RoundingPolicy.Colombia.Mode.ShouldBe(MidpointRounding.AwayFromZero);
    }

    [Fact]
    public void Cantidades_y_costos_usan_cuatro_decimales()
    {
        RoundingPolicy.Colombia.RoundQuantity(0.74255m).ShouldBe(0.7426m);
        RoundingPolicy.Colombia.RoundUnitCost(2_940.12345m).ShouldBe(2_940.1235m);
    }

    [Fact]
    public void Politica_personalizada()
    {
        var policy = new RoundingPolicy(moneyDecimals: 0, cashIncrement: 100m, mode: MidpointRounding.ToEven);

        policy.RoundMoney(2.5m).ShouldBe(2m);
        policy.RoundToCash(250m).ShouldBe(200m);
    }

    [Fact]
    public void Constructor_valida_argumentos()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RoundingPolicy(-1, 50m));
        Should.Throw<ArgumentOutOfRangeException>(() => new RoundingPolicy(5, 50m));
        Should.Throw<ArgumentOutOfRangeException>(() => new RoundingPolicy(2, 0m));
    }
}

public class PercentageTests
{
    [Fact]
    public void IVA_19_por_ciento()
    {
        var iva = Percentage.Of(19m);

        iva.Value.ShouldBe(19m);
        iva.Fraction.ShouldBe(0.19m);
        iva.ApplyTo(1_000m).ShouldBe(190m);
        iva.ToString().ShouldBe("19%");
    }

    [Fact]
    public void Constantes()
    {
        Percentage.Zero.ApplyTo(500m).ShouldBe(0m);
        Percentage.OneHundred.ApplyTo(500m).ShouldBe(500m);
    }

    [Fact]
    public void Valida_negativos_y_decimales()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Percentage.Of(-1m));
        Should.Throw<ArgumentException>(() => Percentage.Of(12.34567m));
        Percentage.Of(12.3456m).Value.ShouldBe(12.3456m);
    }
}
