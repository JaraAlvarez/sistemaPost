using Pos.Modules.Identity.Domain;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Identity.UnitTests;

public class EffectivePermissionsTests
{
    private static readonly Guid Norte = Guid.CreateVersion7();
    private static readonly Guid Sur = Guid.CreateVersion7();
    private static readonly HashSet<string> Catalog = ["a.b.view", "a.b.manage", "c.d.void", "x.y.old"];

    [Fact]
    public void Los_roles_globales_aplican_en_todas_las_sucursales_y_los_de_sucursal_solo_en_la_suya()
    {
        AssignedRole[] roles = [new(null, ["a.b.view"]), new(Norte, ["c.d.void"])];

        EffectivePermissions.Compute(roles, [], Norte, Catalog).ShouldBe(["a.b.view", "c.d.void"], ignoreOrder: true);
        EffectivePermissions.Compute(roles, [], Sur, Catalog).ShouldBe(["a.b.view"]);
        EffectivePermissions.Compute(roles, [], null, Catalog).ShouldBe(["a.b.view"]);
    }

    [Fact]
    public void GRANT_suma_y_DENY_siempre_gana()
    {
        AssignedRole[] roles = [new(null, ["a.b.view", "a.b.manage"])];
        PermissionOverrideRule[] rules =
        [
            new("c.d.void", OverrideEffect.Grant, null),
            new("a.b.manage", OverrideEffect.Deny, null),
            new("c.d.void", OverrideEffect.Deny, Sur),
        ];

        EffectivePermissions.Compute(roles, rules, Norte, Catalog).ShouldBe(["a.b.view", "c.d.void"], ignoreOrder: true);
        EffectivePermissions.Compute(roles, rules, Sur, Catalog).ShouldBe(["a.b.view"]);
    }

    [Fact]
    public void Los_permisos_fuera_del_catalogo_vigente_no_cuentan() =>
        EffectivePermissions.Compute([new(null, ["a.b.view", "retirado.x.y"])], [], null, Catalog).ShouldBe(["a.b.view"]);
}

public class CredentialRulesTests
{
    [Theory]
    [InlineData("corta", "IDENTITY.PASSWORD_TOO_SHORT")]
    [InlineData("password123", "IDENTITY.PASSWORD_TOO_COMMON")]
    [InlineData("aaaaaaaaaa", "IDENTITY.PASSWORD_TOO_COMMON")]
    [InlineData("juanperez2026", "IDENTITY.PASSWORD_CONTAINS_USERNAME")]
    [InlineData("Supermercado-2026!", null)]
    public void Politica_de_contrasenas(string password, string? expected)
    {
        var error = IdentityRules.CheckPassword(password, "juanperez", 8);
        (error == Error.None ? null : error.Code).ShouldBe(expected);
    }

    [Fact]
    public void Una_contrasena_demasiado_larga_se_rechaza() =>
        IdentityRules.CheckPassword(new string('x', 125) + "Ab1-xyzq", "juan", 8).Code.ShouldBe("IDENTITY.PASSWORD_TOO_LONG");

    [Theory]
    [InlineData("4826", null)]
    [InlineData("1234", "IDENTITY.PIN_TOO_SIMPLE")]
    [InlineData("9876", "IDENTITY.PIN_TOO_SIMPLE")]
    [InlineData("0000", "IDENTITY.PIN_TOO_SIMPLE")]
    [InlineData("48A6", "IDENTITY.INVALID_PIN")]
    [InlineData("48261", "IDENTITY.INVALID_PIN")]
    public void Politica_del_PIN(string pin, string? expected)
    {
        var error = IdentityRules.CheckPin(pin, 4);
        (error == Error.None ? null : error.Code).ShouldBe(expected);
    }

    [Theory]
    [InlineData("  Juan.Perez ", true)]
    [InlineData("ab", false)]
    [InlineData("juan perez", false)]
    public void Nombres_de_usuario(string username, bool valid) =>
        IdentityRules.IsValidUsername(IdentityRules.NormalizeUsername(username)).ShouldBe(valid);

