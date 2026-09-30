using Npgsql;

namespace Pos.Database.Tests;

/// <summary>Restricciones de la migración V2026.10.032 (facturación electrónica, Fase 11-B) sobre PostgreSQL real.</summary>
public class BillingElectronicSchemaTests(PostgresFixture postgres)
{
    private static readonly string Company = InfrastructureHarness.CompanyId.ToString();
    private static readonly string User = InfrastructureHarness.SystemUserId.ToString();
    private static readonly string Branch = InfrastructureHarness.BranchS01.ToString();
    private static readonly string BranchS02 = InfrastructureHarness.BranchS02.ToString();
    private static readonly string TerminalC01 = InfrastructureHarness.TerminalC01.ToString();

    private static string Document(
        string source = "SALE", string type = "INVOICE_ELECTRONIC", string status = "PENDING", string? reference = "auto", string extra = "", string extraValues = "")
    {
        var sourceId = Guid.CreateVersion7();
        var code = reference == "auto" ? $"'{sourceId:N}'" : reference is null ? "NULL" : $"'{reference}'";
        return $"""
            INSERT INTO billing.fiscal_documents (id, company_id, branch_id, source, source_id, source_number, document_type, status, reference_code,
                business_date, buyer_name, buyer_identification_type, buyer_identification, subtotal, tax_total, total, issued_at, created_at, created_by{extra})
            VALUES (gen_random_uuid(), '{Company}', '{Branch}', '{source}', '{sourceId}', 'V-1', '{type}', '{status}', {code}, current_date, 'Consumidor final',
                'CC', '222222222222', 1000, 190, 1190, now(), now(), '{User}'{extraValues})
            """;
    }

    private static string Range(string providerId, string? branch = null, string? terminal = null, string type = "INVOICE_ELECTRONIC", bool active = true,
        long from = 1, long to = 100, long current = 0) =>
        $"""
        INSERT INTO billing.fiscal_numbering_ranges (id, company_id, provider, provider_range_id, document_type, prefix, range_from, range_to, current_number,
            is_active, branch_id, pos_terminal_id, synced_at, created_at, created_by)
        VALUES (gen_random_uuid(), '{Company}', 'FACTUS', '{providerId}', '{type}', 'SETP', {from}, {to}, {current}, {(active ? "true" : "false")},
            {(branch is null ? "NULL" : $"'{branch}'")}, {(terminal is null ? "NULL" : $"'{terminal}'")}, now(), now(), '{User}')
        """;

