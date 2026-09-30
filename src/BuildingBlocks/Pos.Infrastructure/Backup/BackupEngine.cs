using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.Backup;

/// <summary>Qué se respalda: la BD (rol <c>pos_backup</c>, lectura total) y el archivo de configuración de la instalación.</summary>
public sealed record BackupSource(string BackupConnectionString, string? ServerConfigFile, string AppVersion);

public sealed record BackupCreated(string FilePath, BackupHeader Header, long Size);

/// <summary>Resultado de verificar un paquete: huellas, manifiesto y volcado legible (RN-BAK-01).</summary>
public sealed record BackupVerification(BackupHeader Header, int DumpEntries);

/// <summary>
/// Motor de backups compartido por el servidor (tarea programada y API) y la consola (<c>Pos.Server.Migrator</c>): crea el paquete
/// cifrado dentro de una foto consistente de la BD, lo verifica y extrae el volcado para restaurarlo (D11-01/D11-02/D11-08).
/// </summary>
public sealed partial class BackupEngine(IPgTools tools)
{
    /// <summary>Tablas cuyo número de filas va en el encabezado: la restauración (y la prueba semanal) las compara.</summary>
    public static readonly string[] CountedTables =
        ["sales.sales", "sales.sale_lines", "inventory.stock_movements", "audit.audit_log", "catalog.products", "cash.cash_sessions", "purchasing.purchases"];

    public async Task<BackupCreated> CreateAsync(
        BackupSource source, string kind, byte[] dataKey, string outputDirectory, TimeProvider time, TimeZoneInfo businessZone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        Directory.CreateDirectory(outputDirectory);
        var work = Path.Combine(outputDirectory, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var dumpFile = Path.Combine(work, "database.dump");
            BackupInfo info;
            await using (var connection = new NpgsqlConnection(source.BackupConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition("SET TRANSACTION READ ONLY", transaction: transaction, cancellationToken: cancellationToken));
                var snapshot = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT pg_export_snapshot()", transaction: transaction, cancellationToken: cancellationToken));
                info = await ReadInfoAsync(connection, transaction, cancellationToken);

                // Mientras la transacción siga abierta, pg_dump ve exactamente la misma foto que los conteos y el sello.
                await tools.DumpAsync(source.BackupConnectionString, dumpFile, snapshot, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            var zipFile = Path.Combine(work, "payload.zip");
            await using (var zipStream = File.Create(zipFile))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var manifest = new Dictionary<string, object?>();
                manifest["database.dump"] = await AddFileAsync(zip, "database.dump", dumpFile, cancellationToken);
                if (source.ServerConfigFile is { } config && File.Exists(config))
                {
                    var sanitized = Sanitize(await File.ReadAllTextAsync(config, cancellationToken));
                    var configFile = Path.Combine(work, "server.json");
                    await File.WriteAllTextAsync(configFile, sanitized, cancellationToken);
                    manifest["server.json"] = await AddFileAsync(zip, "server.json", configFile, cancellationToken);
                }

                var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var writer = entry.Open();
                await JsonSerializer.SerializeAsync(writer, manifest, cancellationToken: cancellationToken);
            }

            string payloadHash;
            long payloadSize;
            await using (var zipRead = File.OpenRead(zipFile))
            {
                payloadHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(zipRead, cancellationToken));
                payloadSize = zipRead.Length;
            }

            var now = time.GetUtcNow();
            var header = new BackupHeader(
                BackupPackage.CurrentFormat, source.AppVersion, info.SchemaVersion, info.CompanyIdentification, info.CompanyName, info.BranchCode,
                info.NodeId, info.NodeEpoch, now, kind, info.SealNo, info.SealCode, payloadHash, payloadSize, BackupKeyStore.KeyId(dataKey),
                info.RecoveryKey, info.Counts);
            var target = Path.Combine(outputDirectory, BackupPackage.FileName(header, TimeZoneInfo.ConvertTime(now, businessZone)));
            await using (var output = File.Create(target))
            await using (var payload = File.OpenRead(zipFile))
            {
                await BackupPackage.WriteAsync(output, header, payload, dataKey, cancellationToken);
            }

            return new BackupCreated(target, header, new FileInfo(target).Length);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>Descifra, comprueba huellas y manifiesto, y lista el volcado (un backup no verificado no cuenta).</summary>
    public async Task<BackupVerification> VerifyAsync(string packageFile, byte[] dataKey, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "posbak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var (header, dump) = await ExtractAsync(packageFile, dataKey, work, cancellationToken);
            return new BackupVerification(header, await tools.ListAsync(dump, cancellationToken));
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>Descifra el paquete en <paramref name="workDirectory"/>, valida el manifiesto y devuelve la ruta del volcado.</summary>
    public static async Task<(BackupHeader Header, string DumpFile)> ExtractAsync(
        string packageFile, byte[] dataKey, string workDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        var zipFile = Path.Combine(workDirectory, "payload.zip");
        BackupHeader header;
        await using (var input = File.OpenRead(packageFile))
        await using (var payload = File.Create(zipFile))
        {
            header = await BackupPackage.DecryptAsync(input, dataKey, payload, cancellationToken);
        }

        var dumpFile = Path.Combine(workDirectory, "database.dump");
        await using (var zipStream = File.OpenRead(zipFile))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            var manifestEntry = zip.GetEntry("manifest.json") ?? throw new BackupPackageException("El backup no tiene manifiesto.");
            Dictionary<string, ManifestEntry>? manifest;
            await using (var reader = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<Dictionary<string, ManifestEntry>>(reader, cancellationToken: cancellationToken);
            }

            foreach (var (name, expected) in manifest ?? [])
            {
                var entry = zip.GetEntry(name) ?? throw new BackupPackageException($"Falta {name} en el backup.");
                var destination = Path.Combine(workDirectory, name);
                entry.ExtractToFile(destination, overwrite: true);
                await using var check = File.OpenRead(destination);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancellationToken)) != expected.Sha256)
                {
                    throw new BackupPackageException($"{name} no coincide con el manifiesto del backup.");
                }
            }
        }

