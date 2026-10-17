using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.UnitTests;

/// <summary>
/// Fase 12-C (D12C-08): el enlace de pago en línea que envía la nube se guarda con la licencia, se reemplaza en cada respuesta, se borra
/// cuando la nube ya no lo envía y solo se aceptan direcciones http/https.
/// </summary>
public sealed class LicensePaymentLinkTests : IDisposable
{
    private const string PayUrl = "https://licencias.businesspost.test/pagar/Q7wX2kLm9pR4tY6u";

    private static readonly DateTimeOffset Now = new(2026, 12, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Node = Guid.CreateVersion7();
    private static readonly DeviceFingerprint Device = DeviceFingerprint.FromHardware("PLACA", "DISCO", "MAQUINA");

    private readonly LicenseSigningKey _key = LicenseSigningKey.Generate();
    private readonly FakeStore _store = new();
    private readonly FakeCloud _cloud = new();
    private readonly LicenseStateCache _cache = new();

    public LicensePaymentLinkTests() => _cloud.Key = _key;

    public void Dispose() => _key.Dispose();

    [Theory]
    [InlineData(PayUrl, PayUrl)]
    [InlineData("  " + PayUrl + "  ", PayUrl)]
    [InlineData("http://localhost:5080/pagar/abc", "http://localhost:5080/pagar/abc")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,<script>alert(1)</script>", null)]
    [InlineData("file:///C:/Windows/system32/calc.exe", null)]
    [InlineData("ftp://servidor/pagar", null)]
    [InlineData("/pagar/relativo", null)]
    [InlineData("pagar/abc", null)]
    [InlineData("https://usuario:clave@pagos.test/pagar/abc", null)]
    [InlineData("https://pagos.test/pagar/a b", null)]
    [InlineData("https://pagos.test/pagar/\nabc", null)]
    public void Solo_se_aceptan_direcciones_http_o_https(string? value, string? expected) =>
        LicensePaymentLink.Normalize(value).ShouldBe(expected);

    [Fact]
    public void Un_enlace_demasiado_largo_se_ignora() =>
        LicensePaymentLink.Normalize("https://pagos.test/" + new string('a', LicensePaymentLink.MaxLength)).ShouldBeNull();

    [Fact]
    public async Task La_activacion_guarda_el_enlace_y_el_estado_lo_muestra()
    {
        _store.Record = Record(token: null);
        _cloud.PaymentUrl = PayUrl;

        var status = (await Service().ActivateAsync(LicenseKey.Generate(), Ct)).Value;

        _store.Record.PaymentUrl.ShouldBe(PayUrl);
        status.PaymentUrl.ShouldBe(PayUrl);
        _cache.Summary.PaymentUrl.ShouldBe(PayUrl);
    }

    [Fact]
    public async Task El_checkin_reemplaza_el_enlace_y_lo_borra_cuando_la_nube_no_lo_envia()
    {
        _store.Record = Record(Token(), paymentUrl: "https://licencias.businesspost.test/pagar/viejo");
        var service = Service();

        _cloud.PaymentUrl = PayUrl;
        (await service.CheckinAsync("MANUAL", Ct)).Value.PaymentUrl.ShouldBe(PayUrl);
        _store.Record.PaymentUrl.ShouldBe(PayUrl);

        // Pago aplicado: la nube ya no envía el enlace y el POS lo borra.
        _cloud.PaymentUrl = null;
        var status = (await service.CheckinAsync("MANUAL", Ct)).Value;
        status.PaymentUrl.ShouldBeNull();
        _store.Record.PaymentUrl.ShouldBeNull();
        _cache.Summary.PaymentUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Un_enlace_que_no_es_http_se_ignora_al_guardarlo()
    {
        _store.Record = Record(Token());
        _cloud.PaymentUrl = "javascript:alert(document.cookie)";

        (await Service().CheckinAsync("MANUAL", Ct)).Value.PaymentUrl.ShouldBeNull();

        _store.Record.PaymentUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Sin_conexion_se_conserva_el_enlace_guardado()
    {
        _store.Record = Record(Token(), paymentUrl: PayUrl);
        _cloud.Reachable = false;

        var status = (await Service().CheckinAsync("SCHEDULED", Ct)).Value;

        status.PaymentUrl.ShouldBe(PayUrl);
        status.LastCheckinError.ShouldNotBeNull();
        _store.Record.PaymentUrl.ShouldBe(PayUrl);
    }

    [Fact]
    public async Task Un_enlace_guardado_invalido_o_sin_licencia_activada_no_se_muestra()
    {
        _store.Record = Record(Token(), paymentUrl: "javascript:alert(1)");
        (await Service().StatusAsync(Ct, persist: false)).Value.PaymentUrl.ShouldBeNull();

        _store.Record = Record(token: null, paymentUrl: PayUrl);
        (await Service().StatusAsync(Ct, persist: false)).Value.PaymentUrl.ShouldBeNull();
        _cache.Summary.PaymentUrl.ShouldBeNull();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LicenseService Service() => new(
        _store, _cloud, new FakeDevice(), new FakeKeys(new LicenseKeyRing([_key.PublicKey])), _cache, new FakeInstallation(), new NullAudit(),
        new NullAnchor(), new FixedClock());

    private string Token() => LicenseToken.Sign(Claims(), _key);

    private static LicenseClaims Claims() => new()
    {
        LicenseId = Guid.CreateVersion7(),
        OrganizationNit = "900123456-7",
        OrganizationName = "Supermercado de Prueba",
        InstallationId = Node,
        DeviceFingerprint = Device.ToString(),
        DeviceRole = DeviceRoles.AllInOne,
        Edition = LicenseEditions.SingleTerminal,
        SubscriptionStatus = SubscriptionStatuses.Active,
        IssuedAt = Now,
        ValidUntil = Now.AddDays(10),
        GraceDays = 7,
        RefreshAfter = Now.AddDays(1),
    };

    private static LicenseRecord Record(string? token, string? paymentUrl = null) =>
        new(Node, token, token is null ? null : "POS-ABCDE", token is null ? null : Now.AddDays(-20), token is null ? null : Now.AddHours(-1), null,
            false, false, false, 0, Now.AddHours(-1), Now.AddDays(-40), paymentUrl);

    private sealed class FakeStore : ILicenseStateStore
    {
        public LicenseRecord Record { get; set; } = null!;

        private string? _lastState;

        public Task<LicenseRecord?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<LicenseRecord?>(Record);

        public Task EnsureAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> GetLastStateAsync(CancellationToken cancellationToken) => Task.FromResult(_lastState);

        public Task SetLastStateAsync(string state, CancellationToken cancellationToken)
        {
            _lastState = state;
            return Task.CompletedTask;
        }

        public Task SaveTokenAsync(
            string token, string? keyPrefix, DateTimeOffset at, bool activation, long clockOffsetSeconds, string? paymentUrl, CancellationToken cancellationToken)
        {
            Record = Record with
            {
                Token = token, LicenseKeyPrefix = keyPrefix ?? Record.LicenseKeyPrefix, ActivatedAt = activation ? at : Record.ActivatedAt,
                LastCheckinAt = at, LastCheckinError = null, Deactivated = false, ClockOffsetSeconds = clockOffsetSeconds, PaymentUrl = paymentUrl,
            };
            return Task.CompletedTask;
        }

        public Task SaveCheckinErrorAsync(string error, bool? revoked, bool? reactivationRequired, CancellationToken cancellationToken)
        {
            Record = Record with { LastCheckinError = error };
            return Task.CompletedTask;
        }

        public Task ClearTokenAsync(CancellationToken cancellationToken)
        {
            Record = Record with { Token = null, Deactivated = true, PaymentUrl = null };
            return Task.CompletedTask;
        }

        public Task RaiseMaxObservedAsync(DateTimeOffset value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordCheckinAsync(LicenseCheckinDto checkin, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<LicenseCheckinDto>> ListCheckinsAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LicenseCheckinDto>>([]);

        public Task<LicenseSiteInfo?> SiteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<LicenseSiteInfo?>(new LicenseSiteInfo("900123456-7", "Supermercado de Prueba", "Principal", 0));
    }

    /// <summary>Nube que firma un token de este equipo y devuelve el enlace de pago configurado.</summary>
    private sealed class FakeCloud : ILicenseCloud
    {
        public string? PaymentUrl { get; set; }

        public bool Reachable { get; set; } = true;

        public LicenseSigningKey? Key { get; set; }

        public bool IsConfigured => true;

        public Task<CloudResult> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken) => Respond();

        public Task<CloudResult> CheckinAsync(CheckinRequest request, CancellationToken cancellationToken) => Respond();

        public Task<CloudResult> DeactivateAsync(DeactivationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CloudResult.Ok(null));

        private Task<CloudResult> Respond()
        {
            if (!Reachable)
            {
                return Task.FromResult(CloudResult.NoConnection);
            }

            var claims = Claims();
            return Task.FromResult(CloudResult.Ok(new LicenseTokenResponse(
                LicenseToken.Sign(claims, Key!), Key!.Kid, claims.SubscriptionStatus, claims.ValidUntil, claims.GraceDays, claims.RefreshAfter, [],
                PaymentUrl)));
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

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;

        public TimeZoneInfo BusinessTimeZone => TimeZoneInfo.Utc;
    }
}
