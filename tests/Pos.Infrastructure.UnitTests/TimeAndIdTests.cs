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
        public LicenseGateState Current { get; } = new(Restricted: restricted, Demo: false, Reason: restricted ? "Licencia vencida." : null);
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

    private sealed record SellCommand : ICommand, IRestrictedSalesWindow;

    private sealed class WindowGate(bool closed) : ILicenseGate
    {
        public LicenseGateState Current { get; } = new(Restricted: true, Demo: false, Reason: "Licencia vencida.", SalesWindowClosed: closed);
    }

    [Fact]
    public async Task En_RESTRICTED_se_vende_solo_las_primeras_24_horas_y_despues_solo_se_cierra()
    {
        static Task<Result> Next() => Task.FromResult(Result.Success());
        var ct = TestContext.Current.CancellationToken;

        (await new LicenseRestrictionBehavior<SellCommand, Result>(new WindowGate(false)).Handle(new SellCommand(), Next, ct)).IsSuccess.ShouldBeTrue();
        var closed = await new LicenseRestrictionBehavior<SellCommand, Result>(new WindowGate(true)).Handle(new SellCommand(), Next, ct);
        closed.Error.Code.ShouldBe(LicenseGateErrors.Restricted.Code);
        closed.Error.Message.ShouldContain("24 horas");

        // Cerrar la jornada (permitido sin ventana) sigue pasando.
        (await new LicenseRestrictionBehavior<SaleCommand, Result>(new WindowGate(true)).Handle(new SaleCommand(), Next, ct)).IsSuccess.ShouldBeTrue();
    }

    private sealed record OpenCommand : ICommand, IRestrictedSessionOpening;

    private sealed class StateGate(LicenseGateState state) : ILicenseGate
    {
        public LicenseGateState Current { get; } = state;
    }

    /// <summary>
    /// Integración 1.2.0: abrir jornada con la licencia restringida solo si la restricción es recuperable (estado alterado o archivo
    /// ilegible) y dentro de la ventana de 24 h; sin restricción, siempre.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, true, null)]
    [InlineData(true, true, false, true, null)]
    [InlineData(true, true, true, false, "LICENSE.RESTRICTED")]
    [InlineData(true, false, false, false, "LICENSE.RESTRICTED")]
    [InlineData(true, false, true, false, "LICENSE.RESTRICTED")]
    public async Task Abrir_jornada_solo_con_restriccion_recuperable_y_dentro_de_la_ventana(
        bool restricted, bool allowsOpening, bool windowClosed, bool passes, string? code)
    {
        var calls = 0;
        Task<Result> Next()
        {
            calls++;
            return Task.FromResult(Result.Success());
        }

        var gate = new StateGate(new LicenseGateState(
            Restricted: restricted, Demo: false, Reason: restricted ? "Licencia restringida." : null, SalesWindowClosed: windowClosed,
            AllowsOpeningSessions: allowsOpening));
        var result = await new LicenseRestrictionBehavior<OpenCommand, Result>(gate).Handle(new OpenCommand(), Next, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBe(passes);
        calls.ShouldBe(passes ? 1 : 0);
        if (code is not null)
        {
            result.Error.Code.ShouldBe(code);
        }

        // AllowsOpeningSessions no abre la administración: un comando sin marca sigue bloqueado.
        if (restricted)
        {
            (await new LicenseRestrictionBehavior<AdminCommand, Result>(gate).Handle(new AdminCommand(), Next, TestContext.Current.CancellationToken))
                .IsSuccess.ShouldBeFalse();
        }
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
