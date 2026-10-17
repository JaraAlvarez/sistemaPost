using System.Net;
using Pos.Cloud.Licensing.Domain;

namespace Pos.Cloud.Licensing.UnitTests;

/// <summary>Código que el dueño escribe en el portal para autorizar la instalación ("Continuar con Google").</summary>
public class InstallerSignInRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero);

    private static InstallerSignInRequest NewRequest() =>
        InstallerSignInRequest.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), InstallerSignInRules.NewPollToken(), IPAddress.Loopback, Now);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("no tengo el codigo")]
    public void PN2_un_texto_sin_la_forma_del_codigo_no_gasta_intentos(string? typed)
    {
        var request = NewRequest();

        request.CheckUserCode(typed, Guid.CreateVersion7(), null, Now).ShouldBe(InstallerSignInCodeCheck.Malformed);

        request.CodeAttempts.ShouldBe(0);
        request.CodeAttemptsLeft.ShouldBe(InstallerSignInRules.MaxCodeAttempts);
        request.IsPendingAt(Now).ShouldBeTrue();
    }

    [Fact]
    public void Tras_codigos_incorrectos_el_correcto_todavia_coincide_y_al_quinto_se_cancela()
    {
        var request = NewRequest();
        var user = Guid.CreateVersion7();

        request.CheckUserCode("ZZZZ-ZZZZ", user, null, Now).ShouldBe(InstallerSignInCodeCheck.Mismatch);
        request.CheckUserCode("ZZZZZZZZ", user, null, Now).ShouldBe(InstallerSignInCodeCheck.Mismatch);
        request.CodeAttemptsLeft.ShouldBe(3);
        request.CheckUserCode(request.UserCode.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), user, null, Now)
            .ShouldBe(InstallerSignInCodeCheck.Matches);

        var other = NewRequest();
        for (var i = 1; i < InstallerSignInRules.MaxCodeAttempts; i++)
        {
            other.CheckUserCode("ZZZZ-ZZZZ", user, null, Now).ShouldBe(InstallerSignInCodeCheck.Mismatch);
        }

        other.CheckUserCode("ZZZZ-ZZZZ", user, null, Now).ShouldBe(InstallerSignInCodeCheck.Exhausted);
        other.Status.ShouldBe(InstallerSignInStatus.Denied);
        other.CheckUserCode(other.UserCode, user, null, Now).ShouldBe(InstallerSignInCodeCheck.Closed);
    }
}
