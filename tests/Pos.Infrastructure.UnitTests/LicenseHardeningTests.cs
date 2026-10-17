using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.UnitTests;

/// <summary>Endurecimiento de la licencia (docs/fases/fase-licencia-endurecimiento.md): R2, H3, H4, H5, H6, H7 y N1.</summary>
public sealed class LicenseHardeningTests : IDisposable
{
    private static readonly DateTimeOffset Installed = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Today = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Node = Guid.CreateVersion7();
    private static readonly DeviceFingerprint Device = DeviceFingerprint.FromHardware("PLACA", "DISCO", "MAQUINA");

    private readonly LicenseSigningKey _key = LicenseSigningKey.Generate();
    private readonly FakeStore _store = new();
    private readonly FakeCloud _cloud = new();
    private readonly MovableClock _clock = new() { UtcNow = Today };
    private readonly LicenseStateCache _cache = new();

    public LicenseHardeningTests()
    {
        _cloud.Key = _key;
        _cloud.IssuedAt = () => Today;
    }

    public void Dispose() => _key.Dispose();

    // ------------------------------------------------------------------------------------------------ N1

    [Fact]
    public void Mas_dias_sin_checkin_que_el_maximo_restringe_y_la_configuracion_solo_puede_acortarlo()
    {
        var record = Record(Token(issuedAt: Today), lastCheckin: Today, maxObserved: Today);

        Evaluate(record, Today.AddDays(29)).State.ShouldBe(LicenseStates.ValidOffline);
        Evaluate(record, Today.AddDays(29)).Reason.ShouldContain("conéctese antes del");
        var offline = Evaluate(record, Today.AddDays(30).AddMinutes(1));
        offline.State.ShouldBe(LicenseStates.Restricted);
        offline.Reason.ShouldContain("30 días");

        Evaluate(record, Today.AddDays(8), maxOfflineDays: 7).State.ShouldBe(LicenseStates.Restricted);
        Evaluate(record, Today.AddDays(31), maxOfflineDays: 365).State.ShouldBe(LicenseStates.Restricted); // no se puede alargar
    }

    // ------------------------------------------------------------------------------------------------ H3

    [Fact]
    public void Atrasar_el_reloj_de_a_poco_no_congela_la_hora_el_tiempo_de_funcionamiento_la_avanza()
    {
        // La licencia vence en 10 días + 7 de gracia. Alguien atrasa el reloj de Windows tanto como avanza: queda "congelado".
        var record = Record(Token(issuedAt: Today, validUntil: Today.AddDays(10)), lastCheckin: Today, maxObserved: Today);
        var frozen = Today;
        var states = new HashSet<string>();
        for (var tick = 0; tick < 20 * 96; tick++) // 20 días de funcionamiento, un recálculo cada 15 minutos
        {
            var evaluation = Evaluate(record, frozen);
            states.Add(evaluation.State);
            record = record with { MaxObservedUtc = LicenseEvaluator.NextMark(record, evaluation, TimeSpan.FromMinutes(15)) };
        }

        // Sin el contador monótono la hora quedaría en "hoy" y la licencia seguiría vigente para siempre.
        record.MaxObservedUtc.ShouldBe(Today.AddDays(20));
        states.ShouldContain(LicenseStates.Valid);
        Evaluate(record, frozen).State.ShouldBe(LicenseStates.Restricted);
        Evaluate(record, frozen).ClockRollback.ShouldBeTrue();

        // Aunque el reloj se corrija a la hora de la marca, la licencia ya venció (10 + 7 días < 20).
        var corrected = Evaluate(record, record.MaxObservedUtc);
        (corrected.State, corrected.ClockRollback).ShouldBe((LicenseStates.Restricted, false));
    }

    [Fact]
    public void El_tiempo_de_funcionamiento_solo_suma_y_se_acota_por_ciclo()
    {
        var record = Record(Token(issuedAt: Today), lastCheckin: Today, maxObserved: Today);
        var evaluation = Evaluate(record, Today);

        LicenseEvaluator.NextMark(record, evaluation, null).ShouldBe(Today);
        LicenseEvaluator.NextMark(record, evaluation, TimeSpan.FromMinutes(-5)).ShouldBe(Today);
        LicenseEvaluator.NextMark(record, evaluation, TimeSpan.FromMinutes(1)).ShouldBe(Today.AddMinutes(1));
        LicenseEvaluator.NextMark(record, evaluation, TimeSpan.FromDays(3)).ShouldBe(Today + LicenseEvaluator.MaxElapsedPerTick);
        LicenseEvaluator.NextMark(record, Evaluate(record, Today.AddHours(2)), TimeSpan.FromMinutes(1)).ShouldBe(Today.AddHours(2));
    }

