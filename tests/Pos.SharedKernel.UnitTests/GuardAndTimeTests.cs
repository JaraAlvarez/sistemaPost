using Pos.SharedKernel.Time;

namespace Pos.SharedKernel.UnitTests;

public class GuardTests
{
    [Fact]
    public void Validaciones_basicas()
    {
        Guard.NotNull("x").ShouldBe("x");
        Should.Throw<ArgumentNullException>(() => Guard.NotNull<string>(null));

        Guard.NotNullOrWhiteSpace("x").ShouldBe("x");
        Should.Throw<ArgumentException>(() => Guard.NotNullOrWhiteSpace(" "));

        Guard.NotNegative(0m).ShouldBe(0m);
        Should.Throw<ArgumentOutOfRangeException>(() => Guard.NotNegative(-0.01m));
    }

    [Fact]
    public void MaxDecimals_ignora_ceros_a_la_derecha()
    {
        Guard.MaxDecimals(1.5000m, 1).ShouldBe(1.5000m);
        Guard.HasAtMostDecimals(1.25m, 1).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => Guard.MaxDecimals(1.25m, 1));
    }
}

public class ClockTests
{
    [Fact]
    public void Zona_de_Colombia_es_UTC_menos_5()
    {
        BusinessTimeZones.Colombia.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc))
            .ShouldBe(TimeSpan.FromHours(-5));
    }

    [Fact]
    public void Zona_inexistente_lanza_excepcion()
    {
        Should.Throw<TimeZoneNotFoundException>(() => BusinessTimeZones.Find("Marte/Olympus"));
    }

    [Fact]
    public void Fecha_de_negocio_usa_la_hora_de_Colombia()
    {
        // 03:30 UTC del 29 de septiembre = 22:30 del 28 de septiembre en Bogotá.
        IClock clock = new FixedClock(new DateTimeOffset(2026, 9, 29, 3, 30, 0, TimeSpan.Zero));

        clock.Today.ShouldBe(new DateOnly(2026, 9, 28));
        clock.ToBusinessTime(clock.UtcNow).Hour.ShouldBe(22);
        clock.ToBusinessTime(clock.UtcNow).Offset.ShouldBe(TimeSpan.FromHours(-5));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public TimeZoneInfo BusinessTimeZone { get; } = BusinessTimeZones.Colombia;
    }
}
