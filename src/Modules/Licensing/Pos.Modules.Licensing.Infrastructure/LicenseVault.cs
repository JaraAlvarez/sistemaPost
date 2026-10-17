using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pos.Infrastructure.Security;

namespace Pos.Modules.Licensing.Infrastructure;

/// <summary>
/// Archivo protegido de la licencia (H4, docs/fases/fase-licencia-endurecimiento.md): <c>{DataRoot}\config\license.vault</c>, cifrado
/// con DPAPI de la máquina como <c>backup.key</c>. Guarda, FUERA de la BD:
/// <list type="bullet">
/// <item>el secreto de 32 bytes con el que se firma (HMAC-SHA256) el estado sensible de <c>licensing.license_state</c>;</item>
/// <item>la mayor hora confiable vista (<c>watermark</c>): al leer se usa <c>max(BD, archivo)</c>, así restaurar un backup viejo o
/// editar la BD no retrocede la marca de tiempo;</item>
/// <item>si ya se selló alguna vez el estado (desde entonces una fila sin firma es una alteración, no una instalación anterior).</item>
/// </list>
/// Sin carpeta de datos (pruebas de otros módulos) vive solo en memoria. Un administrador de Windows puede leerlo o borrarlo: protege
/// contra ediciones de la BD, no contra el dueño del equipo con permisos totales.
/// </summary>
internal sealed partial class LicenseVault
{
    // Identificador criptográfico interno y estable (no es el nombre comercial): cambiarlo dejaría ilegible el archivo ya cifrado.
    public const string Purpose = "PosSupermercado.Licensing.Vault.v1";

    /// <summary>La marca se escribe en el archivo cuando avanzó al menos esto (la tolerancia del retroceso es de 24 h).</summary>
    private static readonly TimeSpan WriteEvery = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string? _file;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly byte[] _secret;
    private DateTimeOffset? _watermark;
    private DateTimeOffset? _persistedWatermark;
    private bool _sealed;

    public LicenseVault(string? file, ILogger<LicenseVault> logger)
    {
        _file = file;
        _logger = logger;
        if (file is not null && TryLoad(file) is { } stored)
        {
            _secret = Convert.FromBase64String(stored.Secret);
            _watermark = _persistedWatermark = stored.Watermark;
            _sealed = stored.Sealed;
            return;
        }

        _secret = RandomNumberGenerator.GetBytes(32);
        Save();
    }

    /// <summary>¿Se selló ya el estado con este secreto? Desde entonces una fila sin firma cuenta como alterada.</summary>
    public bool Sealed
    {
        get
        {
            lock (_gate)
            {
                return _sealed;
            }
        }
    }

    /// <summary>Mayor hora confiable vista por este equipo según el archivo.</summary>
    public DateTimeOffset? Watermark
    {
        get
        {
            lock (_gate)
            {
                return _watermark;
            }
        }
    }

    /// <summary>Firma (hex) del texto canónico del estado.</summary>
    public string Mac(string canonical) => Convert.ToHexStringLower(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(canonical)));

    public void MarkSealed()
    {
        lock (_gate)
        {
            if (_sealed)
            {
                return;
            }

            _sealed = true;
            Save();
        }
    }

    /// <summary>Sube la marca (nunca la baja). Se escribe en el archivo cuando avanzó al menos 5 minutos.</summary>
    public void Raise(DateTimeOffset value)
    {
        lock (_gate)
        {
            if (_watermark is { } current && value <= current)
            {
                return;
            }

            _watermark = value;
            if (_persistedWatermark is not { } persisted || value - persisted >= WriteEvery)
            {
                Save();
            }
        }
    }

    /// <summary>Fija la marca a la hora confiable de la nube aunque sea menor (R2: corrige un reloj que se adelantó por error).</summary>
    public void Reset(DateTimeOffset trustedNow)
    {
        lock (_gate)
        {
            _watermark = trustedNow;
            Save();
        }
    }

    private VaultFile? TryLoad(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var stored = File.ReadAllBytes(file);
            var plain = OperatingSystem.IsWindows() ? ProtectedSecret.UnprotectBytes(stored, Purpose) : stored;
            return JsonSerializer.Deserialize<VaultFile>(plain, Json) is { Secret.Length: > 0 } vault ? vault : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException)
        {
            // Ilegible (otro equipo, dañado): se crea uno nuevo; si la BD ya estaba sellada, la licencia queda restringida hasta el check-in.
            LogUnreadable(_logger, file, ex.Message);
            return null;
        }
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(new VaultFile(1, Convert.ToBase64String(_secret), _watermark, _sealed), Json);
            var data = OperatingSystem.IsWindows() ? ProtectedSecret.ProtectBytes(plain, Purpose) : plain;
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, _file, overwrite: true);
            _persistedWatermark = _watermark;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            LogNotSaved(_logger, _file, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "El archivo de la licencia {File} no se pudo leer y se crea uno nuevo: {Error}")]
    private static partial void LogUnreadable(ILogger logger, string file, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo guardar el archivo de la licencia {File}: {Error}")]
    private static partial void LogNotSaved(ILogger logger, string file, string error);

    private sealed record VaultFile(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("secret")] string Secret,
        [property: JsonPropertyName("watermark")] DateTimeOffset? Watermark,
        [property: JsonPropertyName("sealed")] bool Sealed);
}
