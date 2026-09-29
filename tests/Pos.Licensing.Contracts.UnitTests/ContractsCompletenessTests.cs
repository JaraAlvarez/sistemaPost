using System.Buffers.Text;

namespace Pos.Licensing.Contracts.UnitTests;

/// <summary>Casos borde del contrato: claves públicas inválidas, firmas de largo incorrecto y los DTO de la API.</summary>
public sealed class ContractsCompletenessTests : IDisposable
{
    private readonly LicenseSigningKey _key = LicenseSigningKey.Generate();

    public void Dispose() => _key.Dispose();

    [Fact]
    public void Una_clave_publica_de_largo_distinto_a_32_bytes_se_rechaza()
    {
        Should.Throw<FormatException>(() => LicensePublicKey.FromRaw(new byte[31]));
    }

    [Fact]
    public void La_clave_publica_importada_expone_la_clave_de_NSec_y_el_mismo_kid()
    {
        var parsed = LicensePublicKey.TryParse(_key.PublicKey.X, out var key);

        parsed.ShouldBeTrue();
        key!.Key.ShouldNotBeNull();
        key.Kid.ShouldBe(_key.Kid);
    }

    [Fact]
    public void Un_token_con_firma_de_largo_distinto_a_64_bytes_es_malformado()
    {
        var token = LicenseToken.Sign(LicenseTokenTests.Claims(), _key);
        var parts = token.Split('.');
        var shortSignature = Base64Url.EncodeToString(new byte[10]);

        var result = LicenseToken.Verify($"{parts[0]}.{parts[1]}.{shortSignature}", new LicenseKeyRing([_key.PublicKey]));

        result.Status.ShouldBe(LicenseTokenStatus.Malformed);
        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Los_mensajes_de_la_API_conservan_sus_valores()
    {
        var installation = Guid.CreateVersion7();
        var activation = new ActivationRequest("POS-AAAAA", installation, "fp1.-.-.-", DeviceRoles.AllInOne, "1.0.0", "900123456-8");
        var full = activation with { BranchName = "Centro", DeviceName = "CAJA-1", OperatingSystem = "Windows 11" };
        var now = DateTimeOffset.UtcNow;
        var response = new LicenseTokenResponse("tok", "kid", SubscriptionStatuses.Active, now, 7, now.AddHours(24), []);
        var checkin = new CheckinRequest("tok", "fp1.-.-.-", "1.0.0", 3, now);
        var deactivation = new DeactivationRequest("tok", "fp1.-.-.-");
        var keys = new PublicKeysResponse([new PublicKeyDto("kid", "OKP", "Ed25519", "x", "ACTIVE")]);

        activation.BranchName.ShouldBeNull();
        activation.InstallationId.ShouldBe(installation);
        activation.LicenseKey.ShouldBe("POS-AAAAA");
        activation.Fingerprint.ShouldBe("fp1.-.-.-");
        activation.DeviceRole.ShouldBe(DeviceRoles.AllInOne);
        activation.AppVersion.ShouldBe("1.0.0");
        activation.OrganizationNit.ShouldBe("900123456-8");
        full.BranchName.ShouldBe("Centro");
        full.DeviceName.ShouldBe("CAJA-1");
        full.OperatingSystem.ShouldBe("Windows 11");
        response.Token.ShouldBe("tok");
        response.Kid.ShouldBe("kid");
        response.SubscriptionStatus.ShouldBe(SubscriptionStatuses.Active);
        response.ValidUntil.ShouldBe(now);
        response.GraceDays.ShouldBe(7);
        response.RefreshAfter.ShouldBe(now.AddHours(24));
        response.Messages.ShouldBeEmpty();
        checkin.ActiveTerminals.ShouldBe(3);
        deactivation.Reason.ShouldBeNull();
        keys.Keys.Single().Kty.ShouldBe("OKP");
    }
}