    // ------------------------------------------------------------------------------------------------ H7 y H4 (MAC)

    [Theory]
    [InlineData("900123456-7", "900123456-7", true)]
    [InlineData("900123456-7", "900123456", true)]
    [InlineData("900.123.456-7", "0900123456-7", true)]
    [InlineData("900123456-7", "900123456-8", false)]
    [InlineData("900123456-7", "800123456-7", false)]
    [InlineData("900123456-7", "", false)]
    public void El_nit_se_compara_por_numero_y_digito_de_verificacion(string a, string b, bool same) =>
        LicenseEvaluator.SameNit(a, b).ShouldBe(same);

    [Fact]
    public void Un_token_de_otra_empresa_o_un_estado_alterado_restringen()
    {
        var record = Record(Token(issuedAt: Today), lastCheckin: Today, maxObserved: Today);
        Evaluate(record with { CompanyNit = "900123456-7" }, Today).State.ShouldBe(LicenseStates.Valid);
        var other = Evaluate(record with { CompanyNit = "811222333-1" }, Today);
        other.State.ShouldBe(LicenseStates.Restricted);
        other.Reason.ShouldContain("otra empresa");

        var tampered = Evaluate(record with { Tampered = true }, Today);
        tampered.State.ShouldBe(LicenseStates.Restricted);
        tampered.Reason.ShouldContain("no cuadran con su firma");
    }

    // ------------------------------------------------------------------------------------------------ R2

    [Fact]
    public async Task Un_reloj_adelantado_por_error_se_corrige_con_el_siguiente_checkin()
    {
        _store.Record = Record(Token(issuedAt: Today), lastCheckin: Today, maxObserved: Today);
        var service = Service();

        // Alguien adelanta el reloj 10 días: la marca sube.
        _clock.UtcNow = Today.AddDays(10);
        await service.StatusAsync(Ct);
        _store.Record.MaxObservedUtc.ShouldBe(Today.AddDays(10));

        // Corrige el reloj: retroceso de 10 días respecto de la marca → restringida.
        _clock.UtcNow = Today.AddHours(1);
        (await service.StatusAsync(Ct)).Value.State.ShouldBe(LicenseStates.Restricted);

        // El check-in trae la hora confiable de la nube (iat): la marca BAJA a esa hora y la licencia vuelve a estar vigente.
        _cloud.IssuedAt = () => Today.AddHours(1);
        (await service.CheckinAsync("MANUAL", Ct)).Value.State.ShouldBe(LicenseStates.Valid);
        _store.Record.MaxObservedUtc.ShouldBe(Today.AddHours(1));
        ((ILicenseGate)_cache).Current.Restricted.ShouldBeFalse();
    }

    // ------------------------------------------------------------------------------------------------ H4 (token más viejo)

    [Fact]
    public async Task Un_token_mas_viejo_que_el_vigente_se_rechaza()
    {
        _store.Record = Record(Token(issuedAt: Today), lastCheckin: Today, maxObserved: Today);
        var current = _store.Record.Token;

        _cloud.IssuedAt = () => Today.AddDays(-3);
        var status = (await Service().CheckinAsync("MANUAL", Ct)).Value;

        _store.Record.Token.ShouldBe(current);
        status.LastCheckinError.ShouldNotBeNull().ShouldContain(LicenseErrors.TokenRejected.Code);
    }

    // ------------------------------------------------------------------------------------------------ H6

