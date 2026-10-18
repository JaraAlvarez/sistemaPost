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

/// <summary>Lo que recuerda la marca del equipo: inicio de la demo y la compilación MÁS ANTIGUA vista (piso de lo creíble).</summary>
internal sealed record DemoMarkEntry(DateTimeOffset StartedAt, DateTimeOffset? FirstBuild);

/// <summary>
/// Marca del equipo con el primer inicio de la demo (docs/fases/fase-demo-por-equipo.md §2.1): sobrevive a desinstalar borrando los datos
/// de <c>ProgramData</c>. Compara equipos con <see cref="DemoDeviceIdentity.SameDevice"/> (solo la demo; la licencia no cambia).
/// </summary>
internal interface IDeviceDemoMark
{
    /// <summary>Lo guardado para este equipo; <c>null</c> si no hay o es de otro equipo.</summary>
    DemoMarkEntry? Read(DeviceFingerprint device);

    /// <summary>
    /// Guarda el inicio si no había, si es de otro equipo o si es más antiguo; la compilación más antigua vista se conserva. Con
    /// <paramref name="reset"/> (soporte reinició la demo en la nube) el inicio se REEMPLAZA aunque sea posterior.
    /// </summary>
    void Note(DeviceFingerprint device, DateTimeOffset startedAt, DateTimeOffset? build, bool reset = false);
}

internal static class DemoMarks
{
    /// <summary>Lo que hay que guardar (o <c>null</c> si lo guardado ya está al día).</summary>
    public static (DeviceFingerprint Device, DemoMarkEntry Entry)? Next(
        (DeviceFingerprint Device, DemoMarkEntry Entry)? current, DeviceFingerprint device, DateTimeOffset startedAt, DateTimeOffset? build, bool reset)
    {
        if (current is not { } mark || !DemoDeviceIdentity.SameDevice(mark.Device, device))
        {
            return (device, new DemoMarkEntry(startedAt, build));
        }

        var firstBuild = mark.Entry.FirstBuild is { } known && (build is null || known <= build) ? known : build;
        var start = reset || startedAt < mark.Entry.StartedAt ? startedAt : mark.Entry.StartedAt;
        var next = new DemoMarkEntry(start, firstBuild);
        return next == mark.Entry ? null : (device, next);
    }
}

/// <summary>En memoria del proceso: desarrollo (Debug) y pruebas, para no dejar una demo vencida en el equipo del desarrollador.</summary>
internal sealed class MemoryDeviceDemoMark : IDeviceDemoMark
{
    private readonly Lock _gate = new();
    private (DeviceFingerprint Device, DemoMarkEntry Entry)? _mark;

    /// <summary>Para las pruebas: un equipo que ya tuvo una demo (reinstalado borrando los datos), con un programa de esa época.</summary>
    public MemoryDeviceDemoMark(DeviceFingerprint? device = null, DateTimeOffset? startedAt = null, DateTimeOffset? firstBuild = null)
    {
        if (device is not null && startedAt is { } start)
        {
            _mark = (device, new DemoMarkEntry(start, firstBuild ?? start));
        }
    }

    public DemoMarkEntry? Read(DeviceFingerprint device)
    {
        lock (_gate)
        {
            return _mark is { } mark && DemoDeviceIdentity.SameDevice(mark.Device, device) ? mark.Entry : null;
        }
    }

    public void Note(DeviceFingerprint device, DateTimeOffset startedAt, DateTimeOffset? build, bool reset = false)
    {
        lock (_gate)
        {
            if (DemoMarks.Next(_mark, device, startedAt, build, reset) is { } next)
            {
                _mark = next;
            }
        }
    }
}

