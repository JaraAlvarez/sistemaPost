using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pos.Infrastructure.Security;
using Pos.Licensing.Contracts;

namespace Pos.Modules.Licensing.Infrastructure;

/// <summary>
/// Marca del equipo con el primer inicio de la demo (docs/fases/fase-demo-por-equipo.md §2.1): sobrevive a desinstalar borrando los datos
/// de <c>ProgramData</c>. Solo puede ADELANTAR el inicio de la demo, nunca atrasarlo.
/// </summary>
internal interface IDeviceDemoMark
{
    /// <summary>Inicio guardado para este equipo (la huella guardada debe coincidir, 2 de 3); <c>null</c> si no hay.</summary>
    DateTimeOffset? Read(DeviceFingerprint device);

    /// <summary>Guarda el inicio si no había o si es más antiguo que el guardado.</summary>
    void Note(DeviceFingerprint device, DateTimeOffset startedAt);
}

/// <summary>En memoria del proceso: desarrollo (Debug) y pruebas, para no dejar una demo vencida en el equipo del desarrollador.</summary>
internal sealed class MemoryDeviceDemoMark : IDeviceDemoMark
{
    private readonly Lock _gate = new();
    private (DeviceFingerprint Device, DateTimeOffset StartedAt)? _mark;

    /// <summary>Para las pruebas: un equipo que ya tuvo una demo (reinstalado borrando los datos).</summary>
    public MemoryDeviceDemoMark(DeviceFingerprint? device = null, DateTimeOffset? startedAt = null)
    {
        if (device is not null && startedAt is { } start)
        {
            _mark = (device, start);
        }
    }

    public DateTimeOffset? Read(DeviceFingerprint device)
    {
        lock (_gate)
        {
            return _mark is { } mark && mark.Device.Matches(device) ? mark.StartedAt : null;
        }
    }

    public void Note(DeviceFingerprint device, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            if (_mark is not { } mark || !mark.Device.Matches(device) || startedAt < mark.StartedAt)
            {
                _mark = (device, startedAt);
            }
        }
    }
}

/// <summary>
/// Marca en el registro de Windows: <c>HKLM\SOFTWARE\{Producto}\Demo</c>, valor <c>Mark</c> cifrado con DPAPI de la MÁQUINA (inicio y
/// huella). La clave se crea sin herencia y solo SYSTEM y Administradores la pueden leer o escribir; el desinstalador no la borra.
/// Se lee una vez y se escribe solo cuando el inicio conocido es más antiguo. Un fallo del registro nunca detiene la licencia.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class RegistryDeviceDemoMark : IDeviceDemoMark
{
    // Identificador criptográfico interno y estable (no es el nombre comercial).
    public const string Purpose = "PosSupermercado.Licensing.DemoMark.v1";

    private const string ValueName = "Mark";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RegistryKey _root;
    private readonly string _subKey;
    private readonly bool _restrictAccess;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private (DeviceFingerprint Device, DateTimeOffset StartedAt)? _mark;
    private bool _loaded;

    /// <param name="root">Raíz del registro (HKLM en producción; HKCU en las pruebas).</param>
    /// <param name="subKey">Por ejemplo <c>SOFTWARE\BusinessPost\Demo</c>.</param>
    /// <param name="restrictAccess">Crear la clave solo para SYSTEM y Administradores (producción).</param>
    /// <param name="logger">Registro de los fallos (nunca detienen la licencia).</param>
    public RegistryDeviceDemoMark(RegistryKey root, string subKey, bool restrictAccess, ILogger<RegistryDeviceDemoMark> logger)
    {
        _root = root;
        _subKey = subKey;
        _restrictAccess = restrictAccess;
        _logger = logger;
    }

    public DateTimeOffset? Read(DeviceFingerprint device)
    {
        lock (_gate)
        {
            Load();
            return _mark is { } mark && mark.Device.Matches(device) ? mark.StartedAt : null;
        }
    }

    public void Note(DeviceFingerprint device, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            Load();
            if (_mark is { } mark && mark.Device.Matches(device) && mark.StartedAt <= startedAt)
            {
                return;
            }

            try
            {
                using var key = _restrictAccess
                    ? _root.CreateSubKey(_subKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RestrictedSecurity())
                    : _root.CreateSubKey(_subKey, writable: true);
                var plain = JsonSerializer.SerializeToUtf8Bytes(new Stored(startedAt.ToUniversalTime(), device.ToString()), Json);
                key.SetValue(ValueName, ProtectedSecret.ProtectBytes(plain, Purpose), RegistryValueKind.Binary);
                _mark = (device, startedAt);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or CryptographicException)
            {
                LogNotSaved(_logger, _subKey, ex.Message);
            }
        }
    }

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            using var key = _root.OpenSubKey(_subKey, writable: false);
            if (key?.GetValue(ValueName) is not byte[] data)
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<Stored>(ProtectedSecret.UnprotectBytes(data, Purpose), Json);
            if (stored is not null && DeviceFingerprint.TryParse(stored.Fingerprint, out var device))
            {
                _mark = (device, stored.StartedAt);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or CryptographicException
            or JsonException)
        {
            // Ilegible (otra instalación de Windows, alterada): no cuenta; la nube sigue siendo la protección principal.
            LogUnreadable(_logger, _subKey, ex.Message);
        }
    }

    private static RegistrySecurity RestrictedSecurity()
    {
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new RegistryAccessRule(
                new SecurityIdentifier(sid, null), RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo guardar la marca de la demo en el registro ({Key}): {Error}")]
    private static partial void LogNotSaved(ILogger logger, string key, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La marca de la demo del registro ({Key}) no se pudo leer y se ignora: {Error}")]
    private static partial void LogUnreadable(ILogger logger, string key, string error);

    private sealed record Stored(
        [property: JsonPropertyName("start")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("fp")] string Fingerprint);
}
