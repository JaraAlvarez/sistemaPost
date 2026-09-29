using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Pos.Infrastructure.Auditing;

/// <summary>Tipo de hallazgo del verificador.</summary>
public enum AuditFindingKind
{
    /// <summary>Una fila no coincide con su row_hash: fue modificada.</summary>
    RowAltered,

    /// <summary>Las filas de un rango sellado no coinciden con el sello: se borró o insertó una fila.</summary>
    SealContentMismatch,

    /// <summary>El hash de un sello no corresponde a su contenido.</summary>
    SealAltered,

    /// <summary>La cadena de sellos está rota (prev_seal_hash, numeración o rangos).</summary>
    ChainBroken,
}

public sealed record AuditFinding(AuditFindingKind Kind, Guid NodeId, long? SealNo, long? Seq, string Message);

/// <summary>Resultado de la verificación de la bitácora.</summary>
public sealed record AuditVerificationReport(
    IReadOnlyList<AuditFinding> Findings,
    int NodesChecked,
    int SealsChecked,
    long RowsChecked,
    long UnsealedRows,
    string? LastSealHash)
{
    public bool IsValid => Findings.Count == 0;

    /// <summary>Código corto del último sello (el que se imprime en el reporte Z).</summary>
    public string? LastSealShortCode => LastSealHash is null ? null : AuditHasher.ShortCode(LastSealHash);
}

/// <summary>
/// Verifica la bitácora (comando verify-audit y consulta de auditoría): recalcula filas → rows_digest → seal_hash →
/// cadena. Detecta filas alteradas, borradas o insertadas en rangos sellados (revisión arquitectónica §4.3).
/// </summary>
public sealed class AuditVerifier(NpgsqlDataSource dataSource)
{
    private const string RowColumns = """
        id, occurred_at, node_id, seq, hash_version, company_id, branch_id, pos_terminal_id, user_id, user_display_name,
        session_id, device_id, ip_address, correlation_id, module, action, entity_type, entity_id, entity_label,
        old_values::text, new_values::text, summary, authorized_by, severity, row_hash
        """;

