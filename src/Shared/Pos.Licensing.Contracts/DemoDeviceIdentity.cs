namespace Pos.Licensing.Contracts;

/// <summary>
/// Cómo se reconoce un equipo para la DEMO (docs/fases/fase-demo-por-equipo.md §3). Solo para la demo: la huella de la LICENCIA
/// (<see cref="DeviceFingerprint.Matches"/>, 2 de 3) no cambia, para no dejar sin licencia a una tienda que paga con una placa genérica.
/// <list type="bullet">
/// <item>Los valores genéricos que dejan muchos fabricantes y las máquinas virtuales (UUID de placa todo ceros o todo F, el de ejemplo
/// de SMBIOS; series de disco "0000_0000…", "To be filled by O.E.M.", …) no identifican el equipo: cuentan como no disponibles.</item>
/// <item>Con la placa disponible en los dos lados: 2 de 3, como la licencia. Si falta en alguno (genérica): basta que coincida la serie
/// del disco; si tampoco hay disco, el <c>MachineGuid</c>.</item>
/// </list>
/// Funciona sobre los hashes (los valores crudos nunca salen del equipo): los genéricos se reconocen comparando con el hash de cada uno.
/// </summary>
public static class DemoDeviceIdentity
{
    private static readonly HashSet<string> GenericBoards = Hashes(
        v => DeviceFingerprint.FromHardware(v, null, null).Board,
        "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", "00000000-0000-0000-0000-000000000000", "03000200-0400-0500-0006-000700080009",
        "00020003-0004-0005-0006-000700080009", "12345678-1234-5678-90AB-CDDEEFAABBCC", "To be filled by O.E.M.", "Default string", "None",
        "Not Specified", "System Serial Number");

    private static readonly HashSet<string> GenericDisks = Hashes(
        v => DeviceFingerprint.FromHardware(null, v, null).Disk,
        "0", "00000000", "0000000000000000", "00000000000000000000", "0000_0000_0000_0000", "0000_0000_0000_0000.", "0000_0000_0000_0001.",
        "To be filled by O.E.M.", "Default string", "None", "Not Specified", "Not Applicable", "123456789", "1234567890", "SerialNumber",
        "System Serial Number");

    /// <summary>La huella sin los componentes genéricos.</summary>
    public static DeviceFingerprint Normalize(DeviceFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return new DeviceFingerprint(
            GenericBoards.Contains(fingerprint.Board) ? string.Empty : fingerprint.Board,
            GenericDisks.Contains(fingerprint.Disk) ? string.Empty : fingerprint.Disk,
            fingerprint.Machine);
    }

    /// <summary>¿Sirve para la demo? Al menos un componente no genérico.</summary>
    public static bool IsUsable(DeviceFingerprint fingerprint) => Normalize(fingerprint).AvailableComponents >= 1;

    /// <summary>¿Es el mismo equipo para la demo? (reglas en el resumen de la clase).</summary>
    public static bool SameDevice(DeviceFingerprint a, DeviceFingerprint b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Board.Length > 0 && y.Board.Length > 0)
        {
            return x.Matches(y);
        }

        if (x.Disk.Length > 0 && y.Disk.Length > 0)
        {
            return string.Equals(x.Disk, y.Disk, StringComparison.Ordinal);
        }

        return x.Machine.Length > 0 && string.Equals(x.Machine, y.Machine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Código corto del equipo para soporte (<c>XXXX-XXXX</c>): los primeros 8 caracteres del hash del disco (o de la placa, o del
    /// MachineGuid). El portal lo busca para "Reiniciar la demo de este equipo". No revela nada del hardware.
    /// </summary>
    public static string SupportCode(DeviceFingerprint fingerprint)
    {
        var x = Normalize(fingerprint);
        var source = x.Disk.Length > 0 ? x.Disk : x.Board.Length > 0 ? x.Board : x.Machine;
        return source.Length < 8 ? "—" : $"{source[..4]}-{source[4..8]}".ToUpperInvariant();
    }

    private static HashSet<string> Hashes(Func<string, string> hash, params string[] values) =>
        [.. values.Select(hash).Where(h => h.Length > 0)];
}
