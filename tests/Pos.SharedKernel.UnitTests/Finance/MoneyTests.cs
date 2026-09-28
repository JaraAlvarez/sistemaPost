using Pos.SharedKernel.Finance;

namespace Pos.SharedKernel.UnitTests.Finance;

public class MoneyTests
{
    private static readonly RoundingPolicy Policy = RoundingPolicy.Colombia;

    private static Money Cop(decimal amount) => Money.Of(amount, Currency.Cop);

    [Fact]
    public void Suma_y_resta_de_la_misma_moneda_son_exactas()
    {
        (Cop(0.1m) + Cop(0.2m)).Amount.ShouldBe(0.3m);
        (Cop(100_000m) - Cop(50_000m)).Amount.ShouldBe(50_000m);
        Money.Sum([Cop(1_000m), Cop(2_500m), Cop(0.5m)], Currency.Cop).Amount.ShouldBe(3_500.5m);
    }

    [Fact]
    public void Operar_monedas_distintas_lanza_excepcion()
    {
        var usd = Money.Of(10m, Currency.Usd);
        Should.Throw<InvalidOperationException>(() => Cop(10m) + usd);
        Should.Throw<InvalidOperationException>(() => Cop(10m) - usd);
        Should.Throw<InvalidOperationException>(() => Cop(10m) < usd);
    }

    [Fact]
    public void Multiplicar_conserva_la_precision_sin_redondear()
    {
        (Cop(4_033.61m) * 0.19m).Amount.ShouldBe(766.3859m);
    }

    [Theory]
    [InlineData(1234.565, 1234.57)]
    [InlineData(1234.564, 1234.56)]
    [InlineData(-1234.565, -1234.57)]
    [InlineData(0.005, 0.01)]
    public void Round_usa_dos_decimales_y_punto_medio_lejos_de_cero(decimal value, decimal expected)
    {
        Cop(value).Round(Policy).Amount.ShouldBe(expected);
    }

    [Theory]
    [InlineData(4820, 4800)]
    [InlineData(4825, 4850)]
    [InlineData(4874.99, 4850)]
    [InlineData(4875, 4900)]
    [InlineData(25, 50)]
    [InlineData(24.99, 0)]
    public void RoundToCash_redondea_al_multiplo_de_50_pesos(decimal value, decimal expected)
    {
        Cop(value).RoundToCash(Policy).Amount.ShouldBe(expected);
    }

    [Fact]
    public void Precio_con_IVA_incluido_se_descompone_sin_perder_centavos()
    {
        // Precio exhibido $4.800 con IVA 19 % incluido.
        var price = Cop(4_800m);
        var taxBase = Cop(price.Amount / 1.19m).Round(Policy);
        var tax = (taxBase * 0.19m).Round(Policy);

        taxBase.Amount.ShouldBe(4_033.61m);
        tax.Amount.ShouldBe(766.39m);
        (taxBase + tax).ShouldBe(price);
    }

    [Fact]
    public void Allocate_reparte_sin_perder_ni_crear_centavos()
    {
        var shares = Cop(100m).Allocate([1m, 1m, 1m], Policy);

        shares.Select(s => s.Amount).ShouldBe([33.34m, 33.33m, 33.33m]);
        Money.Sum(shares, Currency.Cop).Amount.ShouldBe(100m);
    }

    [Fact]
    public void Allocate_prorratea_descuento_global_segun_valor_de_lineas()
    {
        var discount = Cop(1_000m);
        var lineTotals = new[] { 4_800m, 2_500m, 12_700m };

        var shares = discount.Allocate(lineTotals, Policy);

        shares.Select(s => s.Amount).ShouldBe([240m, 125m, 635m]);
        Money.Sum(shares, Currency.Cop).ShouldBe(discount);
    }

    [Fact]
    public void Allocate_con_decimales_asigna_residuos_a_los_mayores()
    {
        var shares = Cop(10m).Allocate([3m, 3m, 1m], Policy);

        Money.Sum(shares, Currency.Cop).Amount.ShouldBe(10m);
        shares.Select(s => s.Amount).ShouldBe([4.29m, 4.28m, 1.43m]);
    }

    [Fact]
    public void Allocate_de_importe_negativo_conserva_el_signo()
    {
        var shares = Cop(-100m).Allocate([1m, 1m, 1m], Policy);

        shares.Select(s => s.Amount).ShouldBe([-33.34m, -33.33m, -33.33m]);
    }

    [Fact]
    public void Allocate_con_peso_cero_asigna_cero()
    {
        Cop(50m).Allocate([1m, 0m], Policy).Select(s => s.Amount).ShouldBe([50m, 0m]);
    }

    [Fact]
    public void Allocate_rechaza_pesos_invalidos()
    {
        Should.Throw<ArgumentException>(() => Cop(10m).Allocate([], Policy));
        Should.Throw<ArgumentException>(() => Cop(10m).Allocate([1m, -1m], Policy));
        Should.Throw<ArgumentException>(() => Cop(10m).Allocate([0m, 0m], Policy));
    }

    [Fact]
    public void Igualdad_y_comparacion_por_valor()
    {
        Cop(10m).ShouldBe(Cop(10.00m));
        (Cop(5m) < Cop(10m)).ShouldBeTrue();
        (Cop(10m) > Cop(5m)).ShouldBeTrue();
        (Cop(10m) <= Cop(10m)).ShouldBeTrue();
        (Cop(10m) >= Cop(10m)).ShouldBeTrue();
        Cop(1m).CompareTo(null).ShouldBe(1);
    }

    [Fact]
    public void Propiedades_de_signo_y_negacion()
    {
        Money.Zero(Currency.Cop).IsZero.ShouldBeTrue();
        Cop(1m).IsPositive.ShouldBeTrue();
        (-Cop(1m)).IsNegative.ShouldBeTrue();
        (-Cop(1m)).Amount.ShouldBe(-1m);
    }

    [Fact]
    public void ToString_es_invariante()
    {
        Cop(4_800.5m).ToString().ShouldBe("COP 4800.5");
    }

    [Fact]
    public void Of_requiere_moneda()
    {
        Should.Throw<ArgumentNullException>(() => Money.Of(1m, null!));
    }
}
