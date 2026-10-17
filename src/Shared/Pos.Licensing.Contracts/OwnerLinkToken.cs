using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Licensing.Contracts;

/// <summary>
/// Contenido firmado del "enlace del propietario" (Fase 13-B, D13B-09): la nube confirma que <see cref="Email"/> es un usuario CLIENTE
/// del portal, dueño de la empresa <see cref="OrganizationNit"/>, y que escribió bien su contraseña. El POS lo verifica sin conexión con
/// las mismas claves públicas de la licencia y crea al Propietario con ese correo. Vida corta (<see cref="ExpiresAt"/>): solo sirve
/// durante la instalación. NO lleva la contraseña ni ningún hash de ella.
/// </summary>
public sealed record OwnerLinkClaims
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("ver")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Identificador único del enlace (auditoría).</summary>
    [JsonPropertyName("jti")]
    public required Guid TokenId { get; init; }

    /// <summary>Correo del usuario del portal, en minúsculas.</summary>
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("name")]
    public required string DisplayName { get; init; }

    /// <summary>NIT de la empresa de la licencia con su DV: <c>900123456-7</c>.</summary>
    [JsonPropertyName("org")]
    public required string OrganizationNit { get; init; }

    [JsonPropertyName("iat")]
    public required DateTimeOffset IssuedAt { get; init; }

    [JsonPropertyName("exp")]
    public required DateTimeOffset ExpiresAt { get; init; }
}

public enum OwnerLinkStatus
{
    Valid,
    Malformed,
    UnknownKey,
    InvalidSignature,
    UnsupportedVersion,

    /// <summary>Firma correcta pero vencido (o emitido en el futuro: reloj muy adelantado).</summary>
    Expired,
}

public sealed record OwnerLinkVerification(OwnerLinkStatus Status, OwnerLinkClaims? Claims)
{
    public bool IsValid => Status == OwnerLinkStatus.Valid;
}

/// <summary>
/// El enlace del propietario como JWS compacto EdDSA (igual que <see cref="LicenseToken"/>), con su propio tipo
/// <c>pos-owner-link+jws</c>: un token de licencia no sirve como enlace ni al revés.
/// </summary>
public static class OwnerLinkToken
{
    public const string Type = "pos-owner-link+jws";

    /// <summary>Vigencia del enlace: alcanza para terminar la instalación.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    /// <summary>Tolerancia de reloj entre la nube y el equipo.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        RespectRequiredConstructorParameters = true,
    };

    public static string Sign(OwnerLinkClaims claims, LicenseSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(key);
        var header = JsonSerializer.SerializeToUtf8Bytes(new Header(LicenseToken.Algorithm, key.Kid, Type), Json);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            claims with { IssuedAt = claims.IssuedAt.ToUniversalTime(), ExpiresAt = claims.ExpiresAt.ToUniversalTime() }, Json);
        var signingInput = $"{Base64Url.EncodeToString(header)}.{Base64Url.EncodeToString(payload)}";
        return $"{signingInput}.{Base64Url.EncodeToString(key.Sign(Encoding.ASCII.GetBytes(signingInput)))}";
    }

    public static OwnerLinkVerification Verify(string? token, LicenseKeyRing trustedKeys, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        var parts = token is { Length: <= LicenseToken.MaxLength } ? token.Split('.') : null;
        if (parts is not [var headerPart, var payloadPart, var signaturePart])
        {
            return new(OwnerLinkStatus.Malformed, null);
        }

        try
        {
            var header = JsonSerializer.Deserialize<Header>(Base64Url.DecodeFromChars(headerPart), Json);
            var signature = Base64Url.DecodeFromChars(signaturePart);
            if (header is not { Alg: LicenseToken.Algorithm, Typ: Type, Kid: { Length: > 0 } kid } || signature.Length != 64)
            {
                return new(OwnerLinkStatus.Malformed, null);
            }

            if (!trustedKeys.TryGet(kid, out var publicKey))
            {
                return new(OwnerLinkStatus.UnknownKey, null);
            }

            if (!publicKey.Verify(Encoding.ASCII.GetBytes($"{headerPart}.{payloadPart}"), signature))
            {
                return new(OwnerLinkStatus.InvalidSignature, null);
            }

            var payload = Base64Url.DecodeFromChars(payloadPart);
            using (var document = JsonDocument.Parse(payload))
            {
                if (!document.RootElement.TryGetProperty("ver", out var version) || !version.TryGetInt32(out var ver) || ver < 1)
                {
                    return new(OwnerLinkStatus.Malformed, null);
                }

                if (ver > OwnerLinkClaims.CurrentVersion)
                {
                    return new(OwnerLinkStatus.UnsupportedVersion, null);
                }
            }

            var claims = JsonSerializer.Deserialize<OwnerLinkClaims>(payload, Json);
            if (claims is null)
            {
                return new(OwnerLinkStatus.Malformed, null);
            }

            return now > claims.ExpiresAt + ClockSkew || now < claims.IssuedAt - ClockSkew
                ? new(OwnerLinkStatus.Expired, claims)
                : new(OwnerLinkStatus.Valid, claims);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return new(OwnerLinkStatus.Malformed, null);
        }
    }

    private sealed record Header(
        [property: JsonPropertyName("alg")] string Alg,
        [property: JsonPropertyName("kid")] string Kid,
        [property: JsonPropertyName("typ")] string Typ);
}
