using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Persistence;

namespace Pos.Database.Tests;

/// <summary>
/// Fase 10 · entregable del plan: una manipulación directa en la BD es detectada por el verificador; reescribir TODA la cadena solo se
/// descubre con el código del sello impreso fuera de la BD (reporte Z o constancia). Complementa AuditTests (Fase 2: fila modificada,
/// hash recalculado, fila borrada, fila insertada y sello alterado).
/// </summary>
public class AuditIntegrityTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Borrar_un_sello_intermedio_rompe_la_cadena()
    {
        await using var harness = await CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            await WriteEntriesAsync(harness, 2);
            await SealAllAsync(harness);
        }

        await harness.Database.ExecuteAsync("SET session_replication_role = replica; DELETE FROM audit.audit_seals WHERE seal_no = 2;");

        var report = await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(Ct);
        report.Findings.ShouldContain(f => f.Kind == AuditFindingKind.ChainBroken && f.SealNo == 3);
    }

    [Fact]
    public async Task Reescribir_toda_la_cadena_solo_se_descubre_con_el_codigo_impreso()
    {
        await using var harness = await CreateAsync();
        await WriteEntriesAsync(harness, 2);
        await SealAllAsync(harness);
        await WriteEntriesAsync(harness, 2);
        await SealAllAsync(harness);
        var verifier = harness.Services.GetRequiredService<AuditVerifier>();
        var original = await verifier.VerifyAsync(Ct);
        original.IsValid.ShouldBeTrue();
        var printedCode = original.LastSealShortCode!; // lo que quedó impreso en el Z

        // El administrador del equipo desactiva el disparador, altera una fila y recalcula su hash, los resúmenes y toda la cadena.
        var row = await ReadRowAsync(harness, 1);
        row.Summary = "reescrito";
        row.RowHash = AuditHasher.ComputeRowHash(row);
        await harness.Database.ExecuteAsync(
            $"""
            ALTER TABLE audit.audit_log DISABLE TRIGGER trg_audit_log_append_only;
            UPDATE audit.audit_log SET summary = 'reescrito', row_hash = '{row.RowHash}' WHERE seq = 1;
            ALTER TABLE audit.audit_log ENABLE TRIGGER trg_audit_log_append_only;
            """);
        await RewriteChainAsync(harness);

        var rewritten = await verifier.VerifyAsync(Ct);
        rewritten.IsValid.ShouldBeTrue("una reescritura completa y coherente no se puede detectar desde dentro de la BD (ADR-0012)");
        rewritten.LastSealShortCode.ShouldNotBe(printedCode);
        var check = await verifier.CheckSealCodeAsync(row.NodeId, 2, printedCode, Ct);
        check.ShouldNotBeNull();
        check.Value.Matches.ShouldBeFalse("el código impreso en el Z ya no coincide: la reescritura queda descubierta");
    }

    [Fact]
    public async Task La_verificacion_incremental_revisa_lo_nuevo_y_la_completa_todo()
    {
        await using var harness = await CreateAsync();
        await WriteEntriesAsync(harness, 2);
        await SealAllAsync(harness);
        await WriteEntriesAsync(harness, 2);
        await SealAllAsync(harness);
        var verifier = harness.Services.GetRequiredService<AuditVerifier>();
        var node = (await ReadRowAsync(harness, 1)).NodeId;
        var verifiedUpToSeal1 = new Dictionary<Guid, long> { [node] = 1 };

        // Alteración en el sello 1 (ya verificado): la incremental no recalcula esas filas; la completa (domingo) sí la detecta.
        await harness.Database.ExecuteAsync("SET session_replication_role = replica; UPDATE audit.audit_log SET summary = 'x' WHERE seq = 1;");
        var incremental = await verifier.VerifyAsync(verifiedUpToSeal1, Ct);
        incremental.IsValid.ShouldBeTrue();
        incremental.RowsChecked.ShouldBe(2);
        incremental.LastSealNoByNode[node].ShouldBe(2);
        (await verifier.VerifyAsync(Ct)).Findings.ShouldContain(f => f.Kind == AuditFindingKind.RowAltered && f.Seq == 1);

        // Alteración en el sello 2 (nuevo): la incremental la detecta.
        await harness.Database.ExecuteAsync("SET session_replication_role = replica; UPDATE audit.audit_log SET summary = 'y' WHERE seq = 3;");
        (await verifier.VerifyAsync(verifiedUpToSeal1, Ct)).Findings.ShouldContain(f => f.Kind == AuditFindingKind.RowAltered && f.Seq == 3);
    }

    [Fact]
    public async Task Las_tablas_de_verificaciones_e_incidentes_son_de_solo_insercion()
    {
        await using var harness = await CreateAsync();
        var app = harness.Database.AppConnectionString;
        await harness.Database.ExecuteAsync(
            """
            INSERT INTO audit.verification_runs (id, node_id, kind, started_at, finished_at, seals_checked, rows_checked, unsealed_rows, is_valid,
                findings_count, findings)
            VALUES ('01920000-0000-7000-8000-00000000a001', '01920000-0000-7000-8000-00000000a002', 'MANUAL', now(), now(), 0, 0, 0, true, 0, '[]')
            """,
            app);

        var update = await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("UPDATE audit.verification_runs SET is_valid = true", app));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        var delete = await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("DELETE FROM audit.verification_runs"));
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        var catalog = await Should.ThrowAsync<PostgresException>(() =>
            harness.Database.ExecuteAsync("INSERT INTO audit.action_types VALUES ('FAKE_ACTION', 'x', 'x', 'INFO')", app));
        catalog.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>Recalcula resúmenes y hashes de todos los sellos, en orden, como lo haría quien reescribe la bitácora.</summary>
    private static async Task RewriteChainAsync(InfrastructureHarness harness)
    {
        await using var connection = new NpgsqlConnection(harness.Database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        var seals = new List<(long SealNo, Guid Node, long From, long To, DateTimeOffset SealedAt)>();
        await using (var read = new NpgsqlCommand("SELECT seal_no, node_id, seq_from, seq_to, sealed_at FROM audit.audit_seals ORDER BY seal_no", connection))
        await using (var reader = await read.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                seals.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        var previous = AuditHasher.GenesisHash;
        foreach (var seal in seals)
        {
            var hashes = new List<string>();
            await using (var rows = new NpgsqlCommand("SELECT row_hash FROM audit.audit_log WHERE seq BETWEEN @f AND @t ORDER BY seq", connection))
            {
                rows.Parameters.AddWithValue("f", seal.From);
                rows.Parameters.AddWithValue("t", seal.To);
                await using var reader = await rows.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                    hashes.Add(reader.GetString(0));
                }
            }

            var digest = AuditHasher.ComputeRowsDigest(hashes);
            var hash = AuditHasher.ComputeSealHash(seal.Node, seal.SealNo, seal.From, seal.To, hashes.Count, digest, previous, seal.SealedAt);
            await using var update = new NpgsqlCommand(
                """
                SET session_replication_role = replica;
                UPDATE audit.audit_seals SET rows_digest = @d, prev_seal_hash = @p, seal_hash = @h WHERE seal_no = @n;
                """,
                connection);
            update.Parameters.AddWithValue("d", digest);
            update.Parameters.AddWithValue("p", previous);
            update.Parameters.AddWithValue("h", hash);
            update.Parameters.AddWithValue("n", seal.SealNo);
            await update.ExecuteNonQueryAsync(Ct);
            previous = hash;
        }
    }

    private Task<InfrastructureHarness> CreateAsync() =>
        InfrastructureHarness.CreateAsync(postgres, services => services.AddScoped<IRequestContext>(_ => new FixedRequestContext()));

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
        public string? CorrelationId => "prueba-fase-10";

        public IPAddress? IpAddress => IPAddress.Parse("192.168.1.30");

        public Guid? DeviceId => null;
    }
}
