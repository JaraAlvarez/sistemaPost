using Pos.Cloud.PortalIdentity.Domain;

namespace Pos.Cloud.PortalIdentity.UnitTests;

/// <summary>
/// Autorizar el instalador desde el portal (M15, adenda de ADR-0062): el TOTP se pide si el usuario lo tiene activo o el portal lo
/// exige, SIN IMPORTAR si entró con Google; y el ingreso debe ser reciente.
/// </summary>
public class OwnerLinkAuthorizationPolicyTests
{
    private static readonly DateTimeOffset Start = Users.Start;

    private static PortalSession Session(PortalUser user, SessionAuthMethod method, DateTimeOffset at) =>
        PortalSession.StartActive(Guid.CreateVersion7(), user, "hash-sesion", SessionChannel.Portal, method, SessionPolicy.Default, at, null, null);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void El_TOTP_se_pide_si_el_usuario_lo_tiene_o_el_portal_lo_exige(bool userHasTotp, bool requireTotp, bool expected)
    {
        var user = userHasTotp ? Users.WithTotp() : Users.Support();
        OwnerLinkAuthorizationPolicy.RequiresTotp(user, requireTotp).ShouldBe(expected);
    }

    [Theory]
    [InlineData(SessionAuthMethod.Google)]
    [InlineData(SessionAuthMethod.Password)]
    public void El_metodo_de_ingreso_no_cambia_la_regla_del_TOTP_ni_la_del_ingreso_reciente(SessionAuthMethod method)
    {
        var user = Users.WithTotp();
        var session = Session(user, method, Start);

        OwnerLinkAuthorizationPolicy.RequiresTotp(user, requireTotpByPolicy: false).ShouldBeTrue("Google no reemplaza el TOTP para autorizar el instalador.");
        OwnerLinkAuthorizationPolicy.IsRecent(session, user, Start.AddMinutes(9)).ShouldBeTrue();
        OwnerLinkAuthorizationPolicy.IsRecent(session, user, Start.AddMinutes(11)).ShouldBeFalse();
    }

    [Fact]
    public void Una_sesion_revocada_o_de_otro_usuario_no_cuenta_como_ingreso_reciente()
    {
        var user = Users.Support();
        var session = Session(user, SessionAuthMethod.Google, Start);
        OwnerLinkAuthorizationPolicy.IsRecent(session, Users.Support("otro@ejemplo.co"), Start.AddMinutes(1)).ShouldBeFalse();

        session.Revoke("Salió", Start.AddMinutes(1));
        OwnerLinkAuthorizationPolicy.IsRecent(session, user, Start.AddMinutes(2)).ShouldBeFalse();
    }
}
