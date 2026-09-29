using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Persistence;

namespace Pos.Database.Tests;

/// <summary>AuditorÃ­a con sellado por nodo y horizonte seguro (revisiÃ³n arquitectÃ³nica Â§4).</summary>
public class AuditTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Una_bitacora_sellada_sin_manipulacion_es_valida()
    {
        await using var harness = await CreateAsync();
        await WriteEntriesAsync(harness, 3);
        await SealAllAsync(harness);

        var report = await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct);

        report.IsValid.ShouldBeTrue(string.Join("\n", report.Findings.Select(f => f.Message)));
        report.SealsChecked.ShouldBe(1);
        report.RowsChecked.ShouldBe(3);
        report.UnsealedRows.ShouldBe(0);
        report.LastSealShortCode!.ShouldMatch("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$");
    }

    [Fact]
    public async Task El_hash_se_reproduce_con_texto_en_espanol_decimales_e_IP()
    {
        await using var harness = await CreateAsync();
        await using (var scope = harness.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(
                new AuditEntry(
                    "catalog",
                    "PRODUCT_PRICE_CHANGED",
                    "Product",
                    Guid.CreateVersion7(),
                    "Arroz Diana 500 g Â· Ã‘ame \"especial\"",
                    "Juan cambiÃ³ el precio de $4.500 a $4.800,50",
                    new Dictionary<string, object?> { ["price"] = 4500m, ["active"] = true, ["note"] = null },
                    new Dictionary<string, object?> { ["price"] = 4800.50m, ["active"] = true, ["note"] = "lÃ­nea\nnueva\ttab" },
                    Severity: AuditSeverity.Warning),
                Ct);
            await context.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        await SealAllAsync(harness);
        var report = await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct);

        report.IsValid.ShouldBeTrue(string.Join("\n", report.Findings.Select(f => f.Message)));
        (await harness.Database.ScalarAsync<string>("SELECT host(ip_address) FROM audit.audit_log")).ShouldBe("192.168.1.20");
        (await harness.Database.ScalarAsync<string>("SELECT new_values->>'price' FROM audit.audit_log")).ShouldBe("4800.50");
    }

    [Fact]
    public async Task Detecta_una_fila_modificada_directamente_en_la_BD()
    {
        await using var harness = await CreateAsync();
        await WriteEntriesAsync(harness, 3);
        await SealAllAsync(harness);

        await harness.Database.ExecuteAsync(
            "SET session_replication_role = replica; UPDATE audit.audit_log SET summary = 'alterado' WHERE seq = 2;");

        var report = await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct);
        report.Findings.ShouldContain(f => f.Kind == AuditFindingKind.RowAltered && f.Seq == 2);
    }

    [Fact]
    public async Task Detecta_una_fila_modificada_aunque_se_recalcule_su_hash()
    {
        await using var harness = await CreateAsync();
        await WriteEntriesAsync(harness, 3);
        await SealAllAsync(harness);

        // El atacante altera la fila Y recalcula su row_hash: lo delata el sello.
        var row = await ReadRowAsync(harness, 2);
        row.Summary = "alterado";
        var forgedHash = AuditHasher.ComputeRowHash(row);
        await harness.Database.ExecuteAsync(
            $"SET session_replication_role = replica; UPDATE audit.audit_log SET summary = 'alterado', row_hash = '{forgedHash}' WHERE seq = 2;");

        var report = await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct);
        report.Findings.ShouldContain(f => f.Kind == AuditFindingKind.SealContentMismatch);
        report.Findings.ShouldNotContain(f => f.Kind == AuditFindingKind.RowAltered);
    }

    [Fact]
    public async Task Detecta_una_fila_sellada_borrada()
    {
        await using var harness = await CreateAsync();
        await WriteEntriesAsync(harness, 3);
        await SealAllAsync(harness);

        await harness.Database.ExecuteAsync("SET session_replication_role = replica; DELETE FROM audit.audit_log WHERE seq = 1;");

        var report = await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct);
        report.Findings.ShouldContain(f => f.Kind == AuditFindingKind.SealContentMismatch);
    }

    [Fact]
    public async Task Detecta_una_fila_insertada_dentro_de_un_rango_sellado()
    {
        await using var harness = await CreateAsync();
        await WriteEntriesAsync(harness, 3);

        // Hueco legÃ­timo: una transacciÃ³n que reservÃ³ seq y se revirtiÃ³.
        await using (var scope = harness.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry("test", "ROLLED_BACK"), Ct);
            await context.SaveChangesAsync(Ct);
            await transaction.RollbackAsync(Ct);
        }

        await WriteEntriesAsync(harness, 1);
        await SealAllAsync(harness);
        (await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct)).IsValid.ShouldBeTrue();

        // Alguien con pos_app inserta una fila con hash vÃ¡lido en el hueco (seq 4) despuÃ©s del sellado.
        var forged = await ReadRowAsync(harness, 3);
        forged.Id = Guid.CreateVersion7();
        forged.Seq = 4;
        forged.Summary = "fila insertada";
        forged.RowHash = AuditHasher.ComputeRowHash(forged);
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO audit.audit_log (id, occurred_at, node_id, seq, hash_version, module, action, summary, severity, row_hash,
                company_id, branch_id, user_id, user_display_name, ip_address, correlation_id, entity_type, entity_id, entity_label,
                old_values, new_values)
            SELECT '{forged.Id}', occurred_at, node_id, 4, hash_version, module, action, 'fila insertada', severity, '{forged.RowHash}',
                company_id, branch_id, user_id, user_display_name, ip_address, correlation_id, entity_type, entity_id, entity_label,
                old_values, new_values
            FROM audit.audit_log WHERE seq = 3
            """,
            harness.Database.AppConnectionString);

        var report = await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct);
        report.Findings.ShouldContain(f => f.Kind == AuditFindingKind.SealContentMismatch);
    }

    [Fact]
    public async Task Detecta_un_sello_alterado_y_la_cadena_rota()
    {
        await using var harness = await CreateAsync();
        await WriteEntriesAsync(harness, 2);
        await SealAllAsync(harness);
        await WriteEntriesAsync(harness, 2);
        await SealAllAsync(harness);

        await harness.Database.ExecuteAsync(
            "SET session_replication_role = replica; UPDATE audit.audit_seals SET rows_count = 1 WHERE seal_no = 1;");

        var report = await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct);
        report.Findings.ShouldContain(f => f.Kind == AuditFindingKind.SealAltered && f.SealNo == 1);
    }

    [Fact]
    public async Task Las_filas_sin_sellar_se_informan_y_se_verifican()
    {
        await using var harness = await CreateAsync();
        await WriteEntriesAsync(harness, 2);
        await SealAllAsync(harness);
        await WriteEntriesAsync(harness, 3);

        var report = await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct);

        report.IsValid.ShouldBeTrue();
        report.UnsealedRows.ShouldBe(3);
        report.RowsChecked.ShouldBe(5);
    }

    [Fact]
    public async Task El_sellador_solo_sella_despues_del_horizonte_seguro()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = await CreateAsync(services => services.AddSingleton<TimeProvider>(time));
        await WriteEntriesAsync(harness, 2);
        var sealer = harness.Services.GetRequiredService<AuditSealer>();

        await sealer.TickAsync(Ct); // foto Sâ‚€ = 2
        (await harness.Database.ScalarAsync<long>("SELECT count(*) FROM audit.audit_seals")).ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(30));
        await sealer.TickAsync(Ct);
        (await harness.Database.ScalarAsync<long>("SELECT count(*) FROM audit.audit_seals")).ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(16));
        await sealer.TickAsync(Ct);
        (await harness.Database.ScalarAsync<long>("SELECT count(*) FROM audit.audit_seals")).ShouldBe(1);
        (await harness.Database.ScalarAsync<long>("SELECT seq_to FROM audit.audit_seals")).ShouldBe(2);
    }

    private Task<InfrastructureHarness> CreateAsync(Action<IServiceCollection>? configure = null) =>
        InfrastructureHarness.CreateAsync(postgres, services =>
        {
            services.AddScoped<IRequestContext>(_ => new FixedRequestContext());
            configure?.Invoke(services);
        });

    private static async Task WriteEntriesAsync(InfrastructureHarness harness, int count)
    {
        await using var scope = harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        for (var i = 0; i < count; i++)
        {
            await writer.WriteAsync(new AuditEntry("test", "TEST_EVENT", Summary: $"evento {i}"), Ct);
        }

        await context.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
    }

    private static async Task SealAllAsync(InfrastructureHarness harness)
    {
        var sealer = harness.Services.GetRequiredService<AuditSealer>();
        await sealer.SealUpToAsync(await sealer.ReadIssuedSeqAsync(Ct), Ct);
    }

    private static async Task<AuditLogRecord> ReadRowAsync(InfrastructureHarness harness, long seq)
    {
        await using var scope = harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        return await context.Set<AuditLogRecord>().AsNoTracking().SingleAsync(r => r.Seq == seq, Ct);
    }

    private sealed class FixedRequestContext : IRequestContext
    {
        public string? CorrelationId => "prueba-correlacion";

        public IPAddress? IpAddress => IPAddress.Parse("192.168.1.20");

        public Guid? DeviceId => null;
    }
}

/// <summary>Reloj controlado por la prueba.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
