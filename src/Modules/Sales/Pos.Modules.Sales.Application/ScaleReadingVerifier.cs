using System.Security.Cryptography;
using System.Text;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.SharedKernel.Measurement;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Application;

/// <summary>Lectura de la báscula verificada: su contenido y la clave con la que se firmó.</summary>
public sealed record VerifiedScaleReading(ScaleReadingPayload Reading, Guid KeyId);

/// <summary>
/// Verifica las lecturas firmadas de la báscula (Fase 7-C, D3): la firma ECDSA P-256 con la clave pública registrada para la caja al
/// vincularla, que la lectura sea de esa caja y que no se haya usado antes (nonce). Cualquier falla da
/// <c>SALES.SCALE_READING_REQUIRED</c> (o <c>SALES.SCALE_READING_REUSED</c> si la lectura ya se usó): sin una lectura válida no hay peso
/// de báscula; el peso digitado sigue por su ruta con permiso y auditoría.
/// </summary>
public sealed class ScaleReadingVerifier(ITerminalScaleKeys keys, ISalesStore store)
{
    public async Task<Result<VerifiedScaleReading>> VerifyAsync(ScaleReadingInfo? info, Guid posTerminalId, CancellationToken cancellationToken)
    {
        if (info is null)
        {
            return SalesErrors.ScaleReadingRequired;
        }

        var key = await keys.GetAsync(posTerminalId, cancellationToken);
        var verified = Verify(info, key, posTerminalId);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        return await store.ScaleReadingUsedAsync(verified.Value.Reading.ReadingId, cancellationToken) ? SalesErrors.ScaleReadingReused : verified.Value;
    }

    /// <summary>Firma y caja (sin tocar la base de datos).</summary>
    public static Result<VerifiedScaleReading> Verify(ScaleReadingInfo info, ScaleReadingKey? key, Guid posTerminalId)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (key is null || ScaleReadingPayload.TryParse(info.Payload) is not { } reading || reading.TerminalId != posTerminalId)
        {
            return SalesErrors.ScaleReadingRequired;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
            var valid = ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(info.Payload), Convert.FromBase64String(info.Signature ?? string.Empty), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return valid ? new VerifiedScaleReading(reading, key.KeyId) : SalesErrors.ScaleReadingRequired;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return SalesErrors.ScaleReadingRequired;
        }
    }
}
