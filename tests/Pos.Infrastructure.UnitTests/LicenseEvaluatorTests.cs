using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;

namespace Pos.Infrastructure.UnitTests;

/// <summary>Fase 12-B: estados locales de la licencia con reloj simulado en todos los bordes (§2, §12).</summary>
public sealed class LicenseEvaluatorTests : IDisposable
{
    private static readonly DateTimeOffset Installed = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidUntil = new(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
    private static readonly Guid Node = Guid.CreateVersion7();
    private static readonly DeviceFingerprint Device = DeviceFingerprint.FromHardware("PLACA", "DISCO", "MAQUINA");

    private readonly LicenseSigningKey _key = LicenseSigningKey.Generate();

    private LicenseKeyRing Ring => new([_key.PublicKey]);

    public void Dispose() => _key.Dispose();

    [Fact]
    public void Sin_activar_hay_30_dias_de_demostracion_y_luego_restringida()
    {
        var record = Record(token: null);

        var demo = Evaluate(record, Installed.AddDays(29));
        demo.State.ShouldBe(LicenseStates.Demo);
        demo.DaysLeft.ShouldBe(1);
        demo.Notices.ShouldContain(n => n.Code == "DEMO");

        Evaluate(record, Installed.AddDays(30).AddMinutes(1)).State.ShouldBe(LicenseStates.Restricted);
        Evaluate(record with { Deactivated = true }, Installed.AddDays(1)).State.ShouldBe(LicenseStates.Restricted);
    }

    [Fact]
    public void Vigente_sin_conexion_gracia_y_restringida_segun_el_reloj()
    {
        var checkin = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var record = Record(Token(), lastCheckin: checkin);

        Evaluate(record, checkin.AddHours(2)).State.ShouldBe(LicenseStates.Valid);
        Evaluate(record, checkin.AddDays(3)).State.ShouldBe(LicenseStates.ValidOffline);
        var grace = Evaluate(record, ValidUntil.AddDays(1));
        grace.State.ShouldBe(LicenseStates.Grace);
        grace.DaysLeft.ShouldBe(6);
        Evaluate(record, ValidUntil.AddDays(7)).State.ShouldBe(LicenseStates.Grace);
        Evaluate(record, ValidUntil.AddDays(7).AddSeconds(1)).State.ShouldBe(LicenseStates.Restricted);
    }

    [Theory]
    [InlineData(SubscriptionStatuses.Suspended, LicenseStates.Restricted)]
    [InlineData(SubscriptionStatuses.Cancelled, LicenseStates.Restricted)]
    [InlineData(SubscriptionStatuses.Expired, LicenseStates.Restricted)]
    [InlineData(SubscriptionStatuses.PastDue, LicenseStates.Grace)]
    [InlineData(SubscriptionStatuses.Trial, LicenseStates.Valid)]
    public void El_estado_de_la_suscripcion_informado_por_la_nube_manda(string subscription, string expected)
    {
        var at = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        Evaluate(Record(Token(subscription), lastCheckin: at), at.AddHours(1)).State.ShouldBe(expected);
    }

    [Fact]
    public void Otro_equipo_otra_instalacion_o_un_token_no_confiable_no_sirven()
    {
        var at = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var record = Record(Token(), lastCheckin: at);

        // Cambiar el disco: 2 de 3 siguen coincidiendo.
        LicenseEvaluator.Evaluate(record, Verify(record.Token!), DeviceFingerprint.FromHardware("PLACA", "OTRO", "MAQUINA"), DeviceRoles.AllInOne, at)
            .State.ShouldBe(LicenseStates.Valid);
        LicenseEvaluator.Evaluate(record, Verify(record.Token!), DeviceFingerprint.FromHardware("X", "Y", "Z"), DeviceRoles.AllInOne, at)
            .State.ShouldBe(LicenseStates.ReactivationRequired);
        Evaluate(record with { NodeId = Guid.CreateVersion7() }, at).State.ShouldBe(LicenseStates.ReactivationRequired);
        Evaluate(record with { Revoked = true }, at).State.ShouldBe(LicenseStates.Restricted);

        using var other = LicenseSigningKey.Generate();
        var foreign = LicenseToken.Sign(Claims(SubscriptionStatuses.Active), other);
        LicenseEvaluator.Evaluate(record with { Token = foreign }, LicenseToken.Verify(foreign, Ring), Device, DeviceRoles.AllInOne, at)
            .State.ShouldBe(LicenseStates.Restricted);
    }

    [Fact]
    public void Atrasar_el_reloj_mas_de_24_horas_restringe_hasta_que_la_hora_confiable_lo_corrige()
    {
        var seen = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var record = Record(Token(), lastCheckin: seen, maxObserved: seen);

        Evaluate(record, seen.AddHours(-23)).State.ShouldNotBe(LicenseStates.Restricted);
        var rolledBack = Evaluate(record, seen.AddHours(-25));
        rolledBack.State.ShouldBe(LicenseStates.Restricted);
        rolledBack.ClockRollback.ShouldBeTrue();

        // Un check-in exitoso guarda la diferencia con la hora confiable: el mismo reloj atrasado ya no restringe.
        var corrected = Evaluate(record with { ClockOffsetSeconds = (long)TimeSpan.FromHours(26).TotalSeconds }, seen.AddHours(-25));
        corrected.ClockRollback.ShouldBeFalse();
        corrected.State.ShouldBe(LicenseStates.Valid);
    }

    [Fact]
    public void El_filtro_del_pipeline_sigue_el_estado_calculado()
    {
        var cache = new LicenseStateCache();
        ((Pos.Application.Abstractions.Licensing.ILicenseGate)cache).Current.Restricted.ShouldBeFalse();

        cache.Update(Evaluate(Record(token: null), Installed.AddDays(31)));
        var gate = ((Pos.Application.Abstractions.Licensing.ILicenseGate)cache).Current;
        gate.Restricted.ShouldBeTrue();
        gate.Reason.ShouldNotBeNull().ShouldContain("demostración");

        cache.Update(Evaluate(Record(token: null), Installed.AddDays(1)));
        ((Pos.Application.Abstractions.Licensing.ILicenseGate)cache).Current.Demo.ShouldBeTrue();
        cache.Summary.State.ShouldBe(LicenseStates.Demo);
    }

    private LicenseEvaluation Evaluate(LicenseRecord record, DateTimeOffset now) =>
        LicenseEvaluator.Evaluate(record, record.Token is null ? null : Verify(record.Token), Device, DeviceRoles.AllInOne, now);

    private LicenseTokenVerification Verify(string token) => LicenseToken.Verify(token, Ring);

    private string Token(string subscription = SubscriptionStatuses.Active) => LicenseToken.Sign(Claims(subscription), _key);

    private static LicenseClaims Claims(string subscription) => new()
    {
        LicenseId = Guid.CreateVersion7(),
        OrganizationNit = "900123456-7",
        OrganizationName = "Supermercado de Prueba",
        InstallationId = Node,
        DeviceFingerprint = Device.ToString(),
        DeviceRole = DeviceRoles.AllInOne,
        Edition = LicenseEditions.SingleTerminal,
        SubscriptionStatus = subscription,
        IssuedAt = Installed,
        ValidUntil = ValidUntil,
        GraceDays = 7,
        RefreshAfter = Installed.AddDays(1),
    };

    private static LicenseRecord Record(string? token, DateTimeOffset? lastCheckin = null, DateTimeOffset? maxObserved = null) =>
        new(Node, token, "POS-ABCDE", token is null ? null : Installed, lastCheckin, null, false, false, false, 0, maxObserved ?? Installed, Installed);
}
