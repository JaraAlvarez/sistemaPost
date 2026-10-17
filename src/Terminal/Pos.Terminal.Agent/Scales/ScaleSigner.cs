using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pos.Terminal.Agent.Api;

namespace Pos.Terminal.Agent.Scales;

/// <summary><c>POST /scale/pairing</c>: prepara una clave PENDIENTE para la caja (la vigente sigue firmando hasta confirmar).</summary>
public sealed record ScalePairingRequest(Guid TerminalId);

/// <summary><c>POST /scale/pairing/commit</c>: el servidor ya registró la clave pendiente; el agente la activa.</summary>
public sealed record ScalePairingCommitRequest(Guid KeyId);

/// <summary>Clave pública de la báscula de la caja para registrarla en el servidor (la privada nunca sale del agente).</summary>
public sealed record ScalePairingDto(Guid TerminalId, Guid KeyId, string PublicKey, string Algorithm);

/// <summary>Lectura firmada que la interfaz reenvía al servidor tal cual: el texto canónico y su firma (Base64, formato IEEE P1363).</summary>
public sealed record SignedScaleReading(string Payload, string Signature, Guid KeyId);

/// <summary>
/// Firma las lecturas de la báscula (Fase 7-C, decisión D3 de la revisión). Vincular (F1 → Vincular báscula, en la caja con autorización
/// de supervisor) se hace en dos pasos (BK1): el agente prepara una clave ECDSA P-256 PENDIENTE (solo en memoria; la vigente sigue
/// firmando), la caja registra la pública en el servidor y, solo si el servidor la aceptó, la caja confirma
/// (<see cref="Commit"/>) y el agente la activa y la guarda. Si el registro falla o se cancela, la pendiente se descarta (otro
/// <see cref="Prepare"/> o un reinicio del agente) y la caja sigue pesando con la clave vigente.
/// <para>La privada (BB3/BK3) se guarda en <c>keys\scale-signing.json</c> cifrada con DPAPI de la CUENTA del agente (el servicio corre
/// como SYSTEM; sin entropía fija) y la carpeta <c>keys</c> y el archivo tienen permisos solo para SYSTEM, Administradores y la cuenta del
/// agente (sin herencia). Una clave del formato anterior (<c>dpapi:</c> con entropía fija) se borra al arrancar: hay que volver a
/// vincular. Si cambia la cuenta del servicio también hay que volver a vincular.</para>
/// </summary>
public sealed class ScaleSigner : IDisposable
{
    private readonly Lock _gate = new();
    private readonly string _folder;
    private readonly string _file;
    private readonly ILogger<ScaleSigner> _logger;
    private ECDsa? _key;
    private Pending? _pending;

    public ScaleSigner(IOptions<AgentOptions> options, ILogger<ScaleSigner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        var agentFolder = Path.GetDirectoryName(options.Value.ResolveScaleConfigFile())!;
        _folder = Path.Combine(agentFolder, "keys");
        _file = Path.Combine(_folder, "scale-signing.json");
        RemoveLegacy(Path.Combine(agentFolder, "scale-signing.json"));
        Load();
    }

    /// <summary>Caja con la que está vinculado el agente (nulo: sin vincular, las lecturas no se firman).</summary>
    public Guid? TerminalId { get; private set; }

    public Guid? KeyId { get; private set; }

    /// <summary>Ruta del archivo de la clave vigente.</summary>
    public string KeyFile => _file;

    /// <summary>
    /// Prepara una clave PENDIENTE para la caja (descarta la pendiente anterior, si la había) y devuelve la pública para registrarla en el
    /// servidor. La clave vigente no cambia.
    /// </summary>
    public ScalePairingDto Prepare(Guid terminalId)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyId = Guid.CreateVersion7();
        lock (_gate)
        {
            _pending?.Key.Dispose();
            _pending = new Pending(terminalId, keyId, key);
        }

