namespace Pos.Licensing.Contracts.UnitTests;

/// <summary>Equipo para la DEMO (no para la licencia): valores genéricos fuera, y con placa genérica basta el disco.</summary>
public sealed class DemoDeviceIdentityTests
{
    private const string GenericBoard = "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF";

    [Theory]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("03000200-0400-0500-0006-000700080009")]
    [InlineData("To be filled by O.E.M.")]
    public void Los_uuid_genericos_de_la_placa_no_identifican_el_equipo(string uuid) =>
        DemoDeviceIdentity.Normalize(DeviceFingerprint.FromHardware(uuid, "DISCO", "MAQUINA")).Board.ShouldBeEmpty();

    [Theory]
    [InlineData("0000_0000_0000_0000.")]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("0")]
    public void Las_series_de_disco_genericas_tampoco(string serial) =>
        DemoDeviceIdentity.Normalize(DeviceFingerprint.FromHardware("PLACA", serial, "MAQUINA")).Disk.ShouldBeEmpty();

    [Fact]
    public void La_huella_de_la_licencia_no_cambia()
    {
        var generic = DeviceFingerprint.FromHardware(GenericBoard, null, "MAQUINA");
        generic.Board.ShouldNotBeEmpty();
        generic.Matches(DeviceFingerprint.FromHardware(GenericBoard, null, "MAQUINA")).ShouldBeTrue();
    }

    [Fact]
    public void Con_placa_real_es_2_de_3_y_con_placa_generica_basta_el_disco()
    {
        var real = DeviceFingerprint.FromHardware("PLACA", "DISCO", "MAQUINA");
        DemoDeviceIdentity.SameDevice(real, DeviceFingerprint.FromHardware("PLACA", "DISCO", "WINDOWS-NUEVO")).ShouldBeTrue();
        DemoDeviceIdentity.SameDevice(real, DeviceFingerprint.FromHardware("PLACA", "OTRO", "OTRA")).ShouldBeFalse();

        // Placa genérica (dos PCs del mismo modelo la comparten): decide el disco; reinstalar Windows no importa.
        var vm1 = DeviceFingerprint.FromHardware(GenericBoard, "DISCO-1", "MAQUINA-1");
        DemoDeviceIdentity.SameDevice(vm1, DeviceFingerprint.FromHardware(GenericBoard, "DISCO-1", "MAQUINA-NUEVA")).ShouldBeTrue();
        DemoDeviceIdentity.SameDevice(vm1, DeviceFingerprint.FromHardware(GenericBoard, "DISCO-2", "MAQUINA-1")).ShouldBeFalse();

        // Sin placa ni disco útiles: el MachineGuid.
        var bare = DeviceFingerprint.FromHardware(GenericBoard, "0", "MAQUINA-1");
        DemoDeviceIdentity.IsUsable(bare).ShouldBeTrue();
        DemoDeviceIdentity.SameDevice(bare, DeviceFingerprint.FromHardware(GenericBoard, null, "MAQUINA-1")).ShouldBeTrue();
        DemoDeviceIdentity.IsUsable(DeviceFingerprint.FromHardware(GenericBoard, "0", null)).ShouldBeFalse();
    }

    [Fact]
    public void El_codigo_de_soporte_es_corto_y_estable()
    {
        var device = DeviceFingerprint.FromHardware("PLACA", "DISCO", "MAQUINA");
        var code = DemoDeviceIdentity.SupportCode(device);
        code.ShouldMatch("^[0-9A-F]{4}-[0-9A-F]{4}$");
        code.Replace("-", string.Empty, StringComparison.Ordinal).ShouldBe(device.Disk[..8].ToUpperInvariant());
        DemoDeviceIdentity.SupportCode(DeviceFingerprint.FromHardware("PLACA", "DISCO", "OTRA")).ShouldBe(code);
    }
}
