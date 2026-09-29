using Npgsql;
using Pos.Cloud.Abstractions;

namespace Pos.Cloud.IntegrationTests;

/// <summary>§11: cada acción del portal queda auditada (quién, qué, cuándo) en la bitácora encadenada, sellada y verificable.</summary>
public class AuditTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    [Fact]
    public async Task Cada_accion_del_portal_queda_auditada_con_su_autor_y_la_cadena_verifica()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var pos = cloud.NewPos(customer.Nit);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{customer.SubscriptionId}/suspend", new { reason = "Falta de pago" });
        await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{customer.SubscriptionId}/reactivate", new { reason = "Pagó en efectivo" });
        await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{customer.SubscriptionId}/renew", new { paymentReference = "PAGO-123", periods = 1 });
        await PortalApi.PostAsync<object>(cloud.Superadmin, $"/admin/licenses/{customer.License.LicenseId}/regenerate", new { reason = "Clave extraviada" });

        var entries = await PortalApi.GetAsync<List<CloudAuditEntryDto>>(cloud.Superadmin, "/admin/audit?limit=1000");

        string[] expected =
        [
            "PORTAL_LOGIN_SUCCEEDED", "PORTAL_TOTP_ENROLLED", "PORTAL_PASSWORD_CHANGED", "ACCOUNT_CREATED", "ORGANIZATION_CREATED",
            "SUBSCRIPTION_CREATED", "LICENSE_ISSUED", "INSTALLATION_ACTIVATED", "SUBSCRIPTION_SUSPENDED", "SUBSCRIPTION_REACTIVATED",
            "SUBSCRIPTION_RENEWED", "LICENSE_REGENERATED",
        ];
        foreach (var action in expected)
        {
            entries.ShouldContain(e => e.Action == action, $"Falta la acción auditada {action}.");
        }

        // Las acciones del portal llevan el nombre del usuario del portal; las del POS, el usuario técnico.
        entries.Where(e => e.Action is "LICENSE_ISSUED" or "SUBSCRIPTION_SUSPENDED" or "LICENSE_REGENERATED")
            .ShouldAllBe(e => e.UserDisplayName == "Dueño del producto");
        entries.Single(e => e.Action == "INSTALLATION_ACTIVATED" && (e.EntityLabel ?? string.Empty).Contains(pos.Identity.InstallationId.ToString()))
            .UserDisplayName.ShouldBe(SystemActor.DisplayName);

        // La clave nunca aparece en la auditoría (solo su prefijo).
        entries.ShouldNotContain(e => (e.Summary ?? string.Empty).Contains(customer.License.Key) || (e.NewValues ?? string.Empty).Contains(customer.License.Key));

        // El sellador en segundo plano sella la bitácora y la verificación recorre filas, sellos y cadena.
        CloudAuditIntegrityDto integrity = null!;
        for (var i = 0; i < 100; i++)
        {
            integrity = await PortalApi.GetAsync<CloudAuditIntegrityDto>(cloud.Superadmin, "/admin/audit/verify");
            if (integrity.SealsChecked > 0 && integrity.UnsealedRows == 0)
            {
                break;
            }

            await Task.Delay(200, Ct);
        }

        integrity.IsValid.ShouldBeTrue(string.Join("; ", integrity.Findings));
        integrity.SealsChecked.ShouldBeGreaterThan(0);
        integrity.RowsChecked.ShouldBeGreaterThanOrEqualTo(expected.Length);
    }

    [Fact]
    public async Task Alterar_una_fila_auditada_se_detecta_en_la_verificacion()
    {
        await using var factory = new CloudServerFactory();
        await factory.StartAsync();
        var (superadmin, _) = await PortalApi.BootstrapSuperadminAsync(factory);
        await LicensedCustomer.CreateAsync(superadmin);
        (await PortalApi.GetAsync<CloudAuditIntegrityDto>(superadmin, "/admin/audit/verify")).IsValid.ShouldBeTrue();

        // Alguien con acceso de superusuario a la BD quita el disparador y cambia el resumen de una acción.
        await using (var connection = new NpgsqlConnection(factory.Database.SuperuserConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                """
                ALTER TABLE audit.audit_log DISABLE TRIGGER trg_audit_log_append_only;
                UPDATE audit.audit_log SET summary = 'nada que ver aquí' WHERE action = 'LICENSE_ISSUED';
                ALTER TABLE audit.audit_log ENABLE TRIGGER trg_audit_log_append_only;
                """,
                connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var integrity = await PortalApi.GetAsync<CloudAuditIntegrityDto>(superadmin, "/admin/audit/verify");
        integrity.IsValid.ShouldBeFalse();
        integrity.Findings.ShouldNotBeEmpty();
    }
}