    [Fact]
    public async Task Los_documentos_electronicos_llevan_referencia_unica_y_estados_coherentes()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Document(reference: null), app)))
            .ConstraintName.ShouldBe("ck_fiscal_documents__reference");
        await harness.Database.ExecuteAsync(Document(type: "INTERNAL_RECEIPT", status: "NOT_REQUIRED", reference: null), app);
        await harness.Database.ExecuteAsync(Document(reference: "REF-1"), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Document(reference: "REF-1"), app)))
            .ConstraintName.ShouldBe("ux_fiscal_documents__reference_code");

        // Documento soporte: exclusivo de las compras.
        await harness.Database.ExecuteAsync(Document(source: "PURCHASE", type: "SUPPORT_DOCUMENT"), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Document(source: "PURCHASE"), app)))
            .ConstraintName.ShouldBe("ck_fiscal_documents__support");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Document(type: "SUPPORT_DOCUMENT"), app)))
            .ConstraintName.ShouldBe("ck_fiscal_documents__support");

        // Aceptado: número, CUFE y fecha de validación; rechazado: con mensaje; cancelado: solo un electrónico.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Document(status: "ACCEPTED"), app)))
            .ConstraintName.ShouldBe("ck_fiscal_documents__accepted");
        await harness.Database.ExecuteAsync(
            Document(status: "ACCEPTED", extra: ", fiscal_number, cufe, validated_at", extraValues: ", 'SETP1', 'cufe', now()"), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Document(status: "REJECTED"), app)))
            .ConstraintName.ShouldBe("ck_fiscal_documents__rejected");
        await harness.Database.ExecuteAsync(Document(status: "REJECTED", extra: ", rejection_message", extraValues: ", 'FAK24'"), app);
        await harness.Database.ExecuteAsync(Document(status: "CANCELLED"), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Document(type: "INTERNAL_RECEIPT", status: "CANCELLED", reference: null), app)))
            .ConstraintName.ShouldBe("ck_fiscal_documents__cancelled");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Document(status: "LOST"), app)))
            .ConstraintName.ShouldBe("ck_fiscal_documents__status");

        // Los datos corregidos del adquirente son jsonb; el rango debe existir.
        await harness.Database.ExecuteAsync(Document(extra: ", buyer_fiscal", extraValues: """, '{"personType":"LEGAL","taxRegime":"48"}'"""), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Document(extra: ", numbering_range_id", extraValues: ", gen_random_uuid()"), app)))
            .ConstraintName.ShouldBe("fk_fiscal_documents__numbering_range");

        // Nada se borra (los documentos fiscales son soporte ante la DIAN).
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("DELETE FROM billing.fiscal_documents", app)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("TRUNCATE billing.fiscal_documents CASCADE", app)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Rangos_validos_con_una_asignacion_vigente_por_sucursal_caja_y_tipo()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Range("X1", from: 10, to: 5), app)))
            .ConstraintName.ShouldBe("ck_fiscal_numbering_ranges__numbers");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Range("X2", current: 101), app)))
            .ConstraintName.ShouldBe("ck_fiscal_numbering_ranges__numbers");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Range("X3", terminal: TerminalC01), app)))
            .ConstraintName.ShouldBe("ck_fiscal_numbering_ranges__terminal");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Range("X4", type: "INTERNAL_RECEIPT"), app)))
            .ConstraintName.ShouldBe("ck_fiscal_numbering_ranges__type");
        // La caja debe ser de la sucursal asignada.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Range("X5", BranchS02, TerminalC01), app)))
            .ConstraintName.ShouldBe("fk_fiscal_numbering_ranges__terminal");

        await harness.Database.ExecuteAsync(Range("R1", Branch), app);
        await harness.Database.ExecuteAsync(Range("R2", Branch, TerminalC01), app);
        await harness.Database.ExecuteAsync(Range("R3", Branch, type: "CREDIT_NOTE"), app);
        await harness.Database.ExecuteAsync(Range("R4", Branch, active: false), app);
        await harness.Database.ExecuteAsync(Range("R5"), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Range("R6", Branch), app)))
            .ConstraintName.ShouldBe("ux_fiscal_numbering_ranges__assignment");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Range("R7", Branch, TerminalC01), app)))
            .ConstraintName.ShouldBe("ux_fiscal_numbering_ranges__assignment");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Range("R1"), app)))
            .ConstraintName.ShouldBe("ux_fiscal_numbering_ranges__provider_range");

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("DELETE FROM billing.fiscal_numbering_ranges", app)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task La_configuracion_del_proveedor_exige_credenciales_cifradas_para_encender()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        string Settings(string mode, string environment = "SANDBOX", bool credentials = false, bool updatedAt = false) =>
            $"""
            INSERT INTO billing.provider_settings (company_id, provider, environment, mode, credentials, credentials_updated_at, created_at, created_by)
            VALUES ('{Company}', 'FACTUS', '{environment}', '{mode}', {(credentials ? "'\\x0102'::bytea" : "NULL")}, {(updatedAt ? "now()" : "NULL")}, now(), '{User}')
            """;

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Settings("EVERY_SALE"), app)))
            .ConstraintName.ShouldBe("ck_provider_settings__credentials");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Settings("ALWAYS", credentials: true, updatedAt: true), app)))
            .ConstraintName.ShouldBe("ck_provider_settings__mode");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Settings("OFF", "TEST"), app)))
            .ConstraintName.ShouldBe("ck_provider_settings__environment");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Settings("OFF", credentials: true), app)))
            .ConstraintName.ShouldBe("ck_provider_settings__credentials_updated");

        await harness.Database.ExecuteAsync(Settings("EVERY_SALE", credentials: true, updatedAt: true), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Settings("OFF"), app)))
            .ConstraintName.ShouldBe("pk_provider_settings");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("DELETE FROM billing.provider_settings", app)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task La_cola_tiene_su_indice_y_el_permiso_de_configuracion_es_sensible()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        (await db.ListAsync<string>(
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'billing' AND indexname IN ('ix_fiscal_documents__queue', 'ix_fiscal_documents__pending', 'ix_fiscal_documents__attention') ORDER BY 1"))
            .ShouldBe(["ix_fiscal_documents__attention", "ix_fiscal_documents__queue"]);
        (await db.ScalarAsync<bool>("SELECT is_sensitive FROM identity.permissions WHERE code = 'billing.settings.manage' AND NOT is_deprecated")).ShouldBeTrue();
        (await db.ScalarAsync<string>("SELECT default_severity FROM audit.action_types WHERE code = 'FISCAL_DOCUMENT_REJECTED'")).ShouldBe("CRITICAL");
    }
}
