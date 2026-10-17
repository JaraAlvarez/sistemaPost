using Pos.SharedKernel.Measurement;

namespace Pos.SharedKernel.UnitTests.Measurement;

/// <summary>Hallazgo A2: no cobrar el peso del producto anterior que sigue en la báscula.</summary>
public class ScaleRepeatGuardTests
{
    [Fact]
    public void La_primera_pesada_de_la_caja_siempre_vale()
    {
        var guard = new ScaleRepeatGuard();

        guard.CanAdd(1.200m).ShouldBeTrue();
        guard.LastAddedKg.ShouldBeNull();
    }

    [Fact]
    public void Carne_y_luego_tomate_sin_retirar_la_carne_no_se_cobra_otra_vez()
    {
        var guard = new ScaleRepeatGuard();
        guard.Added(1.200m);

        // La carne sigue en la báscula: mismo peso (±5 g) sin pasar por cero.
        guard.CanAdd(1.200m).ShouldBeFalse();
        guard.CanAdd(1.204m).ShouldBeFalse();
        guard.CanAdd(1.195m).ShouldBeFalse();

        // Otro peso estable (pusieron el tomate encima o en lugar de la carne).
        guard.CanAdd(1.206m).ShouldBeTrue();
        guard.CanAdd(0.800m).ShouldBeTrue();
    }

    [Fact]
    public void Si_la_bascula_quedo_vacia_el_mismo_peso_vale_otra_vez()
    {
        var guard = new ScaleRepeatGuard();
        guard.Added(0.500m);
        guard.ObserveEmpty();

        guard.CanAdd(0.500m).ShouldBeTrue();
        guard.EmptiedSinceLast.ShouldBeTrue();

        guard.Added(0.500m);
        guard.EmptiedSinceLast.ShouldBeFalse();
        guard.CanAdd(0.500m).ShouldBeFalse();
    }

    [Theory]
    [InlineData(1.200, 1.205, true)]
    [InlineData(1.200, 1.195, true)]
    [InlineData(1.200, 1.2051, false)]
    [InlineData(1.200, 0.700, false)]
    public void Tolerancia_de_5_gramos(decimal previous, decimal current, bool repeat) =>
        ScaleRepeatGuard.IsRepeat(previous, current).ShouldBe(repeat);
}
