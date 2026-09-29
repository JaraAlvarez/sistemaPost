using System.Diagnostics;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Security;
using Pos.SharedKernel.Security;

namespace Pos.Infrastructure.UnitTests;

/// <summary>Argon2id en formato PHC (D3-02) y tokens opacos (D3-01).</summary>
public class SecretHasherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hash_en_formato_PHC_con_sal_unica_y_verificacion()
    {
        using var hasher = new Argon2idSecretHasher();

        var first = await hasher.HashAsync("Supermercado-2026!", SecretKind.Password, Ct);
        var second = await hasher.HashAsync("Supermercado-2026!", SecretKind.Password, Ct);

        first.ShouldStartWith("$argon2id$v=19$m=65536,t=3,p=1$");
        first.ShouldNotBe(second);
        (await hasher.VerifyAsync("Supermercado-2026!", first, SecretKind.Password, Ct)).ShouldBe(SecretVerification.Succeeded);
        (await hasher.VerifyAsync("supermercado-2026!", first, SecretKind.Password, Ct)).ShouldBe(SecretVerification.Failed);
    }

    [Fact]
    public async Task El_PIN_usa_parametros_mas_livianos()
    {
        using var hasher = new Argon2idSecretHasher();
        (await hasher.HashAsync("4826", SecretKind.Pin, Ct)).ShouldStartWith("$argon2id$v=19$m=19456,t=2,p=1$");
    }

    [Fact]
    public async Task Un_hash_con_parametros_antiguos_se_verifica_y_pide_recalcularse()
    {
        using var weak = new Argon2idSecretHasher(new Argon2Settings(8192, 1), new Argon2Settings(8192, 1));
        using var current = new Argon2idSecretHasher();
        var old = await weak.HashAsync("Clave-Antigua-9", SecretKind.Password, Ct);

        (await current.VerifyAsync("Clave-Antigua-9", old, SecretKind.Password, Ct)).ShouldBe(SecretVerification.SucceededRehashNeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$c2FsdA$aGFzaA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=4$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g")]
    [InlineData("$argon2id$v=19$m=x,t=3,p=1$c2FsdA$aGFzaA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$@@@$aGFzaA")]
    [InlineData("texto plano")]
    public async Task Un_hash_mal_formado_nunca_verifica(string phc)
    {
        using var hasher = new Argon2idSecretHasher();
        (await hasher.VerifyAsync("cualquiera", phc, SecretKind.Password, Ct)).ShouldBe(SecretVerification.Failed);
        (await hasher.VerifyAsync(string.Empty, "x", SecretKind.Password, Ct)).ShouldBe(SecretVerification.Failed);
    }

    [Fact]
    public async Task Una_contrasena_se_verifica_en_menos_de_500_ms()
    {
        using var hasher = new Argon2idSecretHasher();
        var hash = await hasher.HashAsync("Supermercado-2026!", SecretKind.Password, Ct);

        var watch = Stopwatch.StartNew();
        await hasher.VerifyAsync("Supermercado-2026!", hash, SecretKind.Password, Ct);
        watch.ElapsedMilliseconds.ShouldBeLessThan(500);
    }

    [Fact]
    public async Task Veinte_verificaciones_simultaneas_no_fallan()
    {
        using var hasher = new Argon2idSecretHasher();
        var hash = await hasher.HashAsync("Supermercado-2026!", SecretKind.Password, Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => hasher.VerifyAsync("Supermercado-2026!", hash, SecretKind.Password, Ct)));
        results.ShouldAllBe(r => r == SecretVerification.Succeeded);
    }

    [Fact]
    public void Tokens_opacos_y_codigos_numericos()
    {
        var token = SecureTokens.Create();
        token.Length.ShouldBe(43);
        token.ShouldNotContain("+");
        SecureTokens.Hash(token).Length.ShouldBe(64);
        SecureTokens.Matches(token, SecureTokens.Hash(token)).ShouldBeTrue();
        SecureTokens.Matches(token + "x", SecureTokens.Hash(token)).ShouldBeFalse();
        SecureTokens.NumericCode().ShouldMatch("^[0-9]{6}$");
        SecureTokens.Create().ShouldNotBe(token);
    }
}