    [Fact]
    public async Task Restringida_se_puede_vender_24_horas_y_despues_solo_cerrar()
    {
        // Suspendida: restringida desde ahora.
        _store.Record = Record(Token(SubscriptionStatuses.Suspended, issuedAt: Today), lastCheckin: Today, maxObserved: Today);
        var service = Service();

        await service.StatusAsync(Ct);
        _store.Record.RestrictedSince.ShouldBe(Today);
        var gate = ((ILicenseGate)_cache).Current;
        (gate.Restricted, gate.SalesWindowClosed).ShouldBe((true, false));

        _clock.UtcNow = Today.AddHours(23);
        await service.StatusAsync(Ct);
        ((ILicenseGate)_cache).Current.SalesWindowClosed.ShouldBeFalse();

        _clock.UtcNow = Today.AddHours(25);
        await service.StatusAsync(Ct);
        ((ILicenseGate)_cache).Current.SalesWindowClosed.ShouldBeTrue();
        _store.Record.RestrictedSince.ShouldBe(Today); // no se reinicia

        // Al dejar de estar restringida se olvida la fecha.
        _cloud.IssuedAt = () => Today.AddHours(25);
        (await service.CheckinAsync("MANUAL", Ct)).Value.State.ShouldBe(LicenseStates.Valid);
        _store.Record.RestrictedSince.ShouldBeNull();
        ((ILicenseGate)_cache).Current.SalesWindowClosed.ShouldBeFalse();
    }

    // ------------------------------------------------------------------------------------------------ H5

    [Fact]
    public void Si_el_calculo_falla_mas_de_una_hora_el_filtro_restringe()
    {
        var time = new MonotonicTime();
        var cache = new LicenseStateCache(time);
        ILicenseGate gate = cache;

        // Recién arrancado y sin cálculo todavía: tolerancia (fallo transitorio).
        gate.Current.Restricted.ShouldBeFalse();
        time.Advance(TimeSpan.FromMinutes(61));
        gate.Current.Restricted.ShouldBeTrue();
        gate.Current.Reason.ShouldBe(LicenseStateCache.StaleReason);

        // Un cálculo exitoso vale una hora.
        cache.Update(Evaluate(Record(Token(issuedAt: Today), lastCheckin: Today, maxObserved: Today), Today));
        gate.Current.Restricted.ShouldBeFalse();
        time.Advance(TimeSpan.FromMinutes(59));
        gate.Current.Restricted.ShouldBeFalse();
        time.Advance(TimeSpan.FromMinutes(2));
        gate.Current.Restricted.ShouldBeTrue();
    }

    // ------------------------------------------------------------------------------------------------ Apoyo

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LicenseEvaluation Evaluate(LicenseRecord record, DateTimeOffset now, int maxOfflineDays = LicenseEvaluator.DefaultMaxOfflineDays) =>
        LicenseEvaluator.Evaluate(
            record, record.Token is null ? null : LicenseToken.Verify(record.Token, new LicenseKeyRing([_key.PublicKey])), Device, DeviceRoles.AllInOne,
            now, maxOfflineDays);

    private LicenseService Service() => new(
        _store, _cloud, new FakeDevice(), new FakeKeys(new LicenseKeyRing([_key.PublicKey])), _cache, new FakeInstallation(), new NullAudit(),
        new NullAnchor(), _clock);

    private string Token(string subscription = SubscriptionStatuses.Active, DateTimeOffset? issuedAt = null, DateTimeOffset? validUntil = null) =>
        LicenseToken.Sign(Claims(subscription, issuedAt ?? Installed, validUntil), _key);

    private static LicenseClaims Claims(string subscription, DateTimeOffset issuedAt, DateTimeOffset? validUntil = null) => new()
    {
        LicenseId = Guid.CreateVersion7(),
        OrganizationNit = "900123456-7",
        OrganizationName = "Supermercado de Prueba",
        InstallationId = Node,
        DeviceFingerprint = Device.ToString(),
        DeviceRole = DeviceRoles.AllInOne,
        Edition = LicenseEditions.SingleTerminal,
        SubscriptionStatus = subscription,
        IssuedAt = issuedAt,
        ValidUntil = validUntil ?? issuedAt.AddDays(30),
        GraceDays = 7,
        RefreshAfter = issuedAt.AddDays(1),
    };

    private static LicenseRecord Record(string? token, DateTimeOffset? lastCheckin = null, DateTimeOffset? maxObserved = null) =>
        new(Node, token, "POS-ABCDE", Installed, lastCheckin, null, false, false, false, 0, maxObserved ?? Installed, Installed);

    private sealed class FakeStore : ILicenseStateStore
    {
        private string? _lastState;