    [Theory]
    [InlineData("100", true)]
    [InlineData("123456", true)]
    [InlineData("12", false)]
    [InlineData("12a", false)]
    public void Codigos_de_cajero(string code, bool valid) => IdentityRules.IsValidPosCode(code).ShouldBe(valid);
}

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 0, 0, TimeSpan.Zero);

    private static User NewUser() =>
        User.CreateHuman(Guid.CreateVersion7(), Guid.CreateVersion7(), " Juan.Perez ", " Juan Pérez ", null, "hash", Now, mustChangePassword: true).Value;

    [Fact]
    public void Un_usuario_humano_nace_activo_normalizado_y_debe_cambiar_la_contrasena()
    {
        var user = NewUser();

        user.Username.ShouldBe("juan.perez");
        user.DisplayName.ShouldBe("Juan Pérez");
        user.Status.ShouldBe(UserStatus.Active);
        user.MustChangePassword.ShouldBeTrue();
        user.IsHuman.ShouldBeTrue();
        user.AuditLabel.ShouldBe("Usuario juan.perez · Juan Pérez");
    }

    [Theory]
    [InlineData("system")]
    [InlineData("x")]
    public void Nombres_reservados_o_invalidos(string username) =>
        User.CreateHuman(Guid.CreateVersion7(), Guid.CreateVersion7(), username, "x", null, "hash", Now, true).Error.ShouldBe(IdentityErrors.InvalidUsername);

    [Fact]
    public void Se_bloquea_al_llegar_al_maximo_de_intentos_y_el_bloqueo_vence_solo()
    {
        var user = NewUser();
        for (var i = 0; i < 4; i++)
        {
            user.RecordFailure(Now, maxAttempts: 5, lockMinutes: 15, withPin: false).ShouldBeFalse();
        }

        user.RecordFailure(Now, 5, 15, withPin: false).ShouldBeTrue();
        user.Status.ShouldBe(UserStatus.Locked);
        user.CanAuthenticate(Now.AddMinutes(10), withPin: false).Error.ShouldBe(IdentityErrors.UserLocked);

        user.CanAuthenticate(Now.AddMinutes(16), withPin: false).IsSuccess.ShouldBeTrue();
        user.Status.ShouldBe(UserStatus.Active);
        user.FailedLoginCount.ShouldBe((short)0);
    }

    [Fact]
    public void El_bloqueo_por_PIN_no_impide_entrar_con_contrasena()
    {
        var user = NewUser();
        for (var i = 0; i < 3; i++)
        {
            user.RecordFailure(Now, maxAttempts: 3, lockMinutes: 15, withPin: true);
        }

        user.LockedReason.ShouldBe(LockReason.PinAttempts);
        user.CanAuthenticate(Now, withPin: true).IsFailure.ShouldBeTrue();
        user.CanAuthenticate(Now, withPin: false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Desactivar_impide_entrar_y_los_cambios_suben_la_version_de_seguridad()
    {
        var user = NewUser();
        var version = user.SecurityVersion;

        user.Deactivate();
        user.CanAuthenticate(Now, false).Error.ShouldBe(IdentityErrors.InvalidCredentials);
        user.Activate();
        user.ReplaceRoles([(Guid.CreateVersion7(), null)], Guid.CreateVersion7, user.Id, Now);
        user.ReplaceOverrides([("a.b.c", OverrideEffect.Deny, null, " motivo ")], Guid.CreateVersion7);
        user.SetPin("100", "pinhash", Now).IsSuccess.ShouldBeTrue();
        user.SetPassword("otro", Now, mustChangePassword: false);

        user.SecurityVersion.ShouldBe(version + 6);
        user.Overrides.Single().Reason.ShouldBe("motivo");
        user.MustChangePassword.ShouldBeFalse();
    }

    [Fact]
    public void Roles_repetidos_no_se_duplican_y_los_quitados_desaparecen()
    {
        var user = NewUser();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        user.ReplaceRoles([(a, null), (a, null), (b, null)], Guid.CreateVersion7, user.Id, Now);
        user.Roles.Count.ShouldBe(2);
        user.ReplaceRoles([(b, null)], Guid.CreateVersion7, user.Id, Now);
        user.Roles.Single().RoleId.ShouldBe(b);
    }

    [Fact]
    public void Codigo_de_cajero_invalido_y_hash_vacio()
    {
        var user = NewUser();
        user.SetPin("1", "hash", Now).Error.ShouldBe(IdentityErrors.InvalidPosCode);
        Should.Throw<DomainException>(() => user.SetPassword(" ", Now, false));
        user.RehashPassword("nuevo");
        user.RehashPin("nuevo-pin");
        user.PasswordHash.ShouldBe("nuevo");
        user.Unlock();
        user.Update("Juan P.", "juan@correo.co", null);
        user.Email.ShouldBe("juan@correo.co");
        user.RecordSuccess(Now);
        user.LastLoginAt.ShouldBe(Now);
    }
}

public class RoleTests
{
    [Fact]
    public void Los_roles_de_sistema_no_se_editan_se_clonan()
    {
        var system = Role.CreateSystem(Guid.CreateVersion7(), Guid.CreateVersion7(), new SystemRoleDefinition("CASHIER", "Cajero", "Vende", ["a.b.c"]));

        system.Update("Otro", null, []).Error.ShouldBe(IdentityErrors.SystemRoleImmutable);

        var clone = system.Clone(Guid.CreateVersion7(), "CAJERO_NOCHE", "Cajero de noche").Value;
        clone.IsSystem.ShouldBeFalse();
        clone.PermissionCodes.ShouldBe(["a.b.c"]);
        clone.Update(" Cajero nocturno ", " turno ", ["a.b.c", "d.e.f"]).IsSuccess.ShouldBeTrue();
        clone.Name.ShouldBe("Cajero nocturno");
        clone.PermissionCodes.Count.ShouldBe(2);
    }

    [Fact]
    public void Codigo_invalido_y_permisos_nuevos_de_una_version()
    {
        Role.CreateCustom(Guid.CreateVersion7(), Guid.CreateVersion7(), "cajero", "x", null, []).Error.ShouldBe(IdentityErrors.InvalidRoleCode);

        var owner = Role.CreateSystem(Guid.CreateVersion7(), Guid.CreateVersion7(), new SystemRoleDefinition("OWNER", "Propietario", "Todo", ["a.b.c"]));
        owner.AddMissingPermissions(["a.b.c", "x.y.z", "x.y.z"]).ShouldBe(["x.y.z"]);
        owner.AddMissingPermissions(["a.b.c", "x.y.z"]).ShouldBeEmpty();
    }

    [Fact]
    public void Empleado()
    {
        var employee = Employee.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), null, "CC", " 1020304050 ", " Ana ", " Gómez ", null, null);
        employee.AuditLabel.ShouldBe("Empleado Ana Gómez (CC 1020304050)");
        employee.Deactivate();
        employee.Status.ShouldBe(EmployeeStatus.Inactive);
        employee.Activate();
        employee.Status.ShouldBe(EmployeeStatus.Active);
    }
}