/// <summary>
/// Marca en el registro de Windows: <c>HKLM\SOFTWARE\{Producto}\Demo</c>, valor <c>Mark</c> cifrado con DPAPI de la MÁQUINA (inicio,
/// primera compilación vista y huella). La clave queda sin herencia y solo SYSTEM y Administradores la pueden leer o escribir: la ACL se
/// aplica al crearla y se VUELVE A APLICAR si ya existía (una vez por proceso). El desinstalador no la borra. Si no se puede escribir, se
/// recuerda lo pendiente y se reintenta con espera creciente (1 min → 1 h). Un fallo del registro nunca detiene la licencia.
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
    private (DeviceFingerprint Device, DemoMarkEntry Entry)? _mark;
    private (DeviceFingerprint Device, DemoMarkEntry Entry)? _pending;
    private bool _loaded;
    private bool _aclChecked;
    private long _retryAt;
    private int _failures;

    /// <param name="root">Raíz del registro (HKLM en producción; HKCU en las pruebas).</param>
    /// <param name="subKey">Por ejemplo <c>SOFTWARE\BusinessPost\Demo</c>.</param>
    /// <param name="restrictAccess">Dejar la clave solo para SYSTEM y Administradores (producción).</param>
    /// <param name="logger">Registro de los fallos (nunca detienen la licencia).</param>
    public RegistryDeviceDemoMark(RegistryKey root, string subKey, bool restrictAccess, ILogger<RegistryDeviceDemoMark> logger)
    {
        _root = root;
        _subKey = subKey;
        _restrictAccess = restrictAccess;
        _logger = logger;
    }

    /// <summary>¿Hay una escritura pendiente por un fallo anterior? (pruebas y diagnóstico).</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    public DemoMarkEntry? Read(DeviceFingerprint device)
    {
        lock (_gate)
        {
            Load();
            var current = _pending ?? _mark;
            return current is { } mark && DemoDeviceIdentity.SameDevice(mark.Device, device) ? mark.Entry : null;
        }
    }

    public void Note(DeviceFingerprint device, DateTimeOffset startedAt, DateTimeOffset? build, bool reset = false)
    {
        lock (_gate)
        {
            Load();
            if (DemoMarks.Next(_pending ?? _mark, device, startedAt, build, reset) is { } next)
            {
                _pending = next;
            }

            if (_pending is { } pending && Environment.TickCount64 >= _retryAt)
            {
                Write(pending);
            }
        }
    }

    /// <summary>Para las pruebas: reintentar ya, sin esperar.</summary>
    public void RetryNow()
    {
        lock (_gate)
        {
            _retryAt = 0;
            if (_pending is { } pending)
            {
                Write(pending);
            }
        }
    }

    private void Write((DeviceFingerprint Device, DemoMarkEntry Entry) mark)
    {
        try
        {
            using var key = OpenForWrite();
            var plain = JsonSerializer.SerializeToUtf8Bytes(
                new Stored(mark.Entry.StartedAt.ToUniversalTime(), mark.Entry.FirstBuild?.ToUniversalTime(), mark.Device.ToString()), Json);
            key.SetValue(ValueName, ProtectedSecret.ProtectBytes(plain, Purpose), RegistryValueKind.Binary);
            _mark = mark;
            _pending = null;
            _failures = 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or CryptographicException)
        {
            // Se recuerda lo pendiente (Read lo sigue devolviendo) y se reintenta con espera creciente.
            _failures++;
            var wait = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(_failures - 1, 6))));
            _retryAt = Environment.TickCount64 + (long)wait.TotalMilliseconds;
            LogNotSaved(_logger, _subKey, (int)wait.TotalMinutes, ex.Message);
        }
    }

    private RegistryKey OpenForWrite()
    {
        if (!_restrictAccess)
        {
            return _root.CreateSubKey(_subKey, writable: true);
        }

        if (_root.OpenSubKey(_subKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadKey | RegistryRights.WriteKey | RegistryRights.ChangePermissions)
            is { } existing)
        {
            // Ya existía (creada por otro, o con otra ACL): se vuelve a dejar solo para SYSTEM y Administradores.
            if (!_aclChecked)
            {
                existing.SetAccessControl(RestrictedSecurity());
                _aclChecked = true;
            }

            return existing;
        }

        _aclChecked = true;
        return _root.CreateSubKey(_subKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RestrictedSecurity());
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
                _mark = (device, new DemoMarkEntry(stored.StartedAt, stored.FirstBuild));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo guardar la marca de la demo en el registro ({Key}); se reintenta en {Minutes} min: {Error}")]
    private static partial void LogNotSaved(ILogger logger, string key, int minutes, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La marca de la demo del registro ({Key}) no se pudo leer y se ignora: {Error}")]
    private static partial void LogUnreadable(ILogger logger, string key, string error);

    private sealed record Stored(
        [property: JsonPropertyName("start")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("build")] DateTimeOffset? FirstBuild,
        [property: JsonPropertyName("fp")] string Fingerprint);
}
