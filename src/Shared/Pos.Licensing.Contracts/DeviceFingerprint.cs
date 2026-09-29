using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Pos.Licensing.Contracts;

/// <summary>
/// Huella del equipo servidor (L-07): tres componentes estables del hardware (serie de la placa base, serie del disco del
/// sistema y MachineGuid de Windows), cada uno guardado solo como hash. Se compara componente por componente con tolerancia
/// de 2 de 3: cambiar un disco no obliga a reactivar; copiar la instalación a otro PC sí.
/// Forma textual: <c>fp1.&lt;placa&gt;.&lt;disco&gt;.&lt;maquina&gt;</c> (32 hex por componente; <c>-</c> si no se pudo leer).
/// </summary>
public sealed record DeviceFingerprint
{
    public const string FormatVersion = "fp1";

    /// <summary>Componentes que deben coincidir para considerar que es el mismo equipo.</summary>
    public const int RequiredMatches = 2;

    public const int ComponentLength = 32;

    private const string Missing = "-";

    public DeviceFingerprint(string board, string disk, string machine)
    {
        Board = ValidateComponent(board, nameof(board));
        Disk = ValidateComponent(disk, nameof(disk));
        Machine = ValidateComponent(machine, nameof(machine));
    }

    /// <summary>Hash de la serie de la placa base ("" si no está disponible).</summary>
    public string Board { get; }

    /// <summary>Hash de la serie del disco del sistema ("" si no está disponible).</summary>
    public string Disk { get; }

    /// <summary>Hash del identificador de la máquina (MachineGuid) ("" si no está disponible).</summary>
    public string Machine { get; }

    /// <summary>Componentes con valor. Una huella con menos de <see cref="RequiredMatches"/> no sirve para activar.</summary>
    public int AvailableComponents => new[] { Board, Disk, Machine }.Count(c => c.Length > 0);

    public bool IsUsable => AvailableComponents >= RequiredMatches;

    /// <summary>Calcula la huella a partir de los valores crudos del hardware (lo hace el POS; los valores nunca salen del equipo).</summary>
    public static DeviceFingerprint FromHardware(string? boardSerial, string? diskSerial, string? machineGuid) =>
        new(HashComponent("board", boardSerial), HashComponent("disk", diskSerial), HashComponent("machine", machineGuid));

    public static bool TryParse(string? text, [NotNullWhen(true)] out DeviceFingerprint? fingerprint)
    {
        fingerprint = null;
        var parts = text?.Split('.');
        if (parts is not [FormatVersion, var board, var disk, var machine])
        {
            return false;
        }

        string? Component(string part) => part == Missing ? string.Empty : IsHash(part) ? part : null;
        if (Component(board) is not { } b || Component(disk) is not { } d || Component(machine) is not { } m)
        {
            return false;
        }

        fingerprint = new DeviceFingerprint(b, d, m);
        return true;
    }

    /// <summary>Componentes con valor que coinciden con otra huella (un componente vacío nunca cuenta como coincidencia).</summary>
    public int MatchingComponents(DeviceFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Same(Board, other.Board) + Same(Disk, other.Disk) + Same(Machine, other.Machine);
    }

    /// <summary>¿Es el mismo equipo? Al menos 2 de los 3 componentes coinciden.</summary>
    public bool Matches(DeviceFingerprint other) => MatchingComponents(other) >= RequiredMatches;

    public override string ToString() =>
        $"{FormatVersion}.{Show(Board)}.{Show(Disk)}.{Show(Machine)}";

    private static int Same(string left, string right) => left.Length > 0 && string.Equals(left, right, StringComparison.Ordinal) ? 1 : 0;

    private static string Show(string component) => component.Length == 0 ? Missing : component;

    private static string HashComponent(string name, string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"pos-fp-v1|{name}|{normalized}"));
        return Convert.ToHexStringLower(hash)[..ComponentLength];
    }

    private static bool IsHash(string value) =>
        value.Length == ComponentLength && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static string ValidateComponent(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return value.Length == 0 || IsHash(value)
            ? value
            : throw new ArgumentException("Cada componente de la huella es un hash hexadecimal de 32 caracteres o vacío.", name);
    }
}
