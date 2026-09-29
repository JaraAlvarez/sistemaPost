using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Pos.Infrastructure.Auditing;

/// <summary>
/// Hashes de la bitácora (revisión arquitectónica §4.2). El FORMATO es un contrato: una vez publicada una versión,
/// su lista de campos no cambia. Agregar columnas a audit_log exige una <see cref="CurrentVersion"/> nueva; las filas
/// antiguas se siguen verificando con su propia versión.
/// </summary>
public static class AuditHasher
{
    /// <summary>Versión de la lista de campos de <see cref="ComputeRowHash"/>.</summary>
    public const short CurrentVersion = 1;

    /// <summary>Versión del formato del sello.</summary>
    public const short SealFormatVersion = 1;

    /// <summary>prev_seal_hash del primer sello de cada nodo.</summary>
    public static readonly string GenesisHash = new('0', 64);

    /// <summary>Trunca a microsegundos: la precisión de timestamptz. Se aplica ANTES de hashear y de guardar.</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    public static string ComputeRowHash(AuditLogRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.HashVersion != CurrentVersion)
        {
            throw new NotSupportedException($"Versión de hash de auditoría no soportada: {row.HashVersion}.");
        }

        var payload = new JsonObject
        {
            ["v"] = row.HashVersion,
            ["node_id"] = Uuid(row.NodeId),
            ["seq"] = row.Seq,
            ["id"] = Uuid(row.Id),
            ["occurred_at"] = Timestamp(row.OccurredAt),
            ["company_id"] = Uuid(row.CompanyId),
            ["branch_id"] = Uuid(row.BranchId),
            ["pos_terminal_id"] = Uuid(row.PosTerminalId),
            ["user_id"] = Uuid(row.UserId),
            ["user_display_name"] = row.UserDisplayName,
            ["session_id"] = Uuid(row.SessionId),
            ["device_id"] = Uuid(row.DeviceId),
            ["ip_address"] = row.IpAddress?.ToString(),
            ["correlation_id"] = row.CorrelationId,
            ["module"] = row.Module,
            ["action"] = row.Action,
            ["entity_type"] = row.EntityType,
            ["entity_id"] = Uuid(row.EntityId),
            ["entity_label"] = row.EntityLabel,
            ["old_values"] = Json(row.OldValues),
            ["new_values"] = Json(row.NewValues),
            ["summary"] = row.Summary,
            ["authorized_by"] = Uuid(row.AuthorizedBy),
            ["severity"] = row.Severity,
        };

        return Sha256(CanonicalJson.Serialize(payload));
    }

    /// <summary>SHA-256 de la concatenación de los row_hash (en bytes) en orden de seq.</summary>
    public static string ComputeRowsDigest(IEnumerable<string> rowHashes)
    {
        ArgumentNullException.ThrowIfNull(rowHashes);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var hash in rowHashes)
        {
            sha.AppendData(Convert.FromHexString(hash));
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public static string ComputeSealHash(
        Guid nodeId, long sealNo, long seqFrom, long seqTo, int rowsCount, string rowsDigest, string prevSealHash, DateTimeOffset sealedAt)
    {
        var payload = new JsonObject
        {
            ["v"] = SealFormatVersion,
            ["node_id"] = Uuid(nodeId),
            ["seal_no"] = sealNo,
            ["seq_from"] = seqFrom,
            ["seq_to"] = seqTo,
            ["rows_count"] = rowsCount,
            ["rows_digest"] = rowsDigest,
            ["prev_seal_hash"] = prevSealHash,
            ["sealed_at"] = Timestamp(sealedAt),
        };

        return Sha256(CanonicalJson.Serialize(payload));
    }

    /// <summary>Código corto del sello para imprimir en el reporte Z: 16 caracteres en 4 grupos.</summary>
    public static string ShortCode(string sealHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sealHash);
        var upper = sealHash[..16].ToUpperInvariant();
        return $"{upper[..4]}-{upper[4..8]}-{upper[8..12]}-{upper[12..16]}";
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? Uuid(Guid? value) => value?.ToString("D");

    private static string Timestamp(DateTimeOffset value) =>
        TruncateToMicroseconds(value).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static JsonNode? Json(System.Text.Json.JsonDocument? document) =>
        document is null ? null : JsonNode.Parse(document.RootElement.GetRawText());
}
