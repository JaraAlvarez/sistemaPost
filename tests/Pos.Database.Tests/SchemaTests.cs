using Npgsql;

namespace Pos.Database.Tests;

/// <summary>Restricciones, privilegios y convenciones del esquema de la Fase 2 (tercera línea de defensa).</summary>
public class SchemaTests(PostgresFixture postgres)
{
    private const string CompanyId = "01920000-0000-7000-8000-000000000001";
    private const string BranchA = "01920000-0000-7000-8000-00000000000a";
    private const string BranchB = "01920000-0000-7000-8000-00000000000b";
    private const string SystemUser = "01920000-0000-7000-8000-0000000000ff";

    private static readonly string SeedCompanyAndBranches = $"""
        INSERT INTO org.companies (id, legal_name, trade_name, person_type, identification_type, identification_number,
            check_digit, tax_regime, country_code, municipality_code, address, currency_code, timezone, status, created_at, created_by)
        VALUES ('{CompanyId}', 'Supermercado Prueba SAS', 'Súper Prueba', 'LEGAL', 'NIT', '900123456', '8', '48', 'CO',
            '05001', 'Calle 1 # 2-3', 'COP', 'America/Bogota', 'ACTIVE', now(), '{SystemUser}');
        INSERT INTO org.branches (id, company_id, code, name, municipality_code, address, status, created_at, created_by)
        VALUES ('{BranchA}', '{CompanyId}', 'S01', 'Centro', '05001', 'Calle 1', 'ACTIVE', now(), '{SystemUser}'),
               ('{BranchB}', '{CompanyId}', 'S02', 'Norte', '05001', 'Calle 2', 'ACTIVE', now(), '{SystemUser}');
        """;

    [Fact]
    public async Task Todas_las_claves_foraneas_tienen_indice()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        var unindexed = await db.ListAsync<string>(
            """
            SELECT c.conrelid::regclass || ' ' || c.conname
            FROM pg_constraint c
            WHERE c.contype = 'f'
              AND NOT EXISTS (
                  SELECT 1 FROM pg_index i
                  WHERE i.indrelid = c.conrelid
                    AND (i.indkey::int2[])[0:array_length(c.conkey, 1) - 1] @> c.conkey
                    AND (i.indkey::int2[])[0:array_length(c.conkey, 1) - 1] <@ c.conkey)
            """);

        unindexed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Las_restricciones_tienen_nombre_explicito()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        var generated = await db.ListAsync<string>(
            """
            SELECT conrelid::regclass || ' ' || conname FROM pg_constraint c
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname IN ('system','ref','org','identity','audit')
              AND c.contype IN ('p','f','u','c')
              AND c.conname !~ '^(pk|fk|ux|ck)_'
              AND c.conrelid::regclass::text NOT LIKE 'audit.audit_log_%'
            """);

