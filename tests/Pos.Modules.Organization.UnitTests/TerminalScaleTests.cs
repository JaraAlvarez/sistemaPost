using Pos.Modules.Organization.Domain;

namespace Pos.Modules.Organization.UnitTests;

public class TerminalScaleTests
{
    private static TerminalScale Scale() => TerminalScale.Defaults(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

    private static ScaleConfiguration Config(
        string driver = "TOLEDO_8217", string? port = "COM3", bool enabled = true, int? baud = null, string? parity = null, int stable = 3, int empty = 5) =>
        new(enabled, driver, port, baud, null, parity, null, null, null, null, null, "Torrey", "PCR-40", null, stable, empty, AllowManualWeight: true);

    [Fact]
    public void Sin_configurar_espera_3_s_el_peso_estable_y_5_s_el_producto()
    {
        var scale = Scale();

        (scale.Enabled, scale.StableWaitSeconds, scale.EmptyWaitSeconds, scale.AllowManualWeight).ShouldBe((false, 3, 5, true));
    }

    [Fact]
    public void Configura_toledo_con_los_valores_de_fabrica_del_driver()
    {
        var scale = Scale();

        scale.Configure(Config(port: " com3 ", parity: "even")).IsSuccess.ShouldBeTrue();

        (scale.Driver, scale.Port, scale.Parity, scale.BaudRate, scale.Brand, scale.Model).ShouldBe(("TOLEDO_8217", (string?)"COM3", (string?)"EVEN", (int?)null, (string?)"Torrey", (string?)"PCR-40"));
    }

    [Theory]
    [InlineData("MARCA", "COM3", null, 3, 5)]
    [InlineData("CAS", null, null, 3, 5)]
    [InlineData("CAS", "LPT1", null, 3, 5)]
    [InlineData("CAS", "COM3", 1234, 3, 5)]
    [InlineData("CAS", "COM3", null, 0, 5)]
    [InlineData("CAS", "COM3", null, 3, 31)]
    public void Rechaza_configuraciones_invalidas(string driver, string? port, int? baud, int stable, int empty) =>
        Scale().Configure(Config(driver, port, baud: baud, stable: stable, empty: empty)).Error.Code.ShouldBe("ORGANIZATION.INVALID_SCALE");

    [Fact]
    public void Simulada_y_deshabilitada_no_exigen_puerto()
    {
        Scale().Configure(Config("SIMULATED", null)).IsSuccess.ShouldBeTrue();
        Scale().Configure(Config("CAS", null, enabled: false)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Peso_minimo_por_defecto_20_g_configurable_de_0_a_1000()
    {
        var scale = Scale();
        scale.MinWeightGrams.ShouldBe(20);

        scale.Configure(Config() with { MinWeightGrams = 0 }).IsSuccess.ShouldBeTrue();
        scale.MinWeightGrams.ShouldBe(0);
        scale.Configure(Config()).IsSuccess.ShouldBeTrue();
        scale.MinWeightGrams.ShouldBe(20);
        scale.Configure(Config() with { MinWeightGrams = 1001 }).Error.Code.ShouldBe("ORGANIZATION.INVALID_SCALE");
        scale.Configure(Config() with { MinWeightGrams = -1 }).Error.Code.ShouldBe("ORGANIZATION.INVALID_SCALE");
    }

    [Fact]
    public void Vincular_guarda_solo_la_clave_publica_P256_y_volver_a_vincular_la_reemplaza()
    {
        var scale = Scale();
        using var first = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var second = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var weak = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        var now = DateTimeOffset.UnixEpoch;

        scale.SetReadingKey(Guid.NewGuid(), Convert.ToBase64String(first.ExportSubjectPublicKeyInfo()), now).IsSuccess.ShouldBeTrue();
        var rotatedId = Guid.NewGuid();
        scale.SetReadingKey(rotatedId, Convert.ToBase64String(second.ExportSubjectPublicKeyInfo()), now.AddDays(1)).IsSuccess.ShouldBeTrue();
        (scale.ReadingKeyId, scale.ReadingPublicKey, scale.ReadingKeySetAt).ShouldBe(
            ((Guid?)rotatedId, (string?)Convert.ToBase64String(second.ExportSubjectPublicKeyInfo()), (DateTimeOffset?)now.AddDays(1)));

        scale.SetReadingKey(Guid.NewGuid(), Convert.ToBase64String(weak.ExportSubjectPublicKeyInfo()), now).Error.Code.ShouldBe("ORGANIZATION.INVALID_SCALE_KEY");
        scale.SetReadingKey(Guid.NewGuid(), "no-es-una-clave", now).Error.Code.ShouldBe("ORGANIZATION.INVALID_SCALE_KEY");
        scale.SetReadingKey(Guid.Empty, Convert.ToBase64String(first.ExportSubjectPublicKeyInfo()), now).Error.Code.ShouldBe("ORGANIZATION.INVALID_SCALE_KEY");
        scale.ReadingKeyId.ShouldBe(rotatedId);
    }
}
