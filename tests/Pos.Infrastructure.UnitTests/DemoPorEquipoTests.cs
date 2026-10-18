using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;
using Pos.Modules.Licensing.Infrastructure;

namespace Pos.Infrastructure.UnitTests;

/// <summary>Demo por equipo (docs/fases/fase-demo-por-equipo.md): marca del registro, equipo para la demo y huella de la licencia intacta.</summary>
public sealed class DemoPorEquipoTests : IDisposable
{
    private static readonly DeviceFingerprint Device = DeviceFingerprint.FromHardware("PLACA", "DISCO", "MAQUINA");
    private static readonly DateTimeOffset Build = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

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
    public void La_marca_del_registro_guarda_el_inicio_mas_antiguo_la_primera_compilacion_y_solo_vale_para_este_equipo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var started = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var mark = Mark();
        mark.Read(Device).ShouldBeNull();
        mark.Note(Device, started, Build);
        mark.Note(Device, started.AddDays(10), Build.AddDays(30)); // más reciente / compilación nueva: no cambian

        var reread = Mark();
        reread.Read(Device).ShouldBe(new DemoMarkEntry(started, Build));
        reread.Read(DeviceFingerprint.FromHardware("PLACA", "DISCO", "WINDOWS-REINSTALADO")).ShouldNotBeNull(); // 2 de 3
        reread.Read(DeviceFingerprint.FromHardware("OTRA", "OTRO", "MAQUINA")).ShouldBeNull();

        using (var key = Registry.CurrentUser.OpenSubKey(_subKey)!)
        {
            System.Text.Encoding.UTF8.GetString((byte[])key.GetValue("Mark")!).ShouldNotContain("fp1"); // no queda en claro
        }

        // Soporte reinició la demo en la nube: la marca se reemplaza aunque el inicio sea posterior.
        reread.Note(Device, started.AddDays(40), Build, reset: true);
        Mark().Read(Device)!.StartedAt.ShouldBe(started.AddDays(40));
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

        Mark().Read(Device).ShouldBeNull();
    }

    [Fact]
    public void Si_no_se_puede_escribir_la_marca_se_recuerda_y_se_reintenta()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Raíz abierta solo para lectura: la escritura falla.
        var parent = Path.GetDirectoryName(_subKey)!;
        Registry.CurrentUser.CreateSubKey(parent).Dispose();
        using var readOnly = Registry.CurrentUser.OpenSubKey(parent, writable: false)!;
        var blocked = new RegistryDeviceDemoMark(readOnly, "Demo", restrictAccess: false, NullLogger<RegistryDeviceDemoMark>.Instance);
        blocked.Note(Device, Build, Build);
        blocked.HasPending.ShouldBeTrue();
        blocked.Read(Device).ShouldNotBeNull(); // lo pendiente vale mientras tanto
        blocked.RetryNow();
        blocked.HasPending.ShouldBeTrue();

        var mark = Mark();
        mark.Note(Device, Build, Build);
        mark.HasPending.ShouldBeFalse();
    }

    [Fact]
    public void La_marca_en_memoria_tambien_solo_adelanta_el_inicio()
    {
        var started = DateTimeOffset.UtcNow.AddDays(-40);
        var mark = new MemoryDeviceDemoMark(Device, started);
        mark.Note(Device, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        mark.Read(Device).ShouldBe(new DemoMarkEntry(started, started));
        mark.Read(DeviceFingerprint.FromHardware("X", "Y", "Z")).ShouldBeNull();
    }

    [Fact]
    public void Una_licencia_activada_con_la_placa_generica_y_sin_serie_de_disco_sigue_valida()
    {
        // La exclusión de valores genéricos es SOLO de la demo: la huella de la licencia es la de siempre.
        var generic = DeviceFingerprint.FromHardware("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", null, "MAQUINA-1");
        generic.IsUsable.ShouldBeTrue();
        generic.Board.ShouldNotBeEmpty();
        using var key = LicenseSigningKey.Generate();
        var at = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var node = Guid.CreateVersion7();
        var token = LicenseToken.Sign(new LicenseClaims
        {
            LicenseId = Guid.CreateVersion7(), OrganizationNit = "900123456-7", OrganizationName = "Tienda", InstallationId = node,
            DeviceFingerprint = generic.ToString(), DeviceRole = DeviceRoles.AllInOne, Edition = LicenseEditions.SingleTerminal,
            SubscriptionStatus = SubscriptionStatuses.Active, IssuedAt = at, ValidUntil = at.AddDays(30), GraceDays = 7, RefreshAfter = at.AddDays(1),
        }, key);
        var record = new LicenseRecord(node, token, "POS-ABCDE", at, at, null, false, false, false, 0, at, at);

        LicenseEvaluator.Evaluate(record, LicenseToken.Verify(token, new LicenseKeyRing([key.PublicKey])), generic, DeviceRoles.AllInOne, at.AddHours(1))
            .State.ShouldBe(LicenseStates.Valid);

        // Para la demo, en cambio, la placa genérica no cuenta (queda solo el MachineGuid).
        DemoDeviceIdentity.Normalize(generic).AvailableComponents.ShouldBe(1);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private RegistryDeviceDemoMark Mark() => new(Registry.CurrentUser, _subKey, restrictAccess: false, NullLogger<RegistryDeviceDemoMark>.Instance);
}
