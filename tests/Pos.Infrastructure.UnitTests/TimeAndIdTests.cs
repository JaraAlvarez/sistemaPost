using System.Globalization;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Infrastructure.Identifiers;
using Pos.Infrastructure.Messaging.Behaviors;
using Pos.Infrastructure.Time;
using Pos.SharedKernel.Results;
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

public class LicenseRestrictionBehaviorTests
{
    private sealed record AdminCommand : ICommand;

    private sealed record SaleCommand : ICommand, IAllowedWhenRestricted;

    private sealed record ReadQuery : IQuery<int>;

    private sealed class Gate(bool restricted) : ILicenseGate
    {
        public LicenseGateState Current { get; } = new(restricted, false, restricted ? "Licencia vencida." : null);
    }

    [Fact]
    public async Task En_RESTRICTED_solo_pasan_las_consultas_y_los_comandos_permitidos()
    {
        var calls = 0;
        Task<Result> Next()
        {
            calls++;
            return Task.FromResult(Result.Success());
        }

        Task<Result<int>> NextQuery()
        {
            calls++;
            return Task.FromResult(Result.Success(1));
        }

        var blocked = await new LicenseRestrictionBehavior<AdminCommand, Result>(new Gate(true)).Handle(new AdminCommand(), Next, TestContext.Current.CancellationToken);
        blocked.Error.Code.ShouldBe(LicenseGateErrors.Restricted.Code);
        (await new LicenseRestrictionBehavior<SaleCommand, Result>(new Gate(true)).Handle(new SaleCommand(), Next, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await new LicenseRestrictionBehavior<ReadQuery, Result<int>>(new Gate(true)).Handle(new ReadQuery(), NextQuery, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await new LicenseRestrictionBehavior<AdminCommand, Result>(new Gate(false)).Handle(new AdminCommand(), Next, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        calls.ShouldBe(3);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
