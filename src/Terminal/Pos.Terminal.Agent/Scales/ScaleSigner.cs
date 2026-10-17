using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pos.Terminal.Agent.Api;

namespace Pos.Terminal.Agent.Scales;

/// <summary><c>POST /scale/pairing</c>: vincula el agente con una caja (crea una clave nueva; la anterior deja de valer).</summary>
public sealed record ScalePairingRequest(Guid TerminalId);

/// <summary>Clave pública de la báscula de la caja para registrarla en el servidor (la privada nunca sale del agente).</summary>
public sealed record ScalePairingDto(Guid TerminalId, Guid KeyId, string PublicKey, string Algorithm);

/// <summary>Lectura firmada que la interfaz reenvía al servidor tal cual: el texto canónico y su firma (Base64, formato IEEE P1363).</summary>
public sealed record SignedScaleReading(string Payload, string Signature, Guid KeyId);

/// <summary>
/// Firma las lecturas de la báscula (Fase 7-C, decisión D3 de la revisión). Al vincular la caja (Configuración → Cajas → Báscula →
/// Guardar, en el computador de la caja) el agente crea una clave ECDSA P-256: la privada se guarda protegida con DPAPI de la máquina en
/// <c>scale-signing.json</c> (junto a <c>scale.json</c>) y la pública se registra en el servidor. Así la interfaz (o alguien con las
/// herramientas del navegador) no puede inventar ni cambiar un peso: el servidor solo acepta lecturas firmadas por la clave de esa caja.
/// Volver a vincular crea otra clave (rotación) y la anterior deja de valer en el servidor al registrar la nueva.
/// </summary>
public sealed class ScaleSigner : IDisposable
{
    // Identificador criptográfico interno y estable: cambiarlo dejaría ilegible la clave guardada.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BusinessPost.ScaleReadings.v1");

    private readonly Lock _gate = new();
    private readonly string _file;
    private readonly ILogger<ScaleSigner> _logger;
    private ECDsa? _key;

    public ScaleSigner(IOptions<AgentOptions> options, ILogger<ScaleSigner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _file = Path.Combine(Path.GetDirectoryName(options.Value.ResolveScaleConfigFile())!, "scale-signing.json");
        Load();
    }

    /// <summary>Caja con la que está vinculado el agente (nulo: sin vincular, las lecturas no se firman).</summary>
    public Guid? TerminalId { get; private set; }

    public Guid? KeyId { get; private set; }

    /// <summary>Crea una clave nueva para la caja y la guarda; devuelve la pública para registrarla en el servidor.</summary>
    public ScalePairingDto Pair(Guid terminalId)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyId = Guid.CreateVersion7();
        var stored = new StoredKey(terminalId, keyId, Protect(key.ExportPkcs8PrivateKey()));
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stored, AgentEndpoints.Json));
        File.Move(temporary, _file, overwrite: true);
        lock (_gate)
        {
            _key?.Dispose();
            _key = key;
            TerminalId = terminalId;
            KeyId = keyId;
        }

        return new ScalePairingDto(terminalId, keyId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Pos.SharedKernel.Measurement.ScaleReadingPayload.Algorithm);
    }

    /// <summary>Firma el texto canónico de una lectura; nulo si el agente no está vinculado.</summary>
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

    private static string Protect(byte[] plain) =>
        OperatingSystem.IsWindows()
            ? "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine))
            : "plain:" + Convert.ToBase64String(plain);

    private static byte[] Unprotect(string text) => text switch
    {
        _ when text.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows() =>
            ProtectedData.Unprotect(Convert.FromBase64String(text[6..]), Entropy, DataProtectionScope.LocalMachine),
        _ when text.StartsWith("plain:", StringComparison.Ordinal) => Convert.FromBase64String(text[6..]),
        _ => throw new CryptographicException("Formato de clave desconocido."),
    };

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
}