        public LicenseRecord Record { get; set; } = null!;

        public Task<LicenseRecord?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<LicenseRecord?>(Record);

        public Task EnsureAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> GetLastStateAsync(CancellationToken cancellationToken) => Task.FromResult(_lastState);

        public Task SetLastStateAsync(string state, DateTimeOffset? restrictedSince, CancellationToken cancellationToken)
        {
            _lastState = state;
            Record = Record with { RestrictedSince = restrictedSince };
            return Task.CompletedTask;
        }

        public Task SaveTokenAsync(
            string token, string? keyPrefix, DateTimeOffset at, bool activation, long clockOffsetSeconds, string? paymentUrl, DateTimeOffset trustedNow,
            CancellationToken cancellationToken)
        {
            Record = Record with
            {
                Token = token, LastCheckinAt = at, LastCheckinError = null, Revoked = false, ReactivationRequired = false, Deactivated = false,
                ClockOffsetSeconds = clockOffsetSeconds, PaymentUrl = paymentUrl, MaxObservedUtc = trustedNow,
            };
            return Task.CompletedTask;
        }

        public Task SaveCheckinErrorAsync(string error, bool? revoked, bool? reactivationRequired, CancellationToken cancellationToken)
        {
            Record = Record with { LastCheckinError = error };
            return Task.CompletedTask;
        }

        public Task ClearTokenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RaiseMaxObservedAsync(DateTimeOffset value, CancellationToken cancellationToken)
        {
            if (value > Record.MaxObservedUtc)
            {
                Record = Record with { MaxObservedUtc = value };
            }

            return Task.CompletedTask;
        }

        public Task RecordCheckinAsync(LicenseCheckinDto checkin, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<LicenseCheckinDto>> ListCheckinsAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LicenseCheckinDto>>([]);

        public Task<LicenseSiteInfo?> SiteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<LicenseSiteInfo?>(new LicenseSiteInfo("900123456-7", "Supermercado de Prueba", "Principal", 0));
    }

    /// <summary>Nube que firma un token de este equipo con la hora de emisión que diga la prueba.</summary>
    private sealed class FakeCloud : ILicenseCloud
    {
        public LicenseSigningKey? Key { get; set; }

        public Func<DateTimeOffset> IssuedAt { get; set; } = () => Today;

        public bool IsConfigured => true;

        public Task<CloudResult> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken) => Respond();

        public Task<CloudResult> CheckinAsync(CheckinRequest request, CancellationToken cancellationToken) => Respond();

        public Task<CloudResult> DeactivateAsync(DeactivationRequest request, CancellationToken cancellationToken) => Task.FromResult(CloudResult.Ok(null));

        private Task<CloudResult> Respond()
        {
            var claims = Claims(SubscriptionStatuses.Active, IssuedAt());
            return Task.FromResult(CloudResult.Ok(new LicenseTokenResponse(
                LicenseToken.Sign(claims, Key!), Key!.Kid, claims.SubscriptionStatus, claims.ValidUntil, claims.GraceDays, claims.RefreshAfter, [], null)));
        }
    }

    private sealed class FakeDevice : IDeviceIdentity
    {
        public DeviceFingerprint? Fingerprint => Device;

        public string DeviceName => "CAJA-1";

        public string OperatingSystem => "Windows 11";

        public string AppVersion => "1.0.0";
    }

    private sealed class FakeKeys(LicenseKeyRing ring) : ILicenseKeys
    {
        public LicenseKeyRing Ring => ring;
    }

    private sealed class FakeInstallation : IInstallationContext
    {
        public Guid NodeId => Node;

        public NodeRole NodeRole => NodeRole.AllInOne;

        public bool IsSetupCompleted => true;

        public Guid? CompanyId => Guid.Empty;

        public Guid? BranchId => Guid.Empty;

        public Guid? SystemUserId => null;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullAudit : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullAnchor : IAuditAnchor
    {
        public Task<AuditSealInfo?> SealNowAsync(CancellationToken cancellationToken = default) => Task.FromResult<AuditSealInfo?>(null);
    }

    private sealed class MovableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo BusinessTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Reloj monótono que la prueba hace avanzar.</summary>
    private sealed class MonotonicTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan delta) => _ticks += delta.Ticks;
    }
}
