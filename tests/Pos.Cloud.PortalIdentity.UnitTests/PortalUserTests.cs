using System.Net;
using Pos.Cloud.PortalIdentity.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.PortalIdentity.UnitTests;

internal static class Users
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static PortalUser Support(string email = "soporte@ejemplo.co") =>
        PortalUser.Create(Guid.CreateVersion7(), email, "Soporte", PortalRole.Support, null, "hash-1", Start).Value;

    public static PortalUser WithTotp()
    {
        var user = Support();
        user.BeginTotpEnrollment("secreto-cifrado");
        user.ConfirmTotp(100);
        return user;
    }
}

public class PortalUserTests
{
    private static readonly DateTimeOffset Start = Users.Start;

    [Fact]
    public void Crea_el_usuario_humano_con_correo_normalizado_y_cambio_de_contrasena_obligatorio()
    {
        var user = PortalUser.Create(Guid.CreateVersion7(), "  Ana@Ejemplo.CO ", " Ana Pérez ", PortalRole.Superadmin, null, "hash-1", Start).Value;

        user.Email.ShouldBe("ana@ejemplo.co");
        user.DisplayName.ShouldBe("Ana Pérez");
        user.Kind.ShouldBe(PortalUserKind.Human);
        user.Role.ShouldBe(PortalRole.Superadmin);
        user.Status.ShouldBe(PortalUserStatus.Active);
        user.PasswordHash.ShouldBe("hash-1");
        user.PasswordChangedAt.ShouldBe(Start);
        user.MustChangePassword.ShouldBeTrue();
        user.TotpEnabled.ShouldBeFalse();
        user.SecurityVersion.ShouldBe(2);
        user.AuditLabel.ShouldBe("Usuario del portal ana@ejemplo.co");
        user.CanSignIn(Start).ShouldBeTrue();
    }

    [Theory]
    [InlineData("sin-arroba", "Nombre")]
    [InlineData("a@b", "Nombre")]
    [InlineData("dos@@arrobas.co", "Nombre")]
    [InlineData("con espacio@x.co", "Nombre")]
    [InlineData(null, "Nombre")]
    [InlineData("ok@ejemplo.co", " ")]
    public void Valida_correo_y_nombre(string? email, string name) =>
        PortalUser.Create(Guid.CreateVersion7(), email!, name, PortalRole.Support, null, "hash", Start).Error.ShouldBe(PortalIdentityErrors.InvalidUser);

    [Fact]
    public void Valida_nombre_largo_y_correo_largo()
    {
        PortalUser.Create(Guid.CreateVersion7(), "ok@ejemplo.co", new string('n', 121), PortalRole.Support, null, "hash", Start)
            .Error.ShouldBe(PortalIdentityErrors.InvalidUser);
        PortalUser.IsValidEmail(new string('a', 115) + "@x.com").ShouldBeFalse();
    }

    [Fact]
    public void Solo_el_distribuidor_lleva_cuenta_y_siempre_la_lleva()
    {
        var account = Guid.CreateVersion7();

        PortalUser.Create(Guid.CreateVersion7(), "d@ejemplo.co", "Dist", PortalRole.Reseller, null, "hash", Start)
            .Error.ShouldBe(PortalIdentityErrors.ResellerAccountRequired);
        PortalUser.Create(Guid.CreateVersion7(), "s@ejemplo.co", "Sop", PortalRole.Support, account, "hash", Start)
            .Error.ShouldBe(PortalIdentityErrors.ResellerAccountRequired);
        PortalUser.Create(Guid.CreateVersion7(), "d@ejemplo.co", "Dist", PortalRole.Reseller, account, "hash", Start)
            .Value.ResellerAccountId.ShouldBe(account);
    }

