using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Licensing.Contracts;

/// <summary>
/// Registro de la demo por equipo (docs/fases/fase-demo-por-equipo.md): el POS sin licencia informa la huella de su equipo y el inicio
/// local de la demo; la nube guarda el PRIMER inicio que vio para esa huella (2 de 3 componentes) y lo devuelve firmado. Anónimo, con
/// el límite por IP de la API del POS.
/// </summary>
/// <param name="Fingerprint">Huella del equipo (<see cref="DeviceFingerprint"/>, solo hashes).</param>
/// <param name="LocalStartedAt">Inicio de la demo que conoce el POS (puede ser más reciente si se reinstaló borrando los datos).</param>
/// <param name="BuildDate">
/// Fecha de compilación del POS: la demo no pudo empezar antes de que existiera el programa, así que un inicio anterior (reloj atrasado al
/// instalar) no es creíble y la nube no lo registra.
/// </param>
public sealed record DemoDeviceRequest(string Fingerprint, DateTimeOffset? LocalStartedAt, DateTimeOffset? BuildDate = null);

/// <summary>Respuesta: el token firmado (<see cref="DemoToken"/>) con el primer inicio de la demo de este equipo.</summary>
public sealed record DemoDeviceResponse(string Token);

/// <summary>Contenido firmado del registro de la demo de un equipo.</summary>
public sealed record DemoClaims
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("ver")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Huella con la que el POS consultó (el POS comprueba que es la suya, 2 de 3).</summary>
    [JsonPropertyName("dev")]
    public required string DeviceFingerprint { get; init; }

    /// <summary>Primer inicio de la demo que la nube conoce para este equipo.</summary>
    [JsonPropertyName("started_at")]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("iat")]
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// Soporte reinició la demo de este equipo ("Reiniciar la demo de este equipo" en el portal): el POS anula sus marcas locales y usa
    /// <see cref="StartedAt"/> aunque sea posterior a lo que recuerda (p. ej. un reloj atrasado al instalar dejó la demo vencida).
    /// </summary>
    [JsonPropertyName("reset")]
    public bool Reset { get; init; }
}

public enum DemoTokenStatus
{
    Valid,
    Malformed,
    UnknownKey,
    InvalidSignature,
    UnsupportedVersion,
}

public sealed record DemoTokenVerification(DemoTokenStatus Status, DemoClaims? Claims)
{
    public bool IsValid => Status == DemoTokenStatus.Valid;
}

/// <summary>
/// El registro de la demo como JWS compacto EdDSA, igual que <see cref="LicenseToken"/> pero con su propio tipo <c>pos-demo+jws</c>:
/// no sirve como token de licencia ni como enlace del propietario, ni al revés.
/// </summary>
public static class DemoToken
{
    public const string Type = "pos-demo+jws";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        RespectRequiredConstructorParameters = true,
    };

    public static string Sign(DemoClaims claims, LicenseSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(key);
        var header = JsonSerializer.SerializeToUtf8Bytes(new Header(LicenseToken.Algorithm, key.Kid, Type), Json);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            claims with { StartedAt = claims.StartedAt.ToUniversalTime(), IssuedAt = claims.IssuedAt.ToUniversalTime() }, Json);
        var signingInput = $"{Base64Url.EncodeToString(header)}.{Base64Url.EncodeToString(payload)}";
        return $"{signingInput}.{Base64Url.EncodeToString(key.Sign(Encoding.ASCII.GetBytes(signingInput)))}";
    }

    public static DemoTokenVerification Verify(string? token, LicenseKeyRing trustedKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        var parts = token is { Length: <= LicenseToken.MaxLength } ? token.Split('.') : null;
        if (parts is not [var headerPart, var payloadPart, var signaturePart])
        {
            return new(DemoTokenStatus.Malformed, null);
        }

        try
        {
            var header = JsonSerializer.Deserialize<Header>(Base64Url.DecodeFromChars(headerPart), Json);
            var signature = Base64Url.DecodeFromChars(signaturePart);
            if (header is not { Alg: LicenseToken.Algorithm, Typ: Type, Kid: { Length: > 0 } kid } || signature.Length != 64)
            {
                return new(DemoTokenStatus.Malformed, null);
            }

            if (!trustedKeys.TryGet(kid, out var publicKey))
            {
                return new(DemoTokenStatus.UnknownKey, null);
            }

            if (!publicKey.Verify(Encoding.ASCII.GetBytes($"{headerPart}.{payloadPart}"), signature))
            {
                return new(DemoTokenStatus.InvalidSignature, null);
            }

            var payload = Base64Url.DecodeFromChars(payloadPart);
            using (var document = JsonDocument.Parse(payload))
            {
                if (!document.RootElement.TryGetProperty("ver", out var version) || !version.TryGetInt32(out var ver) || ver < 1)
                {
                    return new(DemoTokenStatus.Malformed, null);
                }

                if (ver > DemoClaims.CurrentVersion)
                {
                    return new(DemoTokenStatus.UnsupportedVersion, null);
                }
            }

            return JsonSerializer.Deserialize<DemoClaims>(payload, Json) is { } claims
                ? new(DemoTokenStatus.Valid, claims)
                : new(DemoTokenStatus.Malformed, null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return new(DemoTokenStatus.Malformed, null);
        }
    }

    private sealed record Header(
        [property: JsonPropertyName("alg")] string Alg,
        [property: JsonPropertyName("kid")] string Kid,
        [property: JsonPropertyName("typ")] string Typ);
}