        generated.ShouldBeEmpty();
    }

    [Fact]
    public async Task Los_datos_de_referencia_de_Colombia_estan_cargados()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        (await db.ScalarAsync<long>("SELECT count(*) FROM ref.departments")).ShouldBe(33);
        (await db.ScalarAsync<long>("SELECT count(*) FROM ref.municipalities")).ShouldBe(1122);
        (await db.ScalarAsync<string>("SELECT name FROM ref.municipalities WHERE code = '11001'")).ShouldBe("BOGOTÁ, D.C.");
        (await db.ScalarAsync<string>("SELECT fiscal_code FROM ref.identification_types WHERE code = 'NIT'")).ShouldBe("31");
        (await db.ScalarAsync<long>("SELECT count(*) FROM system.document_types")).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task pos_app_tiene_limite_de_30_segundos_por_transaccion()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        (await db.ScalarAsync<string>("SHOW transaction_timeout", db.AppConnectionString)).ShouldBe("30s");
    }

    [Fact]
    public async Task pos_app_no_puede_modificar_ni_borrar_la_auditoria()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync(
            """
            INSERT INTO audit.audit_log (id, occurred_at, node_id, hash_version, module, action, severity, row_hash)
            VALUES (gen_random_uuid(), now(), gen_random_uuid(), 1, 'test', 'TEST', 'INFO', repeat('a', 64))
            """,
            db.AppConnectionString);

        var update = await Should.ThrowAsync<PostgresException>(
            () => db.ExecuteAsync("UPDATE audit.audit_log SET summary = 'x'", db.AppConnectionString));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        var delete = await Should.ThrowAsync<PostgresException>(
            () => db.ExecuteAsync("DELETE FROM audit.audit_log", db.AppConnectionString));
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // Ni siquiera el superusuario sin desactivar triggers (defensa adicional a los privilegios).
        var superuser = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync("DELETE FROM audit.audit_log"));
        superuser.MessageText.ShouldContain("solo inserción");
    }

    [Fact]
    public async Task pos_app_no_puede_escribir_catalogos_ni_hacer_DDL()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        (await Should.ThrowAsync<PostgresException>(
                () => db.ExecuteAsync("UPDATE ref.municipalities SET name = 'X'", db.AppConnectionString)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(
                () => db.ExecuteAsync("INSERT INTO identity.permissions VALUES ('a.b.c','a','x',false,false)", db.AppConnectionString)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(
                () => db.ExecuteAsync("CREATE TABLE org.hack (id int)", db.AppConnectionString)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(
                () => db.ExecuteAsync("TRUNCATE org.companies", db.AppConnectionString)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task El_codigo_de_bodega_es_unico_por_sucursal_y_no_por_empresa()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync(SeedCompanyAndBranches, db.AppConnectionString);

        await db.ExecuteAsync(
            $"""
            INSERT INTO org.warehouses (id, company_id, branch_id, code, name, kind, allows_sales, status, created_at, created_by)
            VALUES (gen_random_uuid(), '{CompanyId}', '{BranchA}', 'PISO', 'Piso', 'SALES_FLOOR', true, 'ACTIVE', now(), '{SystemUser}'),
                   (gen_random_uuid(), '{CompanyId}', '{BranchB}', 'PISO', 'Piso', 'SALES_FLOOR', true, 'ACTIVE', now(), '{SystemUser}');
            """,
            db.AppConnectionString);

        var duplicate = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync(
            $"""
            INSERT INTO org.warehouses (id, company_id, branch_id, code, name, kind, allows_sales, status, created_at, created_by)
            VALUES (gen_random_uuid(), '{CompanyId}', '{BranchA}', 'PISO', 'Otra', 'STORAGE', false, 'ACTIVE', now(), '{SystemUser}');
            """,
            db.AppConnectionString));
        duplicate.ConstraintName.ShouldBe("ux_warehouses__branch_code");
    }

    [Fact]
    public async Task Una_bodega_no_puede_apuntar_a_una_sucursal_de_otra_empresa()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync(SeedCompanyAndBranches, db.AppConnectionString);

        var ex = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync(
            $"""
            INSERT INTO org.warehouses (id, company_id, branch_id, code, name, kind, allows_sales, status, created_at, created_by)
            VALUES (gen_random_uuid(), gen_random_uuid(), '{BranchA}', 'PISO', 'Piso', 'SALES_FLOOR', true, 'ACTIVE', now(), '{SystemUser}');
            """,
            db.AppConnectionString));
        ex.ConstraintName.ShouldBe("fk_warehouses__branch");
    }

    [Fact]
    public async Task La_bodega_de_una_caja_debe_admitir_ventas()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync(SeedCompanyAndBranches, db.AppConnectionString);
        await db.ExecuteAsync(
            $"""
            INSERT INTO org.warehouses (id, company_id, branch_id, code, name, kind, allows_sales, status, created_at, created_by)
            VALUES ('01920000-0000-7000-8000-0000000000d1', '{CompanyId}', '{BranchA}', 'AVERIAS', 'Averías', 'DAMAGED', false,
                    'ACTIVE', now(), '{SystemUser}');
            """,
            db.AppConnectionString);

        var ex = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync(
            $"""
            INSERT INTO org.pos_terminals (id, company_id, branch_id, code, name, warehouse_id, status, created_at, created_by)
            VALUES (gen_random_uuid(), '{CompanyId}', '{BranchA}', 'C01', 'Caja 1', '01920000-0000-7000-8000-0000000000d1',
                    'ACTIVE', now(), '{SystemUser}');
            """,
            db.AppConnectionString));
        ex.ConstraintName.ShouldBe("ck_pos_terminals__warehouse_allows_sales");
    }

    [Fact]
    public async Task Una_serie_de_alcance_caja_exige_caja_y_no_retrocede()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync(SeedCompanyAndBranches, db.AppConnectionString);

        var noTerminal = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync(
            $"""
            INSERT INTO system.document_series (id, company_id, branch_id, document_type, prefix, status, created_at, created_by)
            VALUES (gen_random_uuid(), '{CompanyId}', '{BranchA}', 'SALE', 'S01', 'ACTIVE', now(), '{SystemUser}');
            """,
            db.AppConnectionString));
        noTerminal.ConstraintName.ShouldBe("ck_document_series__scope");

        await db.ExecuteAsync(
            $"""
            INSERT INTO system.document_series (id, company_id, branch_id, document_type, prefix, next_number, status, created_at, created_by)
            VALUES ('01920000-0000-7000-8000-0000000000e1', '{CompanyId}', '{BranchA}', 'PURCHASE', 'S01', 10, 'ACTIVE', now(), '{SystemUser}');
            """,
            db.AppConnectionString);
        var backwards = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync(
            "UPDATE system.document_series SET next_number = 9 WHERE id = '01920000-0000-7000-8000-0000000000e1'",
            db.AppConnectionString));
        backwards.ConstraintName.ShouldBe("ck_document_series__monotonic");
    }

    [Fact]
    public async Task La_instalacion_es_una_sola_fila_con_identidad_inmutable()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync(
            "INSERT INTO system.installation (installation_id, node_role, created_at) VALUES (gen_random_uuid(), 'ALL_IN_ONE', now())",
            db.AppConnectionString);

        (await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync(
                "INSERT INTO system.installation (installation_id, node_role, created_at) VALUES (gen_random_uuid(), 'ALL_IN_ONE', now())",
                db.AppConnectionString)))
            .ConstraintName.ShouldBe("pk_installation");
        (await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync(
                "UPDATE system.installation SET installation_id = gen_random_uuid()", db.AppConnectionString)))
            .MessageText.ShouldContain("inmutable");

        // Pasar de Caja Única a Multicaja solo cambia node_role.
        await db.ExecuteAsync("UPDATE system.installation SET node_role = 'STORE_SERVER'", db.AppConnectionString);
    }

    [Fact]
    public async Task La_auditoria_tiene_particion_del_mes_actual_y_pos_app_puede_crear_las_siguientes()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        var partition = "audit_log_" + DateTime.UtcNow.ToString("yyyy_MM", System.Globalization.CultureInfo.InvariantCulture);
        (await db.ScalarAsync<bool>($"SELECT to_regclass('audit.{partition}') IS NOT NULL")).ShouldBeTrue();
        (await db.ScalarAsync<int>("SELECT audit.ensure_partitions(5)", db.AppConnectionString)).ShouldBe(2);
        (await db.ScalarAsync<int>("SELECT audit.ensure_partitions(5)", db.AppConnectionString)).ShouldBe(0);
    }

    [Fact]
    public async Task El_usuario_system_no_puede_tener_credenciales()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync(SeedCompanyAndBranches, db.AppConnectionString);

        var ex = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync(
            $"""
            INSERT INTO identity.users (id, company_id, username, display_name, kind, password_hash, status, created_at, created_by)
            VALUES (gen_random_uuid(), '{CompanyId}', 'system', 'Sistema', 'SYSTEM', 'hash', 'DISABLED', now(), '{SystemUser}');
            """,
            db.AppConnectionString));
        ex.ConstraintName.ShouldBe("ck_users__system_cannot_login");
    }
}
