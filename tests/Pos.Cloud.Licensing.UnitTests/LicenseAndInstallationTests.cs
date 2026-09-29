using System.Net;
using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Security;
using static Pos.Cloud.Licensing.UnitTests.TestSupport;

namespace Pos.Cloud.Licensing.UnitTests;

public class LicenseTests
{
    [Fact]
    public void Solo_guarda_el_hash_y_el_prefijo_visible_y_compara_la_clave()
    {
        var subscription = Paid();
        var key = LicenseKey.Generate();

        var license = License.Issue(Guid.CreateVersion7(), subscription, key, null, Start).Value;

        license.KeyHash.ShouldBe(SecureTokens.Hash(key));
        license.KeyHash.ShouldNotContain(key);
        license.KeyPrefix.ShouldBe(key[..9]);
        license.OrganizationId.ShouldBe(subscription.OrganizationId);
        license.SubscriptionId.ShouldBe(subscription.Id);
        license.IssuedAt.ShouldBe(Start);
        license.IsActive.ShouldBeTrue();
        license.AuditLabel.ShouldBe($"Licencia {key[..9]}…");
        license.Matches(key).ShouldBeTrue();
        license.Matches(LicenseKey.Generate()).ShouldBeFalse();
    }

    [Fact]
    public void La_clave_debe_estar_en_forma_canonica()
    {
        var key = LicenseKey.Generate();

        License.Issue(Guid.CreateVersion7(), Paid(), key.ToLowerInvariant(), null, Start).Error.ShouldBe(LicensingErrors.InvalidLicenseKey);
        License.Issue(Guid.CreateVersion7(), Paid(), "POS-12345", null, Start).Error.ShouldBe(LicensingErrors.InvalidLicenseKey);
    }

    [Fact]
    public void No_se_emite_para_una_suscripcion_cancelada_ni_con_maximo_menor_a_uno()
    {
        var cancelled = Paid();
        cancelled.Cancel("Cierre del negocio", Now);

        License.Issue(Guid.CreateVersion7(), cancelled, LicenseKey.Generate(), null, Start).Error.ShouldBe(LicensingErrors.SubscriptionRequired);
        License.Issue(Guid.CreateVersion7(), Paid(), LicenseKey.Generate(), 0, Start).Error.ShouldBe(LicensingErrors.InvalidMaxInstallations);
    }

    [Fact]
    public void El_maximo_de_instalaciones_limita_las_activas()
    {
        var unlimited = Issue(Paid());
        var limited = Issue(Paid(), max: 2);

        unlimited.MaxInstallations.ShouldBeNull();
        unlimited.AdmitsAnotherInstallation(1_000).ShouldBeTrue();
        limited.AdmitsAnotherInstallation(1).ShouldBeTrue();
        limited.AdmitsAnotherInstallation(2).ShouldBeFalse();

        limited.SetMaxInstallations(0).Error.ShouldBe(LicensingErrors.InvalidMaxInstallations);
        limited.SetMaxInstallations(3).IsSuccess.ShouldBeTrue();
        limited.AdmitsAnotherInstallation(2).ShouldBeTrue();
        limited.SetMaxInstallations(null).IsSuccess.ShouldBeTrue();
        limited.MaxInstallations.ShouldBeNull();
    }

    [Fact]
    public void Revocar_exige_motivo_y_solo_una_vez()
    {
        var license = Issue(Paid());
        var replacement = Guid.CreateVersion7();

        license.Revoke("x", Start).Error.ShouldBe(LicensingErrors.ReasonRequired);
        license.Revoke(" Clave regenerada ", Start.AddDays(1), replacement).IsSuccess.ShouldBeTrue();

        license.Status.ShouldBe(LicenseStatus.Revoked);
        license.IsActive.ShouldBeFalse();
        license.RevokedAt.ShouldBe(Start.AddDays(1));
        license.RevokedReason.ShouldBe("Clave regenerada");
        license.ReplacedBy.ShouldBe(replacement);
        license.Revoke("Clave regenerada", Start).Error.ShouldBe(LicensingErrors.LicenseRevoked);
        license.SetMaxInstallations(5).Error.ShouldBe(LicensingErrors.LicenseRevoked);
    }
}

public class InstallationTests
{
    private readonly License _license = Issue(Paid());

