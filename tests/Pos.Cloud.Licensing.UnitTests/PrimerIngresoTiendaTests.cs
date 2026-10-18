using System.Net;
using Pos.Cloud.Licensing.Domain;

namespace Pos.Cloud.Licensing.UnitTests;

/// <summary>Instalador 1.2.1: "Continuar con Google" sin clave en el primer ingreso a una tienda y el registro propio del cliente.</summary>
public class PrimerIngresoTiendaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sin_clave_la_solicitud_no_lleva_licencia_y_guarda_la_cuenta_de_quien_autoriza()
    {
        var request = InstallerSignInRequest.Create(Guid.CreateVersion7(), null, null, InstallerSignInRules.NewPollToken(), IPAddress.Loopback, Now);
        request.WithoutKey.ShouldBeTrue();
        var account = Guid.CreateVersion7();
        request.Approve(Guid.CreateVersion7(), "dueno@gmail.com", "Dueño", IPAddress.Loopback, Now.AddMinutes(1), account).IsSuccess.ShouldBeTrue();
        request.AccountId.ShouldBe(account);
    }

    [Fact]
    public void Con_clave_no_se_guarda_la_cuenta_y_la_licencia_va_con_la_empresa()
    {
        var request = InstallerSignInRequest.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), InstallerSignInRules.NewPollToken(), null, Now);
        request.WithoutKey.ShouldBeFalse();
        request.Approve(Guid.CreateVersion7(), "dueno@gmail.com", "Dueño", null, Now, Guid.CreateVersion7());
        request.AccountId.ShouldBeNull();
        Should.Throw<ArgumentException>(() => InstallerSignInRequest.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), null, InstallerSignInRules.NewPollToken(), null, Now));
    }

    [Fact]
    public void El_registro_propio_crea_una_cuenta_directa_marcada_como_registro_nuevo()
    {
        var account = Account.SelfRegister(Guid.CreateVersion7(), "Marta Gómez", "marta@gmail.com", Now).Value;
        account.Kind.ShouldBe(AccountKind.Direct);
        account.ContactEmail.ShouldBe("marta@gmail.com");
        account.SelfRegisteredAt.ShouldBe(Now);
        account.FirstStoreLinkedAt.ShouldBeNull();

        account.MarkStoreLinked(Now.AddMinutes(5));
        account.MarkStoreLinked(Now.AddDays(1));
        account.FirstStoreLinkedAt.ShouldBe(Now.AddMinutes(5));
    }
}
