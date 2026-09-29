using Npgsql;
using static Pos.Cloud.Database.Tests.CloudSeed;

namespace Pos.Cloud.Database.Tests;

/// <summary>
/// Restricciones del esquema <c>licensing</c> (tercera línea de defensa, docs/fases/fase-12a-propuesta.md §4 y L-06/L-07/L-09):
/// hash de clave único, una licencia vigente por empresa, activación única por equipo e instalación, NIT único y regeneración
/// con la FK diferida <c>replaced_by</c>. Todo se inserta con el rol de la aplicación.
/// </summary>
public class LicensingSchemaTests(CloudPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task El_NIT_de_la_empresa_es_unico_en_todo_el_servidor()
    {
        var db = await postgres.CreateDatabaseAsync();
        var account = Guid.CreateVersion7();
        var (nit, _) = NewNit();
        await db.ExecuteAsync(Account(account) + Organization(Guid.CreateVersion7(), account, nit), db.AppConnectionString);

        var ex = await db.FailsAsync(Organization(Guid.CreateVersion7(), account, nit), db.AppConnectionString);

        ex.ConstraintName.ShouldBe("ux_organizations__nit");
    }

    [Fact]
    public async Task El_NIT_y_su_digito_de_verificacion_tienen_formato()
    {
        var db = await postgres.CreateDatabaseAsync();
        var account = Guid.CreateVersion7();
        await db.ExecuteAsync(Account(account), db.AppConnectionString);

        var ex = await db.FailsAsync(
            $"""
            INSERT INTO licensing.organizations (id, account_id, legal_name, nit, nit_check_digit, status, created_at, created_by)
            VALUES (gen_random_uuid(), '{account}', 'Mal', '900.123.456', '8', 'ACTIVE', now(), '{SystemUser}')
            """,
            db.AppConnectionString);

        ex.ConstraintName.ShouldBe("ck_organizations__nit");
    }

    [Fact]
    public async Task El_hash_de_la_clave_es_unico_y_con_formato()
    {
        var (sql, ids) = FullChain();
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(sql, db.AppConnectionString);
        var hash = await db.ScalarAsync<string>($"SELECT key_hash FROM licensing.licenses WHERE id = '{ids.License}'");

        // Otra empresa con una licencia que tuviera el mismo hash (la misma clave) se rechaza.
        var account = Guid.CreateVersion7();
        var organization = Guid.CreateVersion7();
        var subscription = Guid.CreateVersion7();
        await db.ExecuteAsync(Account(account) + Organization(organization, account) + Subscription(subscription, organization), db.AppConnectionString);
        (await db.FailsAsync(License(Guid.CreateVersion7(), organization, subscription, hash), db.AppConnectionString))
            .ConstraintName.ShouldBe("ux_licenses__key_hash");

        // La clave nunca se guarda en claro: solo el SHA-256 en hex minúscula.
        (await db.FailsAsync(License(Guid.CreateVersion7(), organization, subscription, "POS-ABCDE-FGHJK-LMNPQ-RSTUV"), db.AppConnectionString))
            .ConstraintName.ShouldBe("ck_licenses__key_hash");
    }

    [Fact]
    public async Task Solo_hay_una_licencia_vigente_y_una_suscripcion_vigente_por_empresa()
    {
        var (sql, ids) = FullChain();
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(sql, db.AppConnectionString);

        (await db.FailsAsync(License(Guid.CreateVersion7(), ids.Organization, ids.Subscription), db.AppConnectionString))
            .ConstraintName.ShouldBe("ux_licenses__organization_active");
        (await db.FailsAsync(Subscription(Guid.CreateVersion7(), ids.Organization), db.AppConnectionString))
            .ConstraintName.ShouldBe("ux_subscriptions__organization_current");

        // Revocadas y canceladas no cuentan: el historial se conserva.
        await db.ExecuteAsync(License(Guid.CreateVersion7(), ids.Organization, ids.Subscription, status: "REVOKED"), db.AppConnectionString);
        await db.ExecuteAsync(Subscription(Guid.CreateVersion7(), ids.Organization, "CANCELLED"), db.AppConnectionString);
        (await db.ScalarAsync<long>($"SELECT count(*) FROM licensing.licenses WHERE organization_id = '{ids.Organization}'")).ShouldBe(2);
    }

    [Fact]
    public async Task La_licencia_debe_ser_de_la_misma_empresa_que_su_suscripcion()
    {
        var (sql, ids) = FullChain();
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(sql, db.AppConnectionString);
        var otherOrganization = Guid.CreateVersion7();
        await db.ExecuteAsync(Organization(otherOrganization, ids.Account), db.AppConnectionString);

        (await db.FailsAsync(License(Guid.CreateVersion7(), otherOrganization, ids.Subscription), db.AppConnectionString))
            .ConstraintName.ShouldBe("fk_licenses__subscription");
    }

    [Fact]
    public async Task Un_equipo_y_una_instalacion_tienen_como_maximo_una_activacion_vigente()
    {
        var (sql, ids) = FullChain();
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(sql, db.AppConnectionString);

        // Otra instalación en el MISMO equipo.
        var otherInstallation = Guid.CreateVersion7();
        await db.ExecuteAsync(Installation(otherInstallation, ids.License, ids.Organization), db.AppConnectionString);
        (await db.FailsAsync(Activation(Guid.CreateVersion7(), otherInstallation, ids.License, ids.Device), db.AppConnectionString))
            .ConstraintName.ShouldBe("ux_activations__device_active");

        // La MISMA instalación en otro equipo.
        var otherDevice = Guid.CreateVersion7();
        await db.ExecuteAsync(Device(otherDevice, ids.Installation), db.AppConnectionString);
        (await db.FailsAsync(Activation(Guid.CreateVersion7(), ids.Installation, ids.License, otherDevice), db.AppConnectionString))
            .ConstraintName.ShouldBe("ux_activations__installation_active");

        // Liberada la activación (con quién, cuándo y por qué), el otro equipo se puede activar.
        await db.ExecuteAsync(
            $"""
            UPDATE licensing.activations SET status = 'RELEASED', released_at = now(), released_by = '{SystemUser}', release_reason = 'Cambio de PC'
            WHERE id = '{ids.Activation}'
            """,
            db.AppConnectionString);
        await db.ExecuteAsync(Activation(Guid.CreateVersion7(), ids.Installation, ids.License, otherDevice), db.AppConnectionString);

        (await db.FailsAsync(
            $"UPDATE licensing.activations SET status = 'RELEASED' WHERE device_id = '{otherDevice}'", db.AppConnectionString))
            .ConstraintName.ShouldBe("ck_activations__released");
    }

    [Fact]
    public async Task La_huella_se_guarda_solo_como_hashes()
    {
        var (sql, ids) = FullChain();
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(sql, db.AppConnectionString);

        (await db.FailsAsync(
            $"""
            INSERT INTO licensing.devices (id, installation_id, fingerprint, role, first_seen_at, last_seen_at)
            VALUES (gen_random_uuid(), '{ids.Installation}', 'fp1.SERIE-PLACA-123.-.-', 'ALL_IN_ONE', now(), now())
            """,
            db.AppConnectionString)).ConstraintName.ShouldBe("ck_devices__fingerprint");
    }

    [Fact]
    public async Task Regenerar_la_clave_revoca_la_anterior_antes_de_insertar_la_nueva_con_la_FK_diferida()
    {
        var (sql, ids) = FullChain();
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(sql, db.AppConnectionString);
        var replacement = Guid.CreateVersion7();

        // Mismo orden que el caso de uso: UPDATE de la anterior (apunta a una licencia que aún no existe) → INSERT de la nueva → COMMIT.
        await using (var connection = new NpgsqlConnection(db.AppConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var revoke = new NpgsqlCommand(
                $"""
                UPDATE licensing.licenses SET status = 'REVOKED', revoked_at = now(), revoked_reason = 'Clave regenerada', replaced_by = '{replacement}'
                WHERE id = '{ids.License}'
                """,
                connection,
                transaction))
            {
                await revoke.ExecuteNonQueryAsync(Ct);
            }

            await using (var insert = new NpgsqlCommand(License(replacement, ids.Organization, ids.Subscription), connection, transaction))
            {
                await insert.ExecuteNonQueryAsync(Ct);
            }

            await transaction.CommitAsync(Ct);
        }

        (await db.ScalarAsync<Guid>($"SELECT replaced_by FROM licensing.licenses WHERE id = '{ids.License}'")).ShouldBe(replacement);

        // Al revés (insertar la nueva mientras la anterior sigue vigente) lo impide el índice parcial.
        var (sql2, ids2) = FullChain();
        await db.ExecuteAsync(sql2, db.AppConnectionString);
        (await db.FailsAsync(License(Guid.CreateVersion7(), ids2.Organization, ids2.Subscription), db.AppConnectionString))
            .ConstraintName.ShouldBe("ux_licenses__organization_active");

        // Si la licencia nueva nunca se inserta, la FK diferida hace fallar el COMMIT.
        var ex = await db.FailsAsync(
            $"""
            UPDATE licensing.licenses SET status = 'REVOKED', revoked_at = now(), revoked_reason = 'Clave regenerada', replaced_by = gen_random_uuid()
            WHERE id = '{ids2.License}'
            """,
            db.AppConnectionString);
        ex.ConstraintName.ShouldBe("fk_licenses__replaced_by");
    }

    [Fact]
    public async Task Solo_puede_haber_una_clave_de_firma_activa()
    {
        var db = await postgres.CreateDatabaseAsync();
        const string Insert = """
            INSERT INTO licensing.signing_keys (kid, algorithm, public_key, status, created_at, activated_at)
            VALUES ('{0}', 'EdDSA', '{1}', 'ACTIVE', now(), now())
            """;
        await db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, "ed25519-aaaaaaaaaaaa", new string('A', 43)), db.AppConnectionString);

        (await db.FailsAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, "ed25519-bbbbbbbbbbbb", new string('B', 43)), db.AppConnectionString))
            .ConstraintName.ShouldBe("ux_signing_keys__single_active");
    }
}
