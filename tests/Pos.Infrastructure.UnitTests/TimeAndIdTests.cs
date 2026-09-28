using System.Globalization;
using Pos.Infrastructure.Identifiers;
using Pos.Infrastructure.Licensing;
using Pos.Infrastructure.Time;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.UnitTests;

public class SystemClockTests
{
    [Fact]
    public void Usa_el_TimeProvider_y_la_zona_del_negocio()
    {
        var instant = new DateTimeOffset(2026, 9, 29, 3, 30, 0, TimeSpan.Zero);
        IClock clock = new SystemClock(new FixedTimeProvider(instant), BusinessTimeZones.Colombia);

        clock.UtcNow.ShouldBe(instant);
        clock.Today.ShouldBe(new DateOnly(2026, 9, 28));
    }
}

public class UuidV7IdGeneratorTests
{
    [Fact]
    public void Genera_UUID_version_7_con_la_marca_de_tiempo_del_reloj()
    {
        var instant = new DateTimeOffset(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);
        var generator = new UuidV7IdGenerator(new SystemClock(new FixedTimeProvider(instant), BusinessTimeZones.Colombia));

        var id = generator.NewId();

        id.Version.ShouldBe(7);
        var embeddedMs = long.Parse(id.ToString("N")[..12], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        embeddedMs.ShouldBe(instant.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Ids_de_instantes_posteriores_ordenan_despues()
    {
        var provider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 28, 15, 0, 0, TimeSpan.Zero));
        var generator = new UuidV7IdGenerator(new SystemClock(provider, BusinessTimeZones.Colombia));

        var first = generator.NewId();
        provider.Advance(TimeSpan.FromMilliseconds(1));
        var second = generator.NewId();

        string.CompareOrdinal(first.ToString("N"), second.ToString("N")).ShouldBeLessThan(0);
        first.ShouldNotBe(second);
    }
}

public class AllowAllFeatureGateTests
{
    [Fact]
    public void Habilita_todo_sin_limites_hasta_la_fase_12()
    {
        var gate = new AllowAllFeatureGate();

        gate.IsEnabled("purchasing.orders").ShouldBeTrue();
        gate.GetLimit("max_terminals").ShouldBeNull();
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