        File.Delete(zipFile);
        return File.Exists(dumpFile) ? (header, dumpFile) : throw new BackupPackageException("El backup no contiene el volcado de la base de datos.");
    }

    /// <summary>Conteo de las tablas de <see cref="CountedTables"/> (para comparar un backup con su restauración).</summary>
    public static async Task<Dictionary<string, long>> CountAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in CountedTables)
        {
#pragma warning disable CA2100 // Nombres de tabla constantes del código.
            counts[table] = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT count(*) FROM {table}", transaction: transaction, cancellationToken: cancellationToken));
#pragma warning restore CA2100
        }

        return counts;
    }

    private static async Task<BackupInfo> ReadInfoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var installation = await connection.QuerySingleAsync<(Guid NodeId, int Epoch, Guid? CompanyId, Guid? BranchId)>(new CommandDefinition(
            "SELECT installation_id, node_epoch, home_company_id, home_branch_id FROM system.installation",
            transaction: transaction, cancellationToken: cancellationToken));
        var company = await connection.QuerySingleOrDefaultAsync<(string Identification, string Name)?>(new CommandDefinition(
            "SELECT identification_number || COALESCE('-' || check_digit, ''), COALESCE(NULLIF(trade_name, ''), legal_name) FROM org.companies WHERE id = @id",
            new { id = installation.CompanyId }, transaction, cancellationToken: cancellationToken));
        var branch = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT code FROM org.branches WHERE id = @id", new { id = installation.BranchId }, transaction, cancellationToken: cancellationToken));
        var schema = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT max(version) FROM system.schema_migrations WHERE kind = 'VERSIONED'", transaction: transaction, cancellationToken: cancellationToken));
        var seal = await connection.QuerySingleOrDefaultAsync<(long SealNo, string Hash)?>(new CommandDefinition(
            "SELECT seal_no, seal_hash FROM audit.audit_seals WHERE node_id = @node ORDER BY seal_no DESC LIMIT 1",
            new { node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
        WrappedKey? recovery = null;
        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT to_regclass('backup.recovery_keys') IS NOT NULL", transaction: transaction, cancellationToken: cancellationToken)))
        {
            var wrapped = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT wrapped_key::text FROM backup.recovery_keys ORDER BY version DESC LIMIT 1", transaction: transaction, cancellationToken: cancellationToken));
            recovery = wrapped is null ? null : JsonSerializer.Deserialize<WrappedKey>(wrapped, WebJson);
        }

        return new BackupInfo(
            installation.NodeId, installation.Epoch, company?.Identification ?? "SIN-EMPRESA", company?.Name ?? string.Empty, branch ?? "S00",
            schema ?? string.Empty, seal?.SealNo, seal is { } s ? AuditHasher.ShortCode(s.Hash) : null, recovery,
            await CountAsync(connection, transaction, cancellationToken));
    }

    internal static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static async Task<ManifestEntry> AddFileAsync(ZipArchive zip, string name, string file, CancellationToken cancellationToken)
    {
        string hash;
        await using (var read = File.OpenRead(file))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, cancellationToken));
        }

        // El volcado ya viene comprimido por pg_dump.
        zip.CreateEntryFromFile(file, name, name.EndsWith(".dump", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        return new ManifestEntry(hash, new FileInfo(file).Length);
    }

    /// <summary>La configuración viaja sin secretos: los valores DPAPI solo sirven en el equipo original y las contraseñas se ocultan.</summary>
    public static string Sanitize(string json)
    {
        var node = JsonNode.Parse(json);
        Walk(node);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";

        static void Walk(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var property in obj.ToList())
                    {
                        if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            obj[property.Key] = text.StartsWith("dpapi:", StringComparison.Ordinal) ? "(protegido en el equipo original)" : PasswordPattern().Replace(text, "$1=***");
                        }
                        else
                        {
                            Walk(property.Value);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Walk(item);
                    }

                    break;
            }
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo.
        }
        catch (UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    [GeneratedRegex("(?i)(password|pwd)=[^;]*", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();

    private sealed record BackupInfo(
        Guid NodeId,
        int NodeEpoch,
        string CompanyIdentification,
        string CompanyName,
        string BranchCode,
        string SchemaVersion,
        long? SealNo,
        string? SealCode,
        WrappedKey? RecoveryKey,
        IReadOnlyDictionary<string, long> Counts);

    private sealed record ManifestEntry(string Sha256, long Size);
}

/// <summary>Pedido de restauración (D11-09): el paquete, cómo abrirlo y dónde restaurarlo.</summary>
public sealed record RestoreRequest(
    string PackageFile,
    string? RecoveryCode,
    BackupKeyStore? LocalKeys,
    string SuperuserConnectionString,
    string TargetDatabase,
    string LatestSchemaVersion,
    string WorkDirectory);

public sealed record RestoreOutcome(
    BackupHeader Header,
    string Database,
    AuditVerificationReport Audit,
    bool SealMatches,
    IReadOnlyDictionary<string, long> Counts,
    bool CountsMatch);

/// <summary>
/// Restauración en una BD NUEVA (D11-09): elige la clave (la local si el backup es de este equipo; si no, el código de recuperación),
/// rechaza backups de un esquema más nuevo (RN-BAK-02), crea la BD, restaura a nombre del dueño del esquema, aplica las migraciones
/// hacia adelante, verifica la auditoría y compara el sello y los conteos del encabezado. No toca la BD activa: cambiarla es un paso
/// aparte de quien llama (la consola), igual que incrementar la vida del nodo y auditar.
/// </summary>
public sealed class BackupRestoreProcedure(IPgTools tools)
{
    public const string SchemaOwnerRole = "pos_owner";

    public async Task<RestoreOutcome> RunAsync(
        RestoreRequest request,
        Func<string, string, CancellationToken, Task> createDatabase,
        Func<string, CancellationToken, Task> migrate,
        string migratorConnectionString,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(createDatabase);
        ArgumentNullException.ThrowIfNull(migrate);
        BackupHeader header;
        await using (var input = File.OpenRead(request.PackageFile))
        {
            header = await BackupPackage.ReadHeaderAsync(input, cancellationToken);
        }

        if (string.CompareOrdinal(header.SchemaVersion, request.LatestSchemaVersion) > 0)
        {
            throw new BackupPackageException(
                $"El backup es de una versión más nueva (esquema {header.SchemaVersion}) que la instalada ({request.LatestSchemaVersion}). Actualice primero.");
        }

        var key = ChooseKey(header, request) ?? throw new BackupPackageException(
            "No se pudo abrir el backup: el código de recuperación no corresponde (o falta) y la clave de este equipo es otra.");
        var (_, dump) = await BackupEngine.ExtractAsync(request.PackageFile, key, request.WorkDirectory, cancellationToken);

        await createDatabase(request.SuperuserConnectionString, request.TargetDatabase, cancellationToken);
        var target = new NpgsqlConnectionStringBuilder(request.SuperuserConnectionString) { Database = request.TargetDatabase }.ConnectionString;
        await tools.RestoreAsync(target, dump, SchemaOwnerRole, cancellationToken);
        var migrator = new NpgsqlConnectionStringBuilder(migratorConnectionString) { Database = request.TargetDatabase }.ConnectionString;
        await migrate(migrator, cancellationToken);

        await using var dataSource = NpgsqlDataSource.Create(migrator);
        var audit = await new AuditVerifier(dataSource).VerifyAsync(cancellationToken);
        var sealMatches = header.AuditSealNo is null || await SealMatchesAsync(dataSource, header, cancellationToken);
        Dictionary<string, long> counts;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            counts = await BackupEngine.CountAsync(connection, null, cancellationToken);
        }

        var countsMatch = header.Counts.All(c => counts.GetValueOrDefault(c.Key) == c.Value);
        return new RestoreOutcome(header, request.TargetDatabase, audit, sealMatches, counts, countsMatch);
    }

    /// <summary>La clave local si corresponde a este backup; si no, la que abre el código de recuperación.</summary>
    public static byte[]? ChooseKey(BackupHeader header, RestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(request);
        if (request.LocalKeys is { Exists: true } local)
        {
            var key = local.Read();
            if (BackupKeyStore.KeyId(key) == header.KeyId)
            {
                return key;
            }
        }

        return request.RecoveryCode is { } code && header.RecoveryKey?.Unwrap(code) is { } unwrapped && BackupKeyStore.KeyId(unwrapped) == header.KeyId
            ? unwrapped
            : null;
    }

    private static async Task<bool> SealMatchesAsync(NpgsqlDataSource dataSource, BackupHeader header, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var hash = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT seal_hash FROM audit.audit_seals WHERE node_id = @node AND seal_no = @no",
            new { node = header.NodeId, no = header.AuditSealNo }, cancellationToken: cancellationToken));
        return hash is not null && AuditHasher.ShortCode(hash) == header.AuditSealCode;
    }
}

