using Pos.Licensing.Contracts;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Licensing.Domain;

/// <summary>
/// Clave pública de firma registrada (L-04). La privada NUNCA está en la BD: vive en un archivo protegido del servidor.
/// Ciclo: STANDBY (reserva publicada, el POS ya la trae embebida) → ACTIVE (firma hoy; solo una) → RETIRED (ya no firma,
/// sigue verificando tokens anteriores). REVOKED = comprometida: deja de ser de confianza.
/// </summary>
public sealed class SigningKey : Entity<string>
{
    public const string EdDsa = "EdDSA";

    private SigningKey(string id, string algorithm, string publicKey, DateTimeOffset createdAt)
        : base(id)
    {
        Algorithm = algorithm;
        PublicKey = publicKey;
        CreatedAt = createdAt;
    }

    public string Kid => Id;

    public string Algorithm { get; private set; }

    /// <summary>Clave pública cruda en base64url (43 caracteres).</summary>
    public string PublicKey { get; private set; }

    public SigningKeyStatus Status { get; private set; } = SigningKeyStatus.Standby;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? RetiredAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>¿Se aceptan los tokens firmados con ella? Todas menos las revocadas.</summary>
    public bool IsTrusted => Status != SigningKeyStatus.Revoked;

    public static Result<SigningKey> Register(string publicKey, DateTimeOffset now)
    {
        if (!LicensePublicKey.TryParse(publicKey, out var parsed))
        {
            return Error.Validation("LICENSING.SIGNING_KEY_INVALID", "La clave pública no es una clave Ed25519 en base64url (43 caracteres).");
        }

        return new SigningKey(parsed.Kid, EdDsa, parsed.X, now);
    }

    public Result Activate(DateTimeOffset now)
    {
        if (Status != SigningKeyStatus.Standby)
        {
            return LicensingErrors.SigningKeyTransition;
        }

        Status = SigningKeyStatus.Active;
        ActivatedAt = now;
        return Result.Success();
    }

    public Result Retire(DateTimeOffset now)
    {
        if (Status != SigningKeyStatus.Active)
        {
            return LicensingErrors.SigningKeyTransition;
        }

        Status = SigningKeyStatus.Retired;
        RetiredAt = now;
        return Result.Success();
    }

    /// <summary>Revoca una clave comprometida. La activa no se revoca directamente: primero se rota a otra.</summary>
    public Result Revoke(DateTimeOffset now)
    {
        if (Status is SigningKeyStatus.Active or SigningKeyStatus.Revoked)
        {
            return LicensingErrors.SigningKeyTransition;
        }

        Status = SigningKeyStatus.Revoked;
        RevokedAt = now;
        return Result.Success();
    }
}
