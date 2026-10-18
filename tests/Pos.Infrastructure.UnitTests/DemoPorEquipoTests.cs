using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Infrastructure;

namespace Pos.Infrastructure.UnitTests;

/// <summary>Demo por equipo (docs/fases/fase-demo-por-equipo.md): marca del registro y huella estable.</summary>
public sealed class DemoPorEquipoTests : IDisposable
{
    private static readonly DeviceFingerprint Device = DeviceFingerprint.FromHardware("PLACA", "DISCO", "MAQUINA");

    // Las pruebas usan HKCU (sin permisos de administrador); en producción es HKLM con la ACL restringida.
    private readonly string _subKey = $@"Software\BusinessPost-Pruebas\{Guid.CreateVersion7():N}\Demo";

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(Path.GetDirectoryName(_subKey)!, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void La_marca_del_registro_guarda_el_inicio_mas_antiguo_y_solo_vale_para_este_equipo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var started = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var mark = new RegistryDeviceDemoMark(Registry.CurrentUser, _subKey, restrictAccess: false, NullLogger<RegistryDeviceDemoMark>.Instance);
        mark.Read(Device).ShouldBeNull();
        mark.Note(Device, started);
        mark.Note(Device, started.AddDays(10)); // más reciente: no la cambia

        // Otro proceso (reinstalación) la lee del registro, cifrada con DPAPI de la máquina.
        var reread = new RegistryDeviceDemoMark(Registry.CurrentUser, _subKey, restrictAccess: false, NullLogger<RegistryDeviceDemoMark>.Instance);
        reread.Read(Device).ShouldBe(started);
        reread.Read(DeviceFingerprint.FromHardware("PLACA", "DISCO", "WINDOWS-REINSTALADO")).ShouldBe(started); // 2 de 3
        reread.Read(DeviceFingerprint.FromHardware("OTRA", "OTRO", "MAQUINA")).ShouldBeNull();

        using (var key = Registry.CurrentUser.OpenSubKey(_subKey)!)
        {
            var raw = (byte[])key.GetValue("Mark")!;
            System.Text.Encoding.UTF8.GetString(raw).ShouldNotContain("fp1"); // no queda en claro
        }

        reread.Note(Device, started.AddDays(-2));
        new RegistryDeviceDemoMark(Registry.CurrentUser, _subKey, restrictAccess: false, NullLogger<RegistryDeviceDemoMark>.Instance)
            .Read(Device).ShouldBe(started.AddDays(-2));
    }

    [Fact]
    public void Una_marca_alterada_se_ignora()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (var key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            key.SetValue("Mark", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        }

        new RegistryDeviceDemoMark(Registry.CurrentUser, _subKey, restrictAccess: false, NullLogger<RegistryDeviceDemoMark>.Instance)
            .Read(Device).ShouldBeNull();
    }

    [Theory]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", null)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("03000200-0400-0500-0006-000700080009", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("4C4C4544-0042-3510-8052-B2C04F4B5A32", "4C4C4544-0042-3510-8052-B2C04F4B5A32")]
    public void Los_uuid_genericos_de_la_placa_no_identifican_el_equipo(string? uuid, string? expected) =>
        WindowsDeviceIdentity.BoardUuid(uuid).ShouldBe(expected);

    [Fact]
    public void La_marca_en_memoria_tambien_solo_adelanta_el_inicio()
    {
        var started = DateTimeOffset.UtcNow.AddDays(-40);
        var mark = new MemoryDeviceDemoMark(Device, started);
        mark.Note(Device, DateTimeOffset.UtcNow);
        mark.Read(Device).ShouldBe(started);
        mark.Read(DeviceFingerprint.FromHardware("X", "Y", "Z")).ShouldBeNull();
    }
}
