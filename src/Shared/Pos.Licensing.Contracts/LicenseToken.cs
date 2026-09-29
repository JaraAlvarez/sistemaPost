using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Licensing.Contracts;

/// <summary>Resultado de verificar un token.</summary>
public enum LicenseTokenStatus
{
    Valid,

    /// <summary>No es un JWS compacto EdDSA de licencia o su contenido no se puede leer.</summary>
    Malformed,

    /// <summary>El <c>kid</c> no está entre las claves de confianza (clave desconocida o revocada).</summary>
    UnknownKey,

    /// <summary>La firma no corresponde: el token fue alterado.</summary>
    InvalidSignature,

    /// <summary>Firma válida, pero con una versión de contenido que este programa no conoce (hay que actualizar).</summary>
    UnsupportedVersion,
}

public sealed record LicenseTokenVerification(LicenseTokenStatus Status, LicenseClaims? Claims, string? Kid)
{
    public bool IsValid => Status == LicenseTokenStatus.Valid;
}

/// <summary>
/// Token de licencia como JWS compacto (RFC 7515) con EdDSA/Ed25519 (RFC 8037):
/// <c>base64url(cabecera).base64url(contenido).base64url(firma)</c>, cabecera <c>{"alg":"EdDSA","kid":"…","typ":"pos-license+jws"}</c>.
/// Se verifica sin conexión con las claves públicas de confianza; la validez en el tiempo la decide quien lo usa
/// (<see cref="LicenseClaims.ValidUntil"/>, gracia), no la firma.
/// </summary>
public static class LicenseToken
{
    public const string Algorithm = "EdDSA";

    public const string Type = "pos-license+jws";

    /// <summary>Tamaño máximo aceptado (defensa ante entradas enormes).</summary>
    public const int MaxLength = 8 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        RespectRequiredConstructorParameters = true,
    };

    public static string Sign(LicenseClaims claims, LicenseSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(key);
        var header = JsonSerializer.SerializeToUtf8Bytes(new Header(Algorithm, key.Kid, Type), Json);
        var normalized = claims with
        {
            IssuedAt = claims.IssuedAt.ToUniversalTime(),
            ValidUntil = claims.ValidUntil.ToUniversalTime(),
            RefreshAfter = claims.RefreshAfter.ToUniversalTime(),
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(normalized, Json);
        var signingInput = $"{Base64Url.EncodeToString(header)}.{Base64Url.EncodeToString(payload)}";
        var signature = key.Sign(Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    public static LicenseTokenVerification Verify(string? token, LicenseKeyRing trustedKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        var parts = token is { Length: <= MaxLength } ? token.Split('.') : null;
        if (parts is not [var headerPart, var payloadPart, var signaturePart])
        {
            return Fail(LicenseTokenStatus.Malformed);
        }

        Header? header;
        byte[] signature;
        try
        {
            header = JsonSerializer.Deserialize<Header>(Base64Url.DecodeFromChars(headerPart), Json);
            signature = Base64Url.DecodeFromChars(signaturePart);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return Fail(LicenseTokenStatus.Malformed);
        }

        if (header is not { Alg: Algorithm, Typ: Type, Kid: { Length: > 0 } kid } || signature.Length != 64)
        {
            return Fail(LicenseTokenStatus.Malformed);
        }

        if (!trustedKeys.TryGet(kid, out var publicKey))
        {
            return new LicenseTokenVerification(LicenseTokenStatus.UnknownKey, null, kid);
        }

        if (!publicKey.Verify(Encoding.ASCII.GetBytes($"{headerPart}.{payloadPart}"), signature))
        {
            return new LicenseTokenVerification(LicenseTokenStatus.InvalidSignature, null, kid);
        }

        try
        {
            var payload = Base64Url.DecodeFromChars(payloadPart);
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("ver", out var version) || !version.TryGetInt32(out var ver) || ver < 1)
            {
                return new LicenseTokenVerification(LicenseTokenStatus.Malformed, null, kid);
            }

            if (ver > LicenseClaims.CurrentVersion)
            {
                return new LicenseTokenVerification(LicenseTokenStatus.UnsupportedVersion, null, kid);
            }

            var claims = JsonSerializer.Deserialize<LicenseClaims>(payload, Json);
            return claims is null
                ? new LicenseTokenVerification(LicenseTokenStatus.Malformed, null, kid)
                : new LicenseTokenVerification(LicenseTokenStatus.Valid, claims, kid);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return new LicenseTokenVerification(LicenseTokenStatus.Malformed, null, kid);
        }
    }

    private static LicenseTokenVerification Fail(LicenseTokenStatus status) => new(status, null, null);

    private sealed record Header(
        [property: JsonPropertyName("alg")] string Alg,
        [property: JsonPropertyName("kid")] string Kid,
        [property: JsonPropertyName("typ")] string Typ);
}
