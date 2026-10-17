using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pos.Infrastructure.Security;

namespace Pos.Modules.Licensing.Infrastructure;

/// <summary>
/// Archivo protegido de la licencia (H4, docs/fases/fase-licencia-endurecimiento.md): <c>{DataRoot}\config\license.vault</c>, cifrado
/// con DPAPI de la máquina como <c>backup.key</c>. Guarda, FUERA de la BD, lo que no debe poder retroceder editando o restaurando la BD:
/// <list type="bullet">
/// <item>el secreto de 32 bytes con el que se firma (HMAC-SHA256) el estado sensible de <c>licensing.license_state</c>;</item>
/// <item>la mayor hora confiable vista (<c>watermark</c>) y la mayor emisión (<c>iat</c>) de un token ya confirmado;</item>
/// <item>el menor inicio de la demo visto, el piso de los días sin conexión y desde cuándo está restringida;</item>
/// <item>los indicadores que informó la nube (revocada, reactivación, liberado) hasta que un token nuevo los limpie;</item>
/// <item>si ya se LEYÓ una firma válida confirmada (desde entonces una fila sin firma es una alteración).</item>
/// </list>
/// Lo que depende de que la transacción se confirme (sellado, <c>iat</c>, inicio de la demo) solo se anota al LEER una fila firmada y
/// confirmada: una transacción revertida no deja el archivo adelantado. Sin carpeta de datos vive solo en memoria. Un administrador de
/// Windows puede leerlo o borrarlo: protege contra ediciones de la BD, no contra el dueño del equipo con permisos totales.
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
    private VaultState _state;
    private DateTimeOffset? _persistedWatermark;

    public LicenseVault(string? file, ILogger<LicenseVault> logger, int readAttempts = 3)
    {
        _file = file;
        _logger = logger;
        switch (file is null ? (LoadResult.Missing, (VaultState?)null) : Load(file, readAttempts))
        {
            case (LoadResult.Loaded, { } stored):
                _secret = Convert.FromBase64String(stored.Secret);
                _state = stored;
                _persistedWatermark = stored.Watermark;
                return;
            case (LoadResult.Unavailable, _):
                // El archivo existe pero no se pudo leer (bloqueado, disco): NO se sobrescribe. Se trabaja sin él hasta reiniciar: no se
                // firma nada nuevo y las firmas existentes no se pueden comprobar (restringida con la ventana normal de 24 h).
                Degraded = true;
                _secret = RandomNumberGenerator.GetBytes(32);
                _state = new VaultState(1, Convert.ToBase64String(_secret));
                return;
            default:
                _secret = RandomNumberGenerator.GetBytes(32);
                _state = new VaultState(1, Convert.ToBase64String(_secret));
                Save();
                return;
        }
    }

    private enum LoadResult
    {
        Missing,
        Loaded,
        Unavailable,
    }

    /// <summary>El archivo existe pero no se pudo leer: no se firma ni se escribe nada (el archivo queda intacto).</summary>
    public bool Degraded { get; }

    /// <summary>¿Se leyó ya una firma válida confirmada? Desde entonces una fila sin firma cuenta como alterada.</summary>
    public bool Sealed => Read(s => s.Sealed);

    /// <summary>Mayor hora confiable vista por este equipo según el archivo.</summary>
    public DateTimeOffset? Watermark => Read(s => s.Watermark);

    /// <summary>Mayor emisión de un token ya confirmado: un token guardado más viejo es una fila vieja vuelta a escribir.</summary>
    public DateTimeOffset? TokenIssuedAt => Read(s => s.TokenIssuedAt);

    /// <summary>Menor inicio de la demo visto (la demo no se reinicia recreando la fila).</summary>
    public DateTimeOffset? DemoStartedAt => Read(s => s.DemoStartedAt);

    /// <summary>Primer piso de los días sin conexión visto (no se puede correr hacia adelante).</summary>
    public DateTimeOffset? OfflineFloor => Read(s => s.OfflineFloor);

    /// <summary>Desde cuándo está restringida según el archivo (la ventana de 24 h no se reinicia volviendo a escribir una fila vieja).</summary>
    public DateTimeOffset? RestrictedSince => Read(s => s.RestrictedSince);

    public bool Revoked => Read(s => s.Revoked);

    public bool ReactivationRequired => Read(s => s.ReactivationRequired);

    public bool Deactivated => Read(s => s.Deactivated);

    /// <summary>Firma (hex) del texto canónico del estado.</summary>
    public string Mac(string canonical) => Convert.ToHexStringLower(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(canonical)));

    /// <summary>Se leyó una fila firmada, válida y CONFIRMADA: se anota lo que solo vale tras el commit.</summary>
    public void Confirm(DateTimeOffset? tokenIssuedAt, DateTimeOffset? demoStartedAt, DateTimeOffset? offlineFloor) =>
        Update(s => s with
        {
            Sealed = true,
            TokenIssuedAt = Max(s.TokenIssuedAt, tokenIssuedAt),
            DemoStartedAt = Min(s.DemoStartedAt, demoStartedAt),
            OfflineFloor = s.OfflineFloor ?? offlineFloor,
        });

    /// <summary>Sube la marca (nunca la baja). Se escribe en el archivo cuando avanzó al menos 5 minutos.</summary>
    public void Raise(DateTimeOffset value)
    {
        lock (_gate)
        {
            if (_state.Watermark is { } current && value <= current)
            {
                return;
            }

            _state = _state with { Watermark = value };
            if (_persistedWatermark is not { } persisted || value - persisted >= WriteEvery)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Token nuevo confirmado por la nube: la marca pasa a la hora confiable aunque sea menor (R2) y se limpian los indicadores. Si la
    /// transacción se revierte, la BD conserva sus indicadores y la marca solo quedó en una hora confiable: no se gana nada.
    /// </summary>
    public void TrustedToken(DateTimeOffset trustedNow) =>
        Update(s => s with { Watermark = trustedNow, Revoked = false, ReactivationRequired = false, Deactivated = false });

    /// <summary>Indicadores informados por la nube (o el equipo liberado): quedan hasta el próximo token.</summary>
    public void Flag(bool revoked = false, bool reactivationRequired = false, bool deactivated = false) =>
        Update(s => s with
        {
            Revoked = s.Revoked || revoked,
            ReactivationRequired = s.ReactivationRequired || reactivationRequired,
            Deactivated = s.Deactivated || deactivated,
        });

    /// <summary>Restringida desde (se conserva el primero) o <c>null</c> cuando dejó de estarlo.</summary>
    public void SetRestrictedSince(DateTimeOffset? since) =>
        Update(s => s with { RestrictedSince = since is null ? null : Min(s.RestrictedSince, since) });

    private T Read<T>(Func<VaultState, T> read)
    {
        lock (_gate)
        {
            return read(_state);
        }
    }

    private void Update(Func<VaultState, VaultState> change)
    {
        lock (_gate)
        {
            var next = change(_state);
            if (next != _state)
            {
                _state = next;
                Save();
            }
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : (a > b ? a : b);

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : (a < b ? a : b);

    private (LoadResult, VaultState?) Load(string file, int attempts)
    {
        if (!File.Exists(file))
        {
            return (LoadResult.Missing, null);
        }

        for (var attempt = 1; ; attempt++)
        {
            byte[] stored;
            try
            {
                stored = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Error transitorio (antivirus, disco): se reintenta y NUNCA se sobrescribe el archivo por esto.
                if (attempt >= attempts)
                {
                    LogUnavailable(_logger, file, ex.Message);
                    return (LoadResult.Unavailable, null);
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(200 * attempt));
                continue;
            }

            try
            {
                var plain = OperatingSystem.IsWindows() ? ProtectedSecret.UnprotectBytes(stored, Purpose) : stored;
                var vault = JsonSerializer.Deserialize<VaultState>(plain, Json) ?? throw new JsonException("Archivo vacío.");
                if (Convert.FromBase64String(vault.Secret).Length != 32)
                {
                    throw new JsonException("El secreto no tiene 32 bytes.");
                }

                return (LoadResult.Loaded, vault);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentNullException)
            {
                // Ilegible de verdad (otro equipo, dañado): se aparta como .bad y se crea uno nuevo; si la BD ya estaba sellada, la licencia
                // queda restringida (ventana de 24 h) hasta el check-in.
                LogUnreadable(_logger, file, ex.Message);
                try
                {
                    File.Move(file, $"{file}.{DateTime.UtcNow:yyyyMMddHHmmss}.bad", overwrite: true);
                }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                {
                    LogUnavailable(_logger, file, moveError.Message);
                    return (LoadResult.Unavailable, null);
                }

                return (LoadResult.Missing, null);
            }
        }
    }

    private void Save()
    {
        if (_file is null || Degraded)
        {
            return;
        }

        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(_state, Json);
            var data = OperatingSystem.IsWindows() ? ProtectedSecret.ProtectBytes(plain, Purpose) : plain;
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, _file, overwrite: true);
            _persistedWatermark = _state.Watermark;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            LogNotSaved(_logger, _file, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "El archivo de la licencia {File} está dañado o es de otro equipo; se aparta como .bad y se crea uno nuevo: {Error}")]
    private static partial void LogUnreadable(ILogger logger, string file, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo leer el archivo de la licencia {File}; se trabaja sin él hasta reiniciar: {Error}")]
    private static partial void LogUnavailable(ILogger logger, string file, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo guardar el archivo de la licencia {File}: {Error}")]
    private static partial void LogNotSaved(ILogger logger, string file, string error);

    private sealed record VaultState(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("secret")] string Secret,
        [property: JsonPropertyName("watermark")] DateTimeOffset? Watermark = null,
        [property: JsonPropertyName("sealed")] bool Sealed = false,
        [property: JsonPropertyName("iat")] DateTimeOffset? TokenIssuedAt = null,
        [property: JsonPropertyName("demo")] DateTimeOffset? DemoStartedAt = null,
        [property: JsonPropertyName("offlineFloor")] DateTimeOffset? OfflineFloor = null,
        [property: JsonPropertyName("restrictedSince")] DateTimeOffset? RestrictedSince = null,
        [property: JsonPropertyName("revoked")] bool Revoked = false,
        [property: JsonPropertyName("reactivation")] bool ReactivationRequired = false,
        [property: JsonPropertyName("deactivated")] bool Deactivated = false);
}