    [Fact]
    public void Se_bloquea_al_quinto_intento_fallido_y_se_desbloquea_con_el_tiempo()
    {
        var user = Users.Support();
        var policy = LoginPolicy.Default;

        for (var i = 1; i < policy.MaxFailedAttempts; i++)
        {
            user.RegisterFailedAttempt(policy, Start).ShouldBeFalse();
        }

        user.FailedLoginCount.ShouldBe((short)4);
        user.RegisterFailedAttempt(policy, Start).ShouldBeTrue();

        user.FailedLoginCount.ShouldBe((short)0);
        user.LockedUntil.ShouldBe(Start.AddMinutes(15));
        user.IsLockedAt(Start.AddMinutes(14)).ShouldBeTrue();
        user.CanSignIn(Start.AddMinutes(14)).ShouldBeFalse();
        user.IsLockedAt(Start.AddMinutes(15)).ShouldBeFalse();
        user.CanSignIn(Start.AddMinutes(16)).ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => user.RegisterFailedAttempt(null!, Start));
    }

    [Fact]
    public void Desbloquear_o_entrar_limpia_los_intentos()
    {
        var user = Users.Support();
        var policy = new LoginPolicy(2, TimeSpan.FromMinutes(5));
        user.RegisterFailedAttempt(policy, Start);
        user.RegisterFailedAttempt(policy, Start).ShouldBeTrue();

        user.Unlock();

        user.LockedUntil.ShouldBeNull();
        user.CanSignIn(Start).ShouldBeTrue();

        user.RegisterFailedAttempt(policy, Start);
        user.RegisterSuccessfulSignIn(Start.AddMinutes(1));

        user.FailedLoginCount.ShouldBe((short)0);
        user.LastLoginAt.ShouldBe(Start.AddMinutes(1));
    }

    [Fact]
    public void Cambiar_la_contrasena_invalida_sesiones_pero_recalcular_el_hash_no()
    {
        var user = Users.Support();
        var version = user.SecurityVersion;
        user.RegisterFailedAttempt(new LoginPolicy(1, TimeSpan.FromHours(1)), Start);

        user.SetPassword("hash-2", Start.AddDays(1), mustChange: false);

        user.PasswordHash.ShouldBe("hash-2");
        user.MustChangePassword.ShouldBeFalse();
        user.PasswordChangedAt.ShouldBe(Start.AddDays(1));
        user.LockedUntil.ShouldBeNull();
        user.SecurityVersion.ShouldBe(version + 1);

        user.RehashPassword("hash-3");
        user.PasswordHash.ShouldBe("hash-3");
        user.SecurityVersion.ShouldBe(version + 1);
        Should.Throw<ArgumentException>(() => user.SetPassword(" ", Start, mustChange: false));
        Should.Throw<ArgumentException>(() => user.RehashPassword(""));
    }

    [Fact]
    public void Enrolar_TOTP_requiere_secreto_y_se_confirma_con_el_primer_codigo()
    {
        var user = Users.Support();
        var version = user.SecurityVersion;

        user.ConfirmTotp(10).Error.ShouldBe(PortalIdentityErrors.TotpEnrollmentRequired);
        user.BeginTotpEnrollment("secreto-cifrado").IsSuccess.ShouldBeTrue();
        user.TotpSecretProtected.ShouldBe("secreto-cifrado");
        user.TotpEnabled.ShouldBeFalse();

        user.ConfirmTotp(10).IsSuccess.ShouldBeTrue();

        user.TotpEnabled.ShouldBeTrue();
        user.TotpLastStep.ShouldBe(10);
        user.SecurityVersion.ShouldBe(version + 1);
        user.ConfirmTotp(11).Error.ShouldBe(PortalIdentityErrors.TotpEnrollmentRequired);
        user.BeginTotpEnrollment("otro").Error.ShouldBe(PortalIdentityErrors.TotpAlreadyEnabled);
        Should.Throw<ArgumentException>(() => user.BeginTotpEnrollment(" "));

        user.UseTotpStep(12);
        user.TotpLastStep.ShouldBe(12);
    }

    [Fact]
    public void Reiniciar_el_TOTP_obliga_a_enrolar_de_nuevo()
    {
        var user = Users.WithTotp();
        var version = user.SecurityVersion;

        user.ResetTotp();

        user.TotpEnabled.ShouldBeFalse();
        user.TotpSecretProtected.ShouldBeNull();
        user.TotpLastStep.ShouldBeNull();
        user.SecurityVersion.ShouldBe(version + 1);
        user.BeginTotpEnrollment("nuevo").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Actualizar_cambia_rol_y_estado_e_invalida_sesiones_solo_si_cambia_algo_de_seguridad()
    {
        var user = Users.Support();
        var version = user.SecurityVersion;

        user.Update(" Soporte Nivel 2 ", PortalRole.Support, null, isActive: true).IsSuccess.ShouldBeTrue();
        user.DisplayName.ShouldBe("Soporte Nivel 2");
        user.SecurityVersion.ShouldBe(version);

        user.Update("Soporte", PortalRole.Superadmin, null, isActive: true).IsSuccess.ShouldBeTrue();
        user.Role.ShouldBe(PortalRole.Superadmin);
        user.SecurityVersion.ShouldBe(version + 1);

        user.Update("Soporte", PortalRole.Superadmin, null, isActive: false).IsSuccess.ShouldBeTrue();
        user.Status.ShouldBe(PortalUserStatus.Disabled);
        user.CanSignIn(Start).ShouldBeFalse();
        user.SecurityVersion.ShouldBe(version + 2);

        var account = Guid.CreateVersion7();
        user.Update("Distribuidor", PortalRole.Reseller, account, isActive: false).IsSuccess.ShouldBeTrue();
        user.ResellerAccountId.ShouldBe(account);
        user.SecurityVersion.ShouldBe(version + 3);
    }

    [Fact]
    public void Actualizar_valida_nombre_y_cuenta_de_distribuidor()
    {
        var user = Users.Support();

        user.Update(null!, PortalRole.Support, null, isActive: true).Error.ShouldBe(PortalIdentityErrors.InvalidUser);
        user.Update(new string('n', 121), PortalRole.Support, null, isActive: true).Error.ShouldBe(PortalIdentityErrors.InvalidUser);
        user.Update("Dist", PortalRole.Reseller, null, isActive: true).Error.ShouldBe(PortalIdentityErrors.ResellerAccountRequired);
    }

    [Fact]
    public void El_usuario_tecnico_no_se_modifica_ni_puede_entrar()
    {
        var user = Users.Support();
        typeof(PortalUser).GetProperty(nameof(PortalUser.Kind))!.SetValue(user, PortalUserKind.System);

        user.Update("Otro", PortalRole.Support, null, isActive: true).Error.ShouldBe(PortalIdentityErrors.SystemUserImmutable);
        user.CanSignIn(Start).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Contrasena2026", true)]
    [InlineData("corta1", false)]
    [InlineData("SoloLetrasLargas", false)]
    [InlineData("123456789012345", false)]
    [InlineData(null, false)]
    [InlineData("ana2026@ejemplo.co", false)]
    public void Politica_de_contrasena(string? password, bool acceptable) =>
        PortalIdentityErrors.IsAcceptablePassword(password, "ANA2026@ejemplo.co").ShouldBe(acceptable);

    [Fact]
    public void Contrasena_demasiado_larga_se_rechaza() =>
        PortalIdentityErrors.IsAcceptablePassword(new string('a', 128) + "1", "x@y.co").ShouldBeFalse();

    [Fact]
    public void Los_errores_tienen_codigo_estable_y_tipo()
    {
        PortalIdentityErrors.InvalidCredentials.Type.ShouldBe(ErrorType.Unauthorized);
        PortalIdentityErrors.EmailDuplicated.Type.ShouldBe(ErrorType.Conflict);
        PortalIdentityErrors.LastSuperadmin.Code.ShouldBe("PORTAL.LAST_SUPERADMIN");
    }
}

public class PortalSessionTests
{
    private static readonly DateTimeOffset Start = Users.Start;
    private static readonly SessionPolicy Policy = SessionPolicy.Default;

    private static PortalSession Pending(PortalUser user, string? userAgent = "Firefox") =>
        PortalSession.StartPending(Guid.CreateVersion7(), user, "hash-pendiente", SessionChannel.Portal, Policy, Start, IPAddress.Loopback, userAgent);

    [Fact]
    public void Sin_TOTP_la_sesion_queda_pendiente_de_enrolamiento()
    {
        var user = Users.Support();

        var session = Pending(user);

        session.Stage.ShouldBe(SessionStage.PendingEnrollment);
        session.UserId.ShouldBe(user.Id);
        session.TokenHash.ShouldBe("hash-pendiente");
        session.Channel.ShouldBe(SessionChannel.Portal);
        session.CreatedAt.ShouldBe(Start);
        session.LastSeenAt.ShouldBe(Start);
        session.ExpiresAt.ShouldBe(Start.AddMinutes(5));
        session.IdleExpiresAt.ShouldBe(Start.AddMinutes(5));
        session.SecurityVersion.ShouldBe(user.SecurityVersion);
        session.IpAddress.ShouldBe(IPAddress.Loopback);
        session.UserAgent.ShouldBe("Firefox");
        session.SecondFactorAt.ShouldBeNull();
    }

    [Fact]
    public void Con_TOTP_la_sesion_queda_pendiente_del_codigo_y_vence_a_los_5_minutos()
    {
        var user = Users.WithTotp();

        var session = Pending(user, userAgent: new string('u', 400));

        session.Stage.ShouldBe(SessionStage.PendingTotp);
        session.UserAgent!.Length.ShouldBe(300);
        session.IsUsable(user, Start.AddMinutes(4)).ShouldBeTrue();
        session.IsUsable(user, Start.AddMinutes(5)).ShouldBeFalse();
        session.Touch(Policy, Start.AddMinutes(2)).ShouldBeFalse();
        Pending(user, userAgent: null).UserAgent.ShouldBeNull();
    }

    [Fact]
    public void Completar_el_segundo_factor_activa_la_sesion_con_token_nuevo()
    {
        var user = Users.WithTotp();
        var session = Pending(user);

        session.CompleteSecondFactor(user, "hash-activo", Policy, Start.AddMinutes(1));

        session.Stage.ShouldBe(SessionStage.Active);
        session.TokenHash.ShouldBe("hash-activo");
        session.SecondFactorAt.ShouldBe(Start.AddMinutes(1));
        session.ExpiresAt.ShouldBe(Start.AddMinutes(1).AddHours(12));
        session.IdleExpiresAt.ShouldBe(Start.AddMinutes(31));
        session.IsUsable(user, Start.AddMinutes(30)).ShouldBeTrue();
        session.IsUsable(user, Start.AddMinutes(31)).ShouldBeFalse();
    }

    [Fact]
    public void Tocar_extiende_la_inactividad_como_maximo_una_vez_por_minuto_y_sin_pasar_el_vencimiento_absoluto()
    {
        var user = Users.WithTotp();
        var session = Pending(user);
        session.CompleteSecondFactor(user, "hash-activo", Policy, Start);

        session.Touch(Policy, Start.AddSeconds(59)).ShouldBeFalse();
        session.Touch(Policy, Start.AddMinutes(10)).ShouldBeTrue();
        session.LastSeenAt.ShouldBe(Start.AddMinutes(10));
        session.IdleExpiresAt.ShouldBe(Start.AddMinutes(40));

        var nearEnd = Start.AddHours(11).AddMinutes(50);
        session.Touch(Policy, nearEnd).ShouldBeTrue();
        session.IdleExpiresAt.ShouldBe(session.ExpiresAt);
        Should.Throw<ArgumentNullException>(() => session.Touch(null!, Start));
    }

    [Fact]
    public void La_sesion_deja_de_servir_si_cambia_la_seguridad_el_usuario_o_se_deshabilita()
    {
        var user = Users.WithTotp();
        var session = Pending(user);
        session.CompleteSecondFactor(user, "hash-activo", Policy, Start);

        session.IsUsable(Users.WithTotp(), Start).ShouldBeFalse();
        user.SetPassword("hash-nuevo", Start, mustChange: false);
        session.IsUsable(user, Start).ShouldBeFalse();

        var other = Users.WithTotp();
        var otherSession = Pending(other);
        otherSession.CompleteSecondFactor(other, "hash-2", Policy, Start);
        typeof(PortalUser).GetProperty(nameof(PortalUser.Status))!.SetValue(other, PortalUserStatus.Disabled);
        otherSession.IsUsable(other, Start).ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => otherSession.IsUsable(null!, Start));
    }

    [Fact]
    public void Revocar_una_sola_vez_y_recorta_el_motivo()
    {
        var user = Users.WithTotp();
        var session = Pending(user);

        session.Revoke(new string('m', 250), Start.AddMinutes(1)).ShouldBeTrue();

        session.RevokedAt.ShouldBe(Start.AddMinutes(1));
        session.RevokedReason!.Length.ShouldBe(200);
        session.IsUsable(user, Start.AddMinutes(1)).ShouldBeFalse();
        session.Revoke("Cierre de sesión", Start.AddMinutes(2)).ShouldBeFalse();
        session.RevokedReason.Length.ShouldBe(200);
        Pending(user).Revoke("Cierre de sesión", Start).ShouldBeTrue();
    }

    [Fact]
    public void Los_argumentos_nulos_se_rechazan()
    {
        var user = Users.Support();
        var session = Pending(user);

        Should.Throw<ArgumentNullException>(() =>
            PortalSession.StartPending(Guid.Empty, null!, "h", SessionChannel.Api, Policy, Start, null, null));
        Should.Throw<ArgumentNullException>(() =>
            PortalSession.StartPending(Guid.Empty, user, "h", SessionChannel.Api, null!, Start, null, null));
        Should.Throw<ArgumentNullException>(() => session.CompleteSecondFactor(null!, "h", Policy, Start));
        Should.Throw<ArgumentNullException>(() => session.CompleteSecondFactor(user, "h", null!, Start));
    }
}
