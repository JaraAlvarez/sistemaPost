using System.Text;
using Pos.Cloud.PortalIdentity.Domain;

namespace Pos.Cloud.PortalIdentity.UnitTests;

public class TotpTests
{
    /// <summary>Secreto de los vectores de prueba de RFC 6238 (apéndice B) para HMAC-SHA1.</summary>
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>
    /// Vectores del RFC 6238 (SHA1). El RFC publica códigos de 8 dígitos; con 6 dígitos el código es el mismo valor truncado
    /// módulo 10^6, es decir, sus 6 últimos dígitos.
    /// </summary>
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Coincide_con_los_vectores_de_prueba_del_RFC_6238(long unixSeconds, string rfcCode)
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var step = Totp.StepAt(instant);

        step.ShouldBe(unixSeconds / 30);
        Totp.ComputeCode(RfcSecret, step).ShouldBe(rfcCode[^6..]);
        Totp.Verify(RfcSecret, rfcCode[^6..], instant, lastUsedStep: null).ShouldBe(step);
    }

    [Fact]
    public void Acepta_un_paso_antes_o_despues_y_rechaza_mas_alla()
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var step = Totp.StepAt(instant);
        var code = Totp.ComputeCode(RfcSecret, step);

        Totp.Verify(RfcSecret, code, instant.AddSeconds(-30), null).ShouldBe(step);
        Totp.Verify(RfcSecret, code, instant.AddSeconds(30), null).ShouldBe(step);
        Totp.Verify(RfcSecret, code, instant.AddSeconds(-60), null).ShouldBeNull();
        Totp.Verify(RfcSecret, code, instant.AddSeconds(60), null).ShouldBeNull();
    }

    [Fact]
    public void El_mismo_codigo_no_sirve_dos_veces()
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var step = Totp.StepAt(instant);
        var code = Totp.ComputeCode(RfcSecret, step);

        Totp.Verify(RfcSecret, code, instant, lastUsedStep: step).ShouldBeNull();
        Totp.Verify(RfcSecret, code, instant, lastUsedStep: step + 1).ShouldBeNull();
        Totp.Verify(RfcSecret, code, instant, lastUsedStep: step - 1).ShouldBe(step);

        var next = Totp.ComputeCode(RfcSecret, step + 1);
        Totp.Verify(RfcSecret, next, instant, lastUsedStep: step).ShouldBe(step + 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("000000")]
    public void Rechaza_codigos_mal_formados_o_incorrectos(string? code) =>
        Totp.Verify(RfcSecret, code, DateTimeOffset.FromUnixTimeSeconds(59), null).ShouldBeNull();

    [Fact]
    public void Ignora_los_espacios_que_escribe_el_usuario() =>
        Totp.Verify(RfcSecret, "287 082", DateTimeOffset.FromUnixTimeSeconds(59), null).ShouldBe(1);

    [Fact]
    public void El_secreto_generado_tiene_160_bits_aleatorios()
    {
        var first = Totp.GenerateSecret();

        first.Length.ShouldBe(Totp.SecretBytes);
        Totp.GenerateSecret().ShouldNotBe(first);
    }

    /// <summary>Vectores de RFC 4648 §10 (sin relleno).</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_sigue_el_RFC_4648(string text, string base32)
    {
        Totp.ToBase32(Encoding.ASCII.GetBytes(text)).ShouldBe(base32);
        Totp.FromBase32(base32).ShouldBe(Encoding.ASCII.GetBytes(text));
    }

    [Fact]
    public void Base32_acepta_minusculas_espacios_y_relleno()
    {
        Totp.ToBase32(RfcSecret).ShouldBe("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        Totp.FromBase32(" gezd gnbv gy3t qojq gezd gnbv gy3t qojq ").ShouldBe(RfcSecret);
        Totp.FromBase32("MZXW6===").ShouldBe(Encoding.ASCII.GetBytes("foo"));
        var secret = Totp.GenerateSecret();
        Totp.FromBase32(Totp.ToBase32(secret)).ShouldBe(secret);
    }

    [Fact]
    public void Base32_rechaza_simbolos_fuera_del_alfabeto()
    {
        Should.Throw<FormatException>(() => Totp.FromBase32("MZXW1"));
        Should.Throw<ArgumentNullException>(() => Totp.FromBase32(null!));
    }

    [Fact]
    public void El_enlace_otpauth_lleva_emisor_cuenta_secreto_y_parametros()
    {
        var link = Totp.EnrollmentLink("POS Nube", "ana@ejemplo.co", RfcSecret);

        link.ShouldBe(
            "otpauth://totp/POS%20Nube%3Aana%40ejemplo.co?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=POS%20Nube&algorithm=SHA1&digits=6&period=30");
        Should.Throw<ArgumentException>(() => Totp.EnrollmentLink(" ", "ana@ejemplo.co", RfcSecret));
        Should.Throw<ArgumentException>(() => Totp.EnrollmentLink("POS Nube", "", RfcSecret));
    }
}