/// <summary>
/// Después de restaurar (D11-09, RN-BAK-06): incrementa la "vida" del nodo en la instalación y en <c>org.nodes</c> y registra
/// <c>BACKUP_RESTORED</c> (CRÍTICO) en la bitácora de la BD restaurada, en una sola transacción.
/// </summary>
public sealed class BackupRestoreFinisher(IServiceProvider services)
{
    public async Task FinishAsync(BackupHeader header, string packageFile, string previousDatabase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        await services.GetRequiredService<IInstallationContext>().RefreshAsync(cancellationToken);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var epoch = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE system.installation SET node_epoch = node_epoch + 1 RETURNING node_epoch;
            """,
            transaction: transaction.GetDbTransaction(), cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE org.nodes SET epoch = @epoch WHERE id = (SELECT installation_id FROM system.installation)",
            new { epoch }, transaction.GetDbTransaction(), cancellationToken: cancellationToken));
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(
            new AuditEntry("backup", "BACKUP_RESTORED", "Backup", null, Path.GetFileName(packageFile),
                $"Restaurado el backup {header.Kind} del {clock.ToBusinessTime(header.CreatedAt):yyyy-MM-dd HH:mm} (esquema {header.SchemaVersion}, "
                + $"sello #{header.AuditSealNo}); la BD anterior quedó como '{previousDatabase}'. Vida del nodo: {epoch}.",
                Severity: AuditSeverity.Critical),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
