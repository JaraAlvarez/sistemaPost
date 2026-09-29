namespace Pos.Licensing.Contracts.UnitTests;

public class DeviceFingerprintTests
{
    private static readonly DeviceFingerprint Original = DeviceFingerprint.FromHardware("BOARD-123", "DISK-456", "8f14e45f-ceea-467a-9575-3c5f1b0c0b1d");

    [Fact]
    public void Solo_guarda_hashes_de_los_componentes()
    {
        Original.ToString().ShouldStartWith("fp1.");
        Original.ToString().ShouldNotContain("BOARD-123");
        Original.Board.Length.ShouldBe(DeviceFingerprint.ComponentLength);
        Original.AvailableComponents.ShouldBe(3);
        Original.IsUsable.ShouldBeTrue();
    }

    [Fact]
    public void Normaliza_mayusculas_y_espacios_de_los_valores_leidos()
    {
        var again = DeviceFingerprint.FromHardware("  board-123 ", "disk-456", "8F14E45F-CEEA-467A-9575-3C5F1B0C0B1D");

        again.ShouldBe(Original);
        again.MatchingComponents(Original).ShouldBe(3);
    }

    [Fact]
    public void Cambiar_un_componente_sigue_siendo_el_mismo_equipo()
    {
        var newDisk = DeviceFingerprint.FromHardware("BOARD-123", "DISK-NUEVO", "8f14e45f-ceea-467a-9575-3c5f1b0c0b1d");

        newDisk.MatchingComponents(Original).ShouldBe(2);
        newDisk.Matches(Original).ShouldBeTrue();
    }

    [Fact]
    public void Cambiar_dos_componentes_es_otro_equipo()
    {
        var other = DeviceFingerprint.FromHardware("OTRA-PLACA", "OTRO-DISCO", "8f14e45f-ceea-467a-9575-3c5f1b0c0b1d");

        other.MatchingComponents(Original).ShouldBe(1);
        other.Matches(Original).ShouldBeFalse();
    }

    [Fact]
    public void Un_componente_vacio_nunca_cuenta_como_coincidencia()
    {
        var partial = DeviceFingerprint.FromHardware(null, "", "machine");
        var samePartial = DeviceFingerprint.FromHardware(" ", null, "machine");

        partial.AvailableComponents.ShouldBe(1);
        partial.IsUsable.ShouldBeFalse();
        partial.MatchingComponents(samePartial).ShouldBe(1);
        partial.Matches(samePartial).ShouldBeFalse();
    }

    [Fact]
    public void Se_lee_de_su_forma_textual()
    {
        DeviceFingerprint.TryParse(Original.ToString(), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(Original);

        var partial = DeviceFingerprint.FromHardware("a", null, "c");
        partial.ToString().ShouldContain(".-.");
        DeviceFingerprint.TryParse(partial.ToString(), out var parsedPartial).ShouldBeTrue();
        parsedPartial!.Disk.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fp1.a.b")]
    [InlineData("fp2.-.-.-")]
    [InlineData("fp1.ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ.-.-")]
    [InlineData("fp1.abc.-.-")]
    public void Rechaza_textos_que_no_son_huellas(string? text) => DeviceFingerprint.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Los_componentes_deben_ser_hashes()
    {
        Should.Throw<ArgumentException>(() => new DeviceFingerprint("no-hex", string.Empty, string.Empty));
        Should.Throw<ArgumentNullException>(() => new DeviceFingerprint(null!, string.Empty, string.Empty));
        Should.Throw<ArgumentNullException>(() => Original.MatchingComponents(null!));
    }
}