    private Installation NewInstallation() => Installation.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), _license, Start);

    [Fact]
    public void Se_registra_liberada_y_sin_equipos()
    {
        var installation = NewInstallation();

        installation.Status.ShouldBe(InstallationStatus.Released);
        installation.LicenseId.ShouldBe(_license.Id);
        installation.OrganizationId.ShouldBe(_license.OrganizationId);
        installation.FirstActivatedAt.ShouldBe(Start);
        installation.AppVersion.ShouldBe("0");
        installation.Devices.ShouldBeEmpty();
        installation.ActiveActivation.ShouldBeNull();
        installation.ActiveDevice.ShouldBeNull();
        installation.AuditLabel.ShouldBe($"Instalación {installation.PosInstallationId}");
    }

    [Fact]
    public void Activar_crea_el_equipo_y_la_activacion()
    {
        var installation = NewInstallation();

        var activation = installation.Activate(_license, Data(PcA, "  1.2.3 "), Now).Value;

        installation.Status.ShouldBe(InstallationStatus.Active);
        installation.AppVersion.ShouldBe("1.2.3");
        installation.BranchName.ShouldBe("Centro");
        installation.AuditLabel.ShouldEndWith(" · Centro");
        activation.Status.ShouldBe(ActivationStatus.Active);
        activation.LicenseId.ShouldBe(_license.Id);
        activation.InstallationId.ShouldBe(installation.Id);
        activation.ActivatedAt.ShouldBe(Start);
        activation.AuditLabel.ShouldBe($"Activación del equipo {activation.DeviceId}");
        var device = installation.ActiveDevice.ShouldNotBeNull();
        device.Id.ShouldBe(activation.DeviceId);
        device.InstallationId.ShouldBe(installation.Id);
        device.Fingerprint.ShouldBe(PcA.ToString());
        device.Role.ShouldBe(DeviceRole.StoreServer);
        device.DeviceName.ShouldBe("SERVIDOR");
        device.OperatingSystem.ShouldBe("Windows 11");
        device.FirstSeenAt.ShouldBe(Start);
        device.LastSeenAt.ShouldBe(Start);
    }

    [Fact]
    public void Reintentar_en_el_mismo_equipo_es_idempotente_con_tolerancia_2_de_3()
    {
        var installation = NewInstallation();
        var first = installation.Activate(_license, Data(PcA), Now).Value;

        var again = installation.Activate(_license, Data(PcANewDisk, branch: null) with { Role = DeviceRole.AllInOne }, At(Start.AddHours(1))).Value;

        again.ShouldBeSameAs(first);
        installation.Activations.Count.ShouldBe(1);
        installation.Devices.Count.ShouldBe(1);
        installation.BranchName.ShouldBe("Centro");
        var device = installation.ActiveDevice!;
        device.Fingerprint.ShouldBe(PcANewDisk.ToString());
        device.Role.ShouldBe(DeviceRole.AllInOne);
        device.LastSeenAt.ShouldBe(Start.AddHours(1));
    }

    [Fact]
    public void Activa_en_otro_equipo_exige_liberar_el_anterior()
    {
        var installation = NewInstallation();
        installation.Activate(_license, Data(PcA), Now);

        installation.Activate(_license, Data(PcB), Now).Error.ShouldBe(LicenseApiErrors.InstallationActiveOnOtherDevice);
    }

    [Fact]
    public void No_se_activa_con_la_licencia_de_otra_empresa()
    {
        var installation = NewInstallation();

        installation.Activate(Issue(Paid()), Data(PcA), Now).Error.ShouldBe(LicenseApiErrors.InstallationOfOtherLicense);
    }

    [Fact]
    public void Liberar_conserva_quien_cuando_y_por_que_y_permite_activar_otro_equipo()
    {
        var installation = NewInstallation();
        var activation = installation.Activate(_license, Data(PcA), Now).Value;

        installation.Release(Guid.CreateVersion7(), "Cambio de PC", Now).Error.ShouldBe(LicensingErrors.ActivationNotFound);
        installation.Release(activation.Id, "x", Now).Error.ShouldBe(LicensingErrors.ReasonRequired);
        installation.Release(activation.Id, " Cambio de PC ", At(Start.AddDays(1))).IsSuccess.ShouldBeTrue();

        installation.Status.ShouldBe(InstallationStatus.Released);
        activation.Status.ShouldBe(ActivationStatus.Released);
        activation.ReleasedAt.ShouldBe(Start.AddDays(1));
        activation.ReleasedBy.ShouldBe(Actor);
        activation.ReleaseReason.ShouldBe("Cambio de PC");
        installation.ActiveActivation.ShouldBeNull();
        installation.Release(activation.Id, "Cambio de PC", Now).Error.ShouldBe(LicensingErrors.ActivationNotFound);

        installation.Activate(_license, Data(PcB), Now).IsSuccess.ShouldBeTrue();
        installation.Devices.Count.ShouldBe(2);
        installation.Activations.Count.ShouldBe(2);
    }

    [Fact]
    public void Reactivar_un_equipo_conocido_reutiliza_su_registro()
    {
        var installation = NewInstallation();
        var first = installation.Activate(_license, Data(PcA), Now).Value;
        installation.Release(first.Id, "Reinstalación", Now);

        var second = installation.Activate(_license, Data(PcANewDisk), At(Start.AddDays(2))).Value;

        second.Id.ShouldNotBe(first.Id);
        second.DeviceId.ShouldBe(first.DeviceId);
        installation.Devices.Count.ShouldBe(1);
        installation.ActiveDevice!.Fingerprint.ShouldBe(PcANewDisk.ToString());
    }

    [Fact]
    public void La_version_vacia_es_cero_y_la_larga_se_recorta()
    {
        var installation = NewInstallation();

        installation.Activate(_license, Data(PcA, "   "), Now);
        installation.AppVersion.ShouldBe("0");
        installation.RecordCheckin(new string('9', 50), 2, null, Start);
        installation.AppVersion.Length.ShouldBe(40);
    }

    [Fact]
    public void El_check_in_se_autentica_con_la_huella_del_equipo_activo()
    {
        var installation = NewInstallation();
        var activation = installation.Activate(_license, Data(PcA), Now).Value;

        var (active, device) = installation.Authenticate(PcANewDisk, Start.AddDays(1)).Value;

        active.ShouldBeSameAs(activation);
        device.Fingerprint.ShouldBe(PcANewDisk.ToString());
        device.LastSeenAt.ShouldBe(Start.AddDays(1));
        device.DeviceName.ShouldBe("SERVIDOR");
        installation.Authenticate(PcB, Start).Error.ShouldBe(LicenseApiErrors.ReactivationRequired);

        installation.Release(activation.Id, "Equipo dañado", Now);
        installation.Authenticate(PcA, Start).Error.ShouldBe(LicenseApiErrors.ReactivationRequired);
    }

    [Fact]
    public void Registrar_el_check_in_guarda_version_cajas_ip_y_fecha()
    {
        var installation = NewInstallation();

        installation.RecordCheckin("2.0.0", -3, IPAddress.Loopback, Start.AddDays(1));

        installation.AppVersion.ShouldBe("2.0.0");
        installation.ActiveTerminals.ShouldBe(0);
        installation.LastIp.ShouldBe(IPAddress.Loopback);
        installation.LastCheckinAt.ShouldBe(Start.AddDays(1));
    }

    [Fact]
    public void Al_regenerar_la_clave_la_instalacion_pasa_a_la_licencia_nueva_de_la_misma_empresa()
    {
        var installation = NewInstallation();
        var activation = installation.Activate(_license, Data(PcA), Now).Value;
        var subscription = Subscription.StartPaid(
            _license.SubscriptionId, _license.OrganizationId, LicenseEdition.MultiTerminal, BillingPeriod.Monthly, "PAGO-1", 1, 7, Now).Value;
        var regenerated = Issue(subscription);

        installation.MoveToLicense(Issue(Paid())).Error.ShouldBe(LicenseApiErrors.InstallationOfOtherLicense);
        installation.MoveToLicense(regenerated).IsSuccess.ShouldBeTrue();

        installation.LicenseId.ShouldBe(regenerated.Id);
        activation.LicenseId.ShouldBe(regenerated.Id);
        installation.Status.ShouldBe(InstallationStatus.Active);

        regenerated.Revoke("Clave regenerada", Start);
        installation.MoveToLicense(regenerated).Error.ShouldBe(LicenseApiErrors.InstallationOfOtherLicense);
    }

    [Fact]
    public void Mover_una_instalacion_liberada_solo_cambia_la_licencia()
    {
        var installation = NewInstallation();
        var subscription = Subscription.StartPaid(
            Guid.CreateVersion7(), _license.OrganizationId, LicenseEdition.MultiTerminal, BillingPeriod.Monthly, "PAGO-1", 1, 7, Now).Value;
        var regenerated = Issue(subscription);

        installation.MoveToLicense(regenerated).IsSuccess.ShouldBeTrue();

        installation.LicenseId.ShouldBe(regenerated.Id);
    }

    [Fact]
    public void Los_argumentos_nulos_se_rechazan()
    {
        var installation = NewInstallation();

        Should.Throw<ArgumentNullException>(() => Installation.Register(Guid.Empty, Guid.Empty, null!, Start));
        Should.Throw<ArgumentNullException>(() => installation.Activate(null!, Data(PcA), Now));
        Should.Throw<ArgumentNullException>(() => installation.Activate(_license, null!, Now));
        Should.Throw<ArgumentNullException>(() => installation.Activate(_license, Data(PcA), null!));
        Should.Throw<ArgumentNullException>(() => installation.Authenticate(null!, Start));
        Should.Throw<ArgumentNullException>(() => installation.Release(Guid.Empty, "motivo", null!));
        Should.Throw<ArgumentNullException>(() => installation.MoveToLicense(null!));
    }
}

