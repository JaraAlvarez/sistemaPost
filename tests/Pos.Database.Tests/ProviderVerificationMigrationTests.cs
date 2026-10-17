using Pos.Server.Migrations;

namespace Pos.Database.Tests;

/// <summary>
/// V2026.10.060 (pantallas faltantes, PA1): una instalación vieja con la facturación electrónica ENCENDIDA sin verificación (llaves y modo
/// distinto de OFF de cuando el adaptador era NONE) queda APAGADA y marcada para el aviso del tablero; una apagada no cambia.
/// </summary>
public class ProviderVerificationMigrationTests(PostgresFixture postgres)
{
    private const string OnCompany = "01920000-0000-7000-8000-000000000061";
    private const string OffCompany = "01920000-0000-7000-8000-000000000062";
    private const string SystemUser = "01920000-0000-7000-8000-0000000000ff";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task La_facturacion_encendida_sin_verificacion_queda_apagada_y_con_aviso()
    {
        var db = await postgres.CreateDatabaseAsync();
        var all = ScriptCatalog.Default;
        var migration = all.Versioned.Single(s => s.Name.StartsWith("V2026.10.060__", StringComparison.Ordinal));
        var before = new ScriptCatalog(all.Versioned.Where(s => string.CompareOrdinal(s.Name, migration.Name) < 0).Concat(all.Repeatable).Concat(all.Always));
        await new DatabaseMigrator(before).MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        string Company(string id, string nit) =>
            $"""
            INSERT INTO org.companies (id, legal_name, trade_name, person_type, identification_type, identification_number,
                check_digit, tax_regime, country_code, municipality_code, address, currency_code, timezone, status, created_at, created_by)
            VALUES ('{id}', 'Supermercado {nit} SAS', 'Súper {nit}', 'LEGAL', 'NIT', '{nit}', '8', '48', 'CO',
                '05001', 'Calle 1 # 2-3', 'COP', 'America/Bogota', 'ACTIVE', now(), '{SystemUser}');
            """;
        await db.ExecuteAsync(
            Company(OnCompany, "900000061") + Company(OffCompany, "900000062") +
            $"""
            INSERT INTO billing.provider_settings (company_id, provider, environment, mode, credentials, credentials_updated_at, created_at, created_by)
            VALUES ('{OnCompany}', 'NONE', 'PRODUCTION', 'EVERY_SALE', '\x0102'::bytea, now(), now(), '{SystemUser}'),
                   ('{OffCompany}', 'FACTUS', 'SANDBOX', 'OFF', '\x0102'::bytea, now(), now(), '{SystemUser}');
            """);

        var report = await new DatabaseMigrator(all).MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        report.AppliedScripts.ShouldContain(migration.Name);
        (await db.ScalarAsync<string>($"SELECT mode FROM billing.provider_settings WHERE company_id = '{OnCompany}'")).ShouldBe("OFF");
        (await db.ScalarAsync<bool>($"SELECT auto_disabled_at IS NOT NULL FROM billing.provider_settings WHERE company_id = '{OnCompany}'")).ShouldBeTrue();
        (await db.ScalarAsync<bool>($"SELECT credentials IS NOT NULL FROM billing.provider_settings WHERE company_id = '{OnCompany}'")).ShouldBeTrue();
        (await db.ScalarAsync<bool>(
                $"SELECT auto_disabled_at IS NULL AND backlog_cutoff IS NULL AND NOT backlog_released FROM billing.provider_settings WHERE company_id = '{OffCompany}'"))
            .ShouldBeTrue();
    }
}
