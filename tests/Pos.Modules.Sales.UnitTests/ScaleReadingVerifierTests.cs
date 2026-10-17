using System.Security.Cryptography;
using System.Text;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.SharedKernel.Measurement;
using static Pos.Modules.Sales.UnitTests.Fx;

namespace Pos.Modules.Sales.UnitTests;

/// <summary>D3: el servidor solo acepta lecturas de la báscula firmadas por el agente de la caja (ECDSA P-256).</summary>
public sealed class ScaleReadingVerifierTests : IDisposable
{
    private readonly ECDsa _agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ScaleReadingKey _registered;

    public ScaleReadingVerifierTests() => _registered = new ScaleReadingKey(Guid.NewGuid(), Convert.ToBase64String(_agentKey.ExportSubjectPublicKeyInfo()));

    public void Dispose() => _agentKey.Dispose();

    private static ScaleReadingPayload Payload(Guid? terminal = null, decimal gross = 0.7m) =>
        new(terminal ?? Terminal, Guid.NewGuid(), Now, Guid.NewGuid(), 3, 7, gross, 0m, gross, true, false, "OK", 5, true);

    private ScaleReadingInfo Signed(ScaleReadingPayload payload, ECDsa? key = null)
    {
        var text = payload.Format();
        return new ScaleReadingInfo(text, Convert.ToBase64String((key ?? _agentKey).SignData(
            Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    [Fact]
    public void Acepta_la_lectura_firmada_por_la_clave_de_la_caja()
    {
        var payload = Payload();

        var verified = ScaleReadingVerifier.Verify(Signed(payload), _registered, Terminal);

        verified.IsSuccess.ShouldBeTrue();
        verified.Value.Reading.ShouldBe(payload);
        verified.Value.KeyId.ShouldBe(_registered.KeyId);
    }

    [Fact]
    public void Rechaza_peso_cambiado_otra_clave_otra_caja_y_caja_sin_vincular()
    {
        var signed = Signed(Payload());

        // El navegador cambia el peso del texto firmado.
        var tampered = signed with { Payload = signed.Payload.Replace("|0.700|", "|0.100|", StringComparison.Ordinal) };
        ScaleReadingVerifier.Verify(tampered, _registered, Terminal).Error.ShouldBe(SalesErrors.ScaleReadingRequired);

        // Firmada con otra clave (otro agente o una clave inventada).
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ScaleReadingVerifier.Verify(Signed(Payload(), other), _registered, Terminal).Error.ShouldBe(SalesErrors.ScaleReadingRequired);

        // Lectura de otra caja, caja sin vincular, firma o texto ilegibles.
        ScaleReadingVerifier.Verify(Signed(Payload(Guid.NewGuid())), _registered, Terminal).Error.ShouldBe(SalesErrors.ScaleReadingRequired);
        ScaleReadingVerifier.Verify(signed, null, Terminal).Error.ShouldBe(SalesErrors.ScaleReadingRequired);
        ScaleReadingVerifier.Verify(signed with { Signature = "no-es-base64" }, _registered, Terminal).Error.ShouldBe(SalesErrors.ScaleReadingRequired);
        ScaleReadingVerifier.Verify(new ScaleReadingInfo("0.700", signed.Signature), _registered, Terminal).Error.ShouldBe(SalesErrors.ScaleReadingRequired);
    }

    [Fact]
    public void El_texto_canonico_va_y_vuelve_igual()
    {
        var payload = Payload() with { LastEmptySeq = null, EmptiedSinceLastAccepted = false, TareKg = 0.05m, NetKg = 0.65m };

        ScaleReadingPayload.TryParse(payload.Format()).ShouldBe(payload);
        ScaleReadingPayload.TryParse("BP-SCALE-3|x").ShouldBeNull();
        ScaleReadingPayload.TryParse(payload.Format().Replace("BP-SCALE-3", "BP-SCALE-2", StringComparison.Ordinal)).ShouldBeNull();
        ScaleReadingPayload.TryParse(payload.Format().Replace("|OK|", "|BIEN|", StringComparison.Ordinal)).ShouldBeNull();
    }
}