    public async Task<AuditVerificationReport> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<AuditFinding>();
        var sealsChecked = 0;
        long rowsChecked = 0;
        long unsealed = 0;
        string? lastSealHash = null;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Lectura consistente de toda la bitácora.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);

        var nodes = await ReadListAsync(connection, transaction,
            "SELECT DISTINCT node_id FROM audit.audit_log UNION SELECT DISTINCT node_id FROM audit.audit_seals", r => r.GetGuid(0), cancellationToken);

        foreach (var nodeId in nodes)
        {
            var seals = await ReadSealsAsync(connection, transaction, nodeId, cancellationToken);
            var previousHash = AuditHasher.GenesisHash;
            long previousSeqTo = 0;
            long expectedSealNo = 1;

            foreach (var seal in seals)
            {
                sealsChecked++;
                if (seal.SealNo != expectedSealNo || seal.SeqFrom != previousSeqTo + 1 || seal.PrevSealHash != previousHash)
                {
                    findings.Add(new AuditFinding(AuditFindingKind.ChainBroken, nodeId, seal.SealNo, null,
                        $"El sello {seal.SealNo} no continúa la cadena (se esperaba el sello {expectedSealNo} desde el seq {previousSeqTo + 1})."));
                }

                var expectedSealHash = AuditHasher.ComputeSealHash(
                    nodeId, seal.SealNo, seal.SeqFrom, seal.SeqTo, seal.RowsCount, seal.RowsDigest, seal.PrevSealHash, seal.SealedAt);
                if (expectedSealHash != seal.SealHash)
                {
                    findings.Add(new AuditFinding(AuditFindingKind.SealAltered, nodeId, seal.SealNo, null,
                        $"El sello {seal.SealNo} fue modificado: su hash no corresponde a su contenido."));
                }

                var rows = await ReadRowsAsync(connection, transaction, nodeId, seal.SeqFrom, seal.SeqTo, cancellationToken);
                rowsChecked += rows.Count;
                CheckRows(rows, nodeId, seal.SealNo, findings);

                var digest = AuditHasher.ComputeRowsDigest(rows.Select(r => r.RowHash));
                if (rows.Count != seal.RowsCount || digest != seal.RowsDigest)
                {
                    findings.Add(new AuditFinding(AuditFindingKind.SealContentMismatch, nodeId, seal.SealNo, null,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"El rango seq {seal.SeqFrom}–{seal.SeqTo} tiene {rows.Count} filas y el sello registró {seal.RowsCount}, o su contenido cambió: se borraron o insertaron filas después de sellar.")));
                }

                previousHash = seal.SealHash;
                previousSeqTo = seal.SeqTo;
                expectedSealNo = seal.SealNo + 1;
                lastSealHash = seal.SealHash;
            }

            var tail = await ReadRowsAsync(connection, transaction, nodeId, previousSeqTo + 1, long.MaxValue, cancellationToken);
            rowsChecked += tail.Count;
            unsealed += tail.Count;
            CheckRows(tail, nodeId, null, findings);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AuditVerificationReport(findings, nodes.Count, sealsChecked, rowsChecked, unsealed, lastSealHash);
    }

    private static void CheckRows(List<AuditLogRecord> rows, Guid nodeId, long? sealNo, List<AuditFinding> findings)
    {
        foreach (var row in rows.Where(r => AuditHasher.ComputeRowHash(r) != r.RowHash))
        {
            findings.Add(new AuditFinding(AuditFindingKind.RowAltered, nodeId, sealNo, row.Seq,
                string.Create(CultureInfo.InvariantCulture, $"La fila seq {row.Seq} ({row.Action}) fue modificada.")));
        }
    }

    private static Task<List<AuditSealRecord>> ReadSealsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid nodeId, CancellationToken cancellationToken) =>
        ReadListAsync(
            connection,
            transaction,
            """
            SELECT node_id, seal_no, format_version, seq_from, seq_to, rows_count, rows_digest, prev_seal_hash, seal_hash, sealed_at
            FROM audit.audit_seals WHERE node_id = @nodeId ORDER BY seal_no
            """,
            r => new AuditSealRecord(
                r.GetGuid(0), r.GetInt64(1), r.GetInt16(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5),
                r.GetString(6), r.GetString(7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9)),
            cancellationToken,
            ("nodeId", nodeId));

    private static Task<List<AuditLogRecord>> ReadRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid nodeId, long seqFrom, long seqTo, CancellationToken cancellationToken) =>
        ReadListAsync(
            connection,
            transaction,
            $"SELECT {RowColumns} FROM audit.audit_log WHERE node_id = @nodeId AND seq BETWEEN @seqFrom AND @seqTo ORDER BY seq",
            ReadRow,
            cancellationToken,
            ("nodeId", nodeId),
            ("seqFrom", seqFrom),
            ("seqTo", seqTo));

    private static AuditLogRecord ReadRow(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        OccurredAt = r.GetFieldValue<DateTimeOffset>(1),
        NodeId = r.GetGuid(2),
        Seq = r.GetInt64(3),
        HashVersion = r.GetInt16(4),
        CompanyId = NullableGuid(r, 5),
        BranchId = NullableGuid(r, 6),
        PosTerminalId = NullableGuid(r, 7),
        UserId = NullableGuid(r, 8),
        UserDisplayName = NullableString(r, 9),
        SessionId = NullableGuid(r, 10),
        DeviceId = NullableGuid(r, 11),
        IpAddress = r.IsDBNull(12) ? null : r.GetFieldValue<IPAddress>(12),
        CorrelationId = NullableString(r, 13),
        Module = r.GetString(14),
        Action = r.GetString(15),
        EntityType = NullableString(r, 16),
        EntityId = NullableGuid(r, 17),
        EntityLabel = NullableString(r, 18),
        OldValues = r.IsDBNull(19) ? null : JsonDocument.Parse(r.GetString(19)),
        NewValues = r.IsDBNull(20) ? null : JsonDocument.Parse(r.GetString(20)),
        Summary = NullableString(r, 21),
        AuthorizedBy = NullableGuid(r, 22),
        Severity = r.GetString(23),
        RowHash = r.GetString(24),
    };

    private static Guid? NullableGuid(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    private static string? NullableString(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static async Task<List<T>> ReadListAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
#pragma warning disable CA2100 // SQL constante; los valores van como parámetros.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(map(reader));
        }

        return result;
    }
}
