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
/// de supervisor) se hace en dos pasos (BK1): el agente prepara una clave ECDSA P-256 PENDIENTE (la vigente sigue firmando), la caja
/// registra la pública en el servidor y, solo si el servidor la aceptó, la caja confirma (<see cref="Commit"/>) y el agente la activa.
/// La pendiente se guarda en disco (BQ2): la confirmación vale aunque el agente se haya reiniciado entre el registro y la confirmación. Un
/// nuevo <see cref="Prepare"/> la reemplaza; si nunca se confirma, la caja sigue pesando con la clave vigente.
/// <para>Las claves (BB3/BK3) se guardan en la carpeta <c>keys</c> cifradas con DPAPI de la CUENTA del agente (el servicio corre como
/// SYSTEM; sin entropía fija); la carpeta y sus archivos tienen permisos solo para SYSTEM, Administradores y la cuenta del agente (sin
/// herencia) y se reparan al arrancar. Una clave del formato anterior (<c>dpapi:</c> con entropía fija) se borra al arrancar.</para>
/// <para>BQ3: también guarda en <c>keys</c> el número de arranque del agente, que sube en cada arranque y va firmado en cada lectura.</para>
/// </summary>
public sealed class ScaleSigner : IDisposable
{
    private readonly Lock _gate = new();
    private readonly string _folder;
    private readonly string _file;
    private readonly string _pendingFile;
    private readonly string _bootFile;
    private readonly ILogger<ScaleSigner> _logger;
    private ECDsa? _key;

    public ScaleSigner(IOptions<AgentOptions> options, ILogger<ScaleSigner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        var agentFolder = Path.GetDirectoryName(options.Value.ResolveScaleConfigFile())!;
        _folder = Path.Combine(agentFolder, "keys");
        _file = Path.Combine(_folder, "scale-signing.json");
        _pendingFile = Path.Combine(_folder, "scale-signing.pending.json");
        _bootFile = Path.Combine(_folder, "boot-counter.txt");
        RemoveLegacy(Path.Combine(agentFolder, "scale-signing.json"));
        RepairFolder();
        BootCounter = NextBootCounter();
        Load();
    }

    /// <summary>Caja con la que está vinculado el agente (nulo: sin vincular, las lecturas no se firman).</summary>
    public Guid? TerminalId { get; private set; }

    public Guid? KeyId { get; private set; }

    /// <summary>Número de este arranque del agente (BQ3): siempre mayor que el del arranque anterior.</summary>
    public long BootCounter { get; }

    /// <summary>Ruta del archivo de la clave vigente.</summary>
    public string KeyFile => _file;

    /// <summary>
    /// Prepara una clave PENDIENTE para la caja (reemplaza la pendiente anterior, si la había) y la guarda; devuelve la pública para
    /// registrarla en el servidor. La clave vigente no cambia.
    /// </summary>
    public ScalePairingDto Prepare(Guid terminalId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyId = Guid.CreateVersion7();
        lock (_gate)
        {
            Save(_pendingFile, new StoredKey(terminalId, keyId, Protect(key.ExportPkcs8PrivateKey())));
        }

        return new ScalePairingDto(terminalId, keyId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Pos.SharedKernel.Measurement.ScaleReadingPayload.Algorithm);
    }

    /// <summary>
    /// Activa la clave pendiente <paramref name="keyId"/> (el servidor ya la registró). Devuelve false si no hay una pendiente con ese
    /// identificador (otro intento la reemplazó o ya se confirmó). Todo pasa bajo el mismo candado que <see cref="Prepare"/> (BQ5).
    /// </summary>
    public bool Commit(Guid keyId)
    {
        lock (_gate)
        {
            if (Read(_pendingFile) is not { } pending || pending.KeyId != keyId)
            {
                return false;
            }

            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Unprotect(pending.PrivateKey), out _);
            File.Move(_pendingFile, _file, overwrite: true);
            _key?.Dispose();
            _key = key;
            TerminalId = pending.TerminalId;
            KeyId = pending.KeyId;
            return true;
        }
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

    private static StoredKey? Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<StoredKey>(File.ReadAllText(path), AgentEndpoints.Json) : null;

    /// <summary>
    /// BK3/BK5: carpeta <c>keys</c> con permisos solo para SYSTEM, Administradores y la cuenta del agente. Al arrancar se reparan la carpeta
    /// y sus archivos (por si alguien cambió los permisos).
    /// </summary>
    private void RepairFolder()
    {
        try
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
                return;
            }

            folder.SetAccessControl(RestrictedDirectorySecurity());
            foreach (var file in folder.GetFiles())
            {
                file.SetAccessControl(RestrictedFileSecurity());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            AgentLog.ScaleConfigUnreadable(_logger, ex, _folder);
        }
    }

    /// <summary>Escribe con permisos restringidos: borra un <c>.tmp</c> anterior (conservaría sus permisos) y lo reemplaza.</summary>
    private static void WriteRestricted(string path, string content)
    {
        var temporary = path + ".tmp";
        File.Delete(temporary);
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

        File.Move(temporary, path, overwrite: true);
    }

    private void Save(string path, StoredKey stored)
    {
        Directory.CreateDirectory(_folder);
        WriteRestricted(path, JsonSerializer.Serialize(stored, AgentEndpoints.Json));
    }

    /// <summary>BQ3: lee el número del último arranque, lo sube y lo guarda (si no se puede guardar, igual sube en memoria).</summary>
    private long NextBootCounter()
    {
        long previous = 0;
        try
        {
            if (File.Exists(_bootFile))
            {
                _ = long.TryParse(File.ReadAllText(_bootFile).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out previous);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AgentLog.ScaleConfigUnreadable(_logger, ex, _bootFile);
        }

        var next = previous + 1;
        try
        {
            Directory.CreateDirectory(_folder);
            WriteRestricted(_bootFile, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AgentLog.ScaleConfigUnreadable(_logger, ex, _bootFile);
        }

        return next;
    }

    /// <summary>BK3: la clave del formato anterior (DPAPI de máquina con entropía fija) se borra al arrancar.</summary>
    private void RemoveLegacy(string legacy)
    {
        try
        {
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
            }

            foreach (var file in new[] { _file, _pendingFile })
            {
                if (File.Exists(file) && File.ReadAllText(file).Contains("\"dpapi:", StringComparison.Ordinal))
                {
                    File.Delete(file);
                }
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
            if (Read(_file) is not { } stored)
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
}
