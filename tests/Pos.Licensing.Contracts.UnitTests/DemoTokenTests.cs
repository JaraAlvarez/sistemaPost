namespace Pos.Licensing.Contracts.UnitTests;

/// <summary>Registro firmado de la demo por equipo: tipo propio, no intercambiable con el token de licencia ni con el enlace del propietario.</summary>
public sealed class DemoTokenTests : IDisposable
{
    private readonly LicenseSigningKey _key = LicenseSigningKey.Generate();

    public void Dispose() => _key.Dispose();

    private LicenseKeyRing Ring => new([_key.PublicKey]);

    [Fact]
    public void Se_firma_y_se_verifica_con_las_claves_de_confianza()
    {
        var started = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(-5));
        var token = DemoToken.Sign(new DemoClaims { DeviceFingerprint = "fp1.-.-.-", StartedAt = started, IssuedAt = started.AddDays(3) }, _key);

        var verification = DemoToken.Verify(token, Ring);
        verification.IsValid.ShouldBeTrue();
        verification.Claims!.StartedAt.ShouldBe(started);
        verification.Claims.StartedAt.Offset.ShouldBe(TimeSpan.Zero);

        using var other = LicenseSigningKey.Generate();
        DemoToken.Verify(token, new LicenseKeyRing([other.PublicKey])).Status.ShouldBe(DemoTokenStatus.UnknownKey);
        DemoToken.Verify(token[..^4] + "AAAA", Ring).Status.ShouldNotBe(DemoTokenStatus.Valid);
        DemoToken.Verify("no.es.token", Ring).Status.ShouldBe(DemoTokenStatus.Malformed);
        DemoToken.Verify(null, Ring).Status.ShouldBe(DemoTokenStatus.Malformed);
    }

    [Fact]
    public void No_se_confunde_con_un_token_de_licencia()
    {
        var demo = DemoToken.Sign(new DemoClaims { DeviceFingerprint = "fp1.-.-.-", StartedAt = DateTimeOffset.UtcNow, IssuedAt = DateTimeOffset.UtcNow }, _key);
        LicenseToken.Verify(demo, Ring).Status.ShouldBe(LicenseTokenStatus.Malformed);

        var license = LicenseToken.Sign(LicenseTokenTests.Claims(), _key);
        DemoToken.Verify(license, Ring).Status.ShouldBe(DemoTokenStatus.Malformed);
    }
}