        return new ScalePairingDto(terminalId, keyId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Pos.SharedKernel.Measurement.ScaleReadingPayload.Algorithm);
    }

    /// <summary>
    /// Activa la clave pendiente <paramref name="keyId"/> (el servidor ya la registró) y la guarda. Devuelve false si no hay una pendiente
    /// con ese identificador (otra la reemplazó o el agente se reinició).
    /// </summary>
    public bool Commit(Guid keyId)
    {
        Pending pending;
        lock (_gate)
        {
            if (_pending is not { } p || p.KeyId != keyId)
            {
                return false;
            }

            pending = p;
        }

        Save(new StoredKey(pending.TerminalId, pending.KeyId, Protect(pending.Key.ExportPkcs8PrivateKey())));
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending))
            {
                return false;
            }

            _key?.Dispose();
            _key = pending.Key;
            TerminalId = pending.TerminalId;
            KeyId = pending.KeyId;
            _pending = null;
        }

        return true;
    }

    /// <summary>Firma el texto canónico de una lectura con la clave VIGENTE; nulo si el agente no está vinculado.</summary>
    public SignedScaleReading? Sign(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_gate)
        {
            if (_key is null || KeyId is not { } keyId)
            {
                return null;
            }

            var signature = _key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new SignedScaleReading(payload, Convert.ToBase64String(signature), keyId);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _key?.Dispose();
            _key = null;
            _pending?.Key.Dispose();
            _pending = null;
        }
    }

    [SupportedOSPlatform("windows")]
    public static FileSecurity RestrictedFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AllowedSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    [SupportedOSPlatform("windows")]
    public static DirectorySecurity RestrictedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AllowedSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    [SupportedOSPlatform("windows")]
    private static List<SecurityIdentifier> AllowedSids()
    {
        using var current = WindowsIdentity.GetCurrent();
        return new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), current.User! }
            .Distinct()
            .ToList();
    }

    private static string Protect(byte[] plain) =>
        OperatingSystem.IsWindows()
            ? "dpapi-user:" + Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser))
            : "plain:" + Convert.ToBase64String(plain);

    private static byte[] Unprotect(string text) => text switch
    {
        _ when text.StartsWith("dpapi-user:", StringComparison.Ordinal) && OperatingSystem.IsWindows() =>
            ProtectedData.Unprotect(Convert.FromBase64String(text["dpapi-user:".Length..]), null, DataProtectionScope.CurrentUser),
        _ when text.StartsWith("plain:", StringComparison.Ordinal) => Convert.FromBase64String(text[6..]),
        _ => throw new CryptographicException("Formato de clave desconocido."),
    };

    /// <summary>Carpeta <c>keys</c> con permisos solo para SYSTEM, Administradores y la cuenta del agente (BK3/BK5).</summary>
    private void EnsureFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_folder);
            return;
        }

        var folder = new DirectoryInfo(_folder);
        if (!folder.Exists)
        {
            folder.Create(RestrictedDirectorySecurity());
        }
        else
        {
            folder.SetAccessControl(RestrictedDirectorySecurity());
        }
    }

    /// <summary>Escribe la clave con permisos restringidos: borra un <c>.tmp</c> anterior (conservaría sus permisos) y lo reemplaza.</summary>
    private void Save(StoredKey stored)
    {
        EnsureFolder();
        var temporary = _file + ".tmp";
        File.Delete(temporary);
        var content = JsonSerializer.Serialize(stored, AgentEndpoints.Json);
        if (OperatingSystem.IsWindows())
        {
            using var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, RestrictedFileSecurity());
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }
        else
        {
            File.WriteAllText(temporary, content);
        }

        File.Move(temporary, _file, overwrite: true);
    }

    /// <summary>BK3: la clave del formato anterior (DPAPI de máquina con entropía fija, fuera de <c>keys</c>) se borra al arrancar.</summary>
    private void RemoveLegacy(string legacy)
    {
        try
        {
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
            }

            if (File.Exists(_file) && File.ReadAllText(_file).Contains("\"dpapi:", StringComparison.Ordinal))
            {
                File.Delete(_file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AgentLog.ScaleConfigUnreadable(_logger, ex, legacy);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<StoredKey>(File.ReadAllText(_file), AgentEndpoints.Json);
            if (stored is null)
            {
                return;
            }

            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Unprotect(stored.PrivateKey), out _);
            _key = key;
            TerminalId = stored.TerminalId;
            KeyId = stored.KeyId;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            // Clave ilegible (otro equipo, archivo dañado): hay que volver a vincular la caja.
            AgentLog.ScaleConfigUnreadable(_logger, ex, _file);
        }
    }

    private sealed record StoredKey(Guid TerminalId, Guid KeyId, string PrivateKey);

    private sealed record Pending(Guid TerminalId, Guid KeyId, ECDsa Key);
}