public class CheckinTests
{
    private static readonly DateTimeOffset LocalClock = new(2026, 10, 1, 7, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void Un_check_in_emitido_guarda_el_resultado_y_la_vigencia()
    {
        var license = Issue(Paid());
        var installation = Installation.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), license, Start);
        var activation = installation.Activate(license, Data(PcA), Now).Value;
        var report = new CheckinReport(" 1.0.0 ", -1, LocalClock, IPAddress.Loopback);

        var checkin = Checkin.Issued(Guid.CreateVersion7(), installation, activation.Id, report, SubscriptionStatus.Active, Start.AddDays(30), Start);

        checkin.InstallationId.ShouldBe(installation.Id);
        checkin.ActivationId.ShouldBe(activation.Id);
        checkin.OccurredAt.ShouldBe(Start);
        checkin.AppVersion.ShouldBe("1.0.0");
        checkin.ActiveTerminals.ShouldBe(0);
        checkin.ReportedClock.Offset.ShouldBe(TimeSpan.Zero);
        checkin.ReportedClock.ShouldBe(LocalClock);
        checkin.IpAddress.ShouldBe(IPAddress.Loopback);
        checkin.Result.ShouldBe(CheckinResult.TokenIssued);
        checkin.SubscriptionStatus.ShouldBe(SubscriptionStatus.Active);
        checkin.TokenValidUntil.ShouldBe(Start.AddDays(30));
        checkin.RejectionCode.ShouldBeNull();
    }

    [Fact]
    public void Un_check_in_rechazado_guarda_el_codigo()
    {
        var license = Issue(Paid());
        var installation = Installation.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), license, Start);
        var report = new CheckinReport(new string('1', 60), 4, LocalClock, null);

        var checkin = Checkin.Rejected(Guid.CreateVersion7(), installation, report, LicenseErrorCodes.ReactivationRequired, Start);

        checkin.Result.ShouldBe(CheckinResult.Rejected);
        checkin.RejectionCode.ShouldBe(LicenseErrorCodes.ReactivationRequired);
        checkin.ActivationId.ShouldBeNull();
        checkin.AppVersion.Length.ShouldBe(40);
        checkin.ActiveTerminals.ShouldBe(4);
        checkin.SubscriptionStatus.ShouldBeNull();
        Checkin.Rejected(Guid.CreateVersion7(), installation, report with { AppVersion = null! }, "X", Start).AppVersion.ShouldBe("0");
        Should.Throw<ArgumentException>(() => Checkin.Rejected(Guid.CreateVersion7(), installation, report, " ", Start));
        Should.Throw<ArgumentNullException>(() => Checkin.Rejected(Guid.CreateVersion7(), null!, report, "X", Start));
        Should.Throw<ArgumentNullException>(() => Checkin.Rejected(Guid.CreateVersion7(), installation, null!, "X", Start));
        Should.Throw<ArgumentNullException>(() => Checkin.Issued(Guid.CreateVersion7(), null!, Guid.Empty, report, SubscriptionStatus.Active, Start, Start));
        Should.Throw<ArgumentNullException>(() => Checkin.Issued(Guid.CreateVersion7(), installation, Guid.Empty, null!, SubscriptionStatus.Active, Start, Start));
    }
}
