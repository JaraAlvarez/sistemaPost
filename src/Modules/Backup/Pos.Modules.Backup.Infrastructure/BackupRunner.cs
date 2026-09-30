using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Backup;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Security;
using Pos.Modules.Backup.Application;
using Pos.Modules.Backup.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Backup.Infrastructure;

/// <summary>Destino donde se copia el paquete (D11-06). Una ubicación no disponible (USB desconectada) deja la copia pendiente.</summary>
internal interface IDestinationClient
{
    Task<string> UploadAsync(string file, CancellationToken cancellationToken);

    Task DeleteAsync(string location, CancellationToken cancellationToken);

    Task<string?> TestAsync(CancellationToken cancellationToken);
}

/// <summary>El destino no está conectado ahora (disco externo retirado, red caída): se reintenta más tarde.</summary>
internal sealed class DestinationUnavailableException(string message) : Exception(message);

/// <summary>Carpeta local, disco externo (por letra y, si se indica, etiqueta del volumen) o carpeta de red (UNC con la cuenta del servicio).</summary>
internal sealed class FolderDestination(string path, string? volumeLabel) : IDestinationClient
{
    public async Task<string> UploadAsync(string file, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        Directory.CreateDirectory(path);
        var target = Path.Combine(path, Path.GetFileName(file));
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        var temporary = target + ".partial";
        await using (var source = File.OpenRead(file))
        await using (var destination = File.Create(temporary))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        File.Move(temporary, target, overwrite: true);
        return target;
    }

    public Task DeleteAsync(string location, CancellationToken cancellationToken)
    {
        if (File.Exists(location))
        {
            File.Delete(location);
        }

        return Task.CompletedTask;
    }

    public async Task<string?> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureAvailable();
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".prueba-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probe, "prueba", cancellationToken);
            var ok = await File.ReadAllTextAsync(probe, cancellationToken) == "prueba";
            File.Delete(probe);
            return ok ? null : "No se pudo leer lo escrito.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DestinationUnavailableException)
        {
            return ex.Message;
        }
    }

    private void EnsureAvailable()
    {
        if (string.IsNullOrWhiteSpace(volumeLabel) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return;
        }

        var root = Path.GetPathRoot(Path.GetFullPath(path));
        var drive = root is null ? null : new DriveInfo(root);
        if (drive is not { IsReady: true })
        {
            throw new DestinationUnavailableException($"El disco {root} no está conectado.");
        }

        if (!string.Equals(drive.VolumeLabel, volumeLabel, StringComparison.OrdinalIgnoreCase))
        {
            throw new DestinationUnavailableException($"El disco {root} no es '{volumeLabel}' (es '{drive.VolumeLabel}').");
        }
    }
}

/// <summary>Almacenamiento compatible S3 (MinIO en el VPS): <c>prefijo/NIT-sucursal/archivo.posbak</c>.</summary>
internal sealed class S3Destination(S3Storage storage, string keyPrefix) : IDestinationClient
{
    public async Task<string> UploadAsync(string file, CancellationToken cancellationToken)
    {
        var key = keyPrefix + Path.GetFileName(file);
        try
        {
            await storage.PutAsync(key, file, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new DestinationUnavailableException($"No hay conexión con el almacenamiento S3: {ex.Message}");
        }

        return key;
    }

    public Task DeleteAsync(string location, CancellationToken cancellationToken) => storage.DeleteAsync(location, cancellationToken);

    public async Task<string?> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var probe = Path.Combine(Path.GetTempPath(), $"prueba-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probe, "prueba", cancellationToken);
            var key = keyPrefix + Path.GetFileName(probe);
            await storage.PutAsync(key, probe, cancellationToken);
            File.Delete(probe);
            await storage.DeleteAsync(key, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            return ex.Message;
        }
    }
}

/// <summary>
/// Ejecuta los backups (D11-05 a D11-08): crea el paquete en el destino LOCAL, lo verifica, lo registra, lo copia a los destinos que
/// corresponden al tipo de backup, aplica la retención y audita. También hace la restauración de prueba semanal. Uno a la vez.
/// </summary>
internal sealed partial class BackupRunner(
    IServiceScopeFactory scopes,
    BackupEnvironment environment,
    IPgTools tools,
    IInstallationContext installation,
    IClock clock,
    TimeProvider time,
    ILogger<BackupRunner> logger) : IDisposable
{
    // Identificador criptográfico interno y estable (no es el nombre comercial): cambiarlo dejaría ilegibles los secretos ya cifrados.
    public const string DestinationPurpose = "PosSupermercado.Backup.Destination.v1";

    /// <summary>Un solo cliente para el almacenamiento S3 (subidas largas: hasta 2 horas).</summary>
    private static readonly HttpClient S3Http = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(environment.BackupConnectionString);

    internal BackupKeyStore Keys => new(environment.KeyFile);

    public void Dispose() => _gate.Dispose();

    public async Task<Guid?> RunAsync(string kind, Guid? requestedBy, CancellationToken cancellationToken)
    {
        if (!IsConfigured || installation.CompanyId is not { } companyId)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var started = clock.UtcNow;
            var runId = Guid.CreateVersion7();
            BackupCreated created;
            BackupVerification verification;
            try
            {
                var key = Keys.GetOrCreate();
                var engine = new BackupEngine(tools);
                created = await engine.CreateAsync(
                    new BackupSource(environment.BackupConnectionString!, environment.ServerConfigFile, environment.AppVersion),
                    kind, key, environment.LocalDirectory, time, clock.BusinessTimeZone, cancellationToken);
                verification = await engine.VerifyAsync(created.FilePath, key, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, kind, ex);
                await RecordFailureAsync(runId, kind, started, requestedBy, ex.Message, cancellationToken);
                return runId;
            }

            await RecordSuccessAsync(runId, companyId, kind, started, requestedBy, created, verification, cancellationToken);
            await CopyAsync(runId, kind, created.FilePath, cancellationToken);
            await ApplyRetentionAsync(cancellationToken);
            LogCompleted(logger, kind, created.FilePath, created.Size);
            return runId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reintenta las copias pendientes o fallidas (p. ej. el disco externo se conectó de nuevo).</summary>
    public async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            var connection = await OpenAsync(context, cancellationToken);
            var pending = (await connection.QueryAsync<(Guid CopyId, Guid DestinationId, string FileName)>(new CommandDefinition(
                """
                SELECT c.id, c.destination_id, r.file_name FROM backup.backup_copies c
                JOIN backup.backup_runs r ON r.id = c.run_id JOIN backup.destinations d ON d.id = c.destination_id
                WHERE c.status IN ('PENDING', 'FAILED') AND c.attempts < 50 AND d.is_active AND r.node_id = @node
                ORDER BY r.started_at DESC LIMIT 20
                """,
                new { node = installation.NodeId }, cancellationToken: cancellationToken))).ToList();
            foreach (var (copyId, destinationId, fileName) in pending)
            {
                var file = Path.Combine(environment.LocalDirectory, fileName);
                if (!File.Exists(file))
                {
                    continue;
                }

                var destination = await LoadDestinationAsync(connection, destinationId, cancellationToken);
                if (destination is not null)
                {
                    await CopyOneAsync(connection, copyId, destination, file, cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Restauración de prueba (D11-08): restaura el último backup verificado en una BD temporal (rol <c>pos_backup</c>), verifica la
    /// auditoría, el sello y los conteos del encabezado, borra la BD temporal y registra el resultado.
    /// </summary>
    public async Task<bool?> RestoreTestAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        var work = Path.Combine(environment.BackupsDirectory, ".restore-test-" + Guid.NewGuid().ToString("N"));
        var database = "pos_restoretest_" + Guid.NewGuid().ToString("N")[..12];
        var started = clock.UtcNow;
        Guid? runId = null;
        bool? auditValid = null, sealMatches = null, countsMatch = null;
        string? error = null;
        try
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var connection = await OpenAsync(scope.ServiceProvider.GetRequiredService<PosDbContext>(), cancellationToken);
                var latest = await connection.QuerySingleOrDefaultAsync<(Guid Id, string FileName)?>(new CommandDefinition(
                    "SELECT id, file_name FROM backup.backup_runs WHERE node_id = @node AND verified ORDER BY started_at DESC LIMIT 1",
                    new { node = installation.NodeId }, cancellationToken: cancellationToken));
                if (latest is not { } run || !File.Exists(Path.Combine(environment.LocalDirectory, run.FileName)))
                {
                    return null;
                }

                runId = run.Id;
                var (header, dump) = await BackupEngine.ExtractAsync(Path.Combine(environment.LocalDirectory, run.FileName), Keys.Read(), work, cancellationToken);
                await using (var admin = new NpgsqlConnection(environment.BackupConnectionString))
                {
                    await admin.OpenAsync(cancellationToken);
#pragma warning disable CA2100 // Nombre generado por el sistema (letras y dígitos).
                    await using var create = new NpgsqlCommand(
                        $"CREATE DATABASE {database} ENCODING 'UTF8' LOCALE_PROVIDER builtin BUILTIN_LOCALE 'C.UTF-8' TEMPLATE template0", admin);
#pragma warning restore CA2100
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }

                var target = new NpgsqlConnectionStringBuilder(environment.BackupConnectionString) { Database = database, Pooling = false }.ConnectionString;
                await tools.RestoreAsync(target, dump, null, cancellationToken);
                await using var dataSource = NpgsqlDataSource.Create(target);
                auditValid = (await new AuditVerifier(dataSource).VerifyAsync(cancellationToken)).IsValid;
                await using var check = await dataSource.OpenConnectionAsync(cancellationToken);
                var hash = header.AuditSealNo is null ? null : await check.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT seal_hash FROM audit.audit_seals WHERE node_id = @node AND seal_no = @no",
                    new { node = header.NodeId, no = header.AuditSealNo }, cancellationToken: cancellationToken));
                sealMatches = header.AuditSealNo is null || (hash is not null && AuditHasher.ShortCode(hash) == header.AuditSealCode);
                var counts = await BackupEngine.CountAsync(check, null, cancellationToken);
                countsMatch = header.Counts.All(c => counts.GetValueOrDefault(c.Key) == c.Value);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }
        finally
        {
            await DropAsync(database);
            TryDelete(work);
            _gate.Release();
        }

        if (runId is null)
        {
            return null;
        }

        var succeeded = error is null && auditValid == true && sealMatches == true && countsMatch == true;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var connection = await OpenAsync(context, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO backup.restore_tests (id, run_id, started_at, finished_at, succeeded, audit_valid, seal_matches, counts_match, error)
                VALUES (@id, @runId, @started, @finished, @succeeded, @auditValid, @sealMatches, @countsMatch, @error)
                """,
                new { id = Guid.CreateVersion7(), runId, started, finished = clock.UtcNow, succeeded, auditValid, sealMatches, countsMatch, error },
                cancellationToken: cancellationToken));
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(
                new AuditEntry("backup", succeeded ? "RESTORE_TEST_PASSED" : "RESTORE_TEST_FAILED", "Backup", runId, "Restauración de prueba",
                    succeeded
                        ? "La restauración de prueba del último backup fue correcta (auditoría, sello y conteos)."
                        : $"La restauración de prueba FALLÓ: {error ?? $"auditoría {auditValid}, sello {sealMatches}, conteos {countsMatch}"}.",
                    Severity: succeeded ? AuditSeverity.Info : AuditSeverity.Critical),
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return succeeded;
    }

    internal IDestinationClient Client(DestinationRecord destination)
    {
        var secret = destination.Secret is null ? null
            : OperatingSystem.IsWindows() ? System.Text.Encoding.UTF8.GetString(ProtectedSecret.UnprotectBytes(destination.Secret, DestinationPurpose))
            : System.Text.Encoding.UTF8.GetString(destination.Secret);
        return destination.Kind switch
        {
            DestinationKinds.Local => new FolderDestination(environment.LocalDirectory, null),
            DestinationKinds.S3 => new S3Destination(
                new S3Storage(S3Http, new S3Settings(destination.Endpoint!, destination.Region ?? "us-east-1", destination.Bucket!,
                    destination.AccessKey!, secret ?? string.Empty), time),
                KeyPrefix(destination.Prefix)),
            _ => new FolderDestination(destination.Path!, destination.Kind == DestinationKinds.External ? destination.VolumeLabel : null),
        };
    }

    internal static async Task<DestinationRecord?> LoadDestinationAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<DestinationRecord>(new CommandDefinition(
            """
            SELECT id AS Id, kind AS Kind, name AS Name, path AS Path, volume_label AS VolumeLabel, endpoint AS Endpoint, region AS Region, bucket AS Bucket,
                   prefix AS Prefix, access_key AS AccessKey, secret AS Secret, keep_daily AS KeepDaily, keep_weekly AS KeepWeekly, keep_monthly AS KeepMonthly,
                   on_scheduled AS OnScheduled, on_nightly AS OnNightly, on_closing AS OnClosing, on_manual AS OnManual
            FROM backup.destinations WHERE id = @id
            """,
            new { id }, cancellationToken: cancellationToken));

    internal static async Task<NpgsqlConnection> OpenAsync(PosDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (NpgsqlConnection)context.Database.GetDbConnection();
    }

    private string KeyPrefix(string? prefix)
    {
        var folder = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/') + "/";
        return folder + installation.NodeId.ToString("N") + "/";
    }

    private async Task RecordSuccessAsync(
        Guid runId, Guid companyId, string kind, DateTimeOffset started, Guid? requestedBy, BackupCreated created, BackupVerification verification,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = await OpenAsync(context, cancellationToken);
        var header = created.Header;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO backup.backup_runs (id, node_id, company_id, kind, started_at, finished_at, succeeded, file_name, size_bytes, payload_sha256,
                app_version, schema_version, audit_seal_no, audit_seal_code, verified, dump_entries, requested_by)
            VALUES (@runId, @node, @companyId, @kind, @started, @finished, true, @fileName, @size, @sha, @appVersion, @schema, @sealNo, @sealCode, true,
                @entries, @requestedBy)
            """,
            new
            {
                runId, node = installation.NodeId, companyId, kind, started, finished = clock.UtcNow, fileName = Path.GetFileName(created.FilePath),
                size = created.Size, sha = header.PayloadSha256, appVersion = header.AppVersion, schema = header.SchemaVersion, sealNo = header.AuditSealNo,
                sealCode = header.AuditSealCode, entries = verification.DumpEntries, requestedBy,
            },
            cancellationToken: cancellationToken));
        await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(
            new AuditEntry("backup", "BACKUP_COMPLETED", "Backup", runId, Path.GetFileName(created.FilePath),
                string.Create(CultureInfo.InvariantCulture,
                    $"Backup {kind} verificado ({created.Size / 1024.0 / 1024.0:0.0} MB, esquema {header.SchemaVersion}, sello #{header.AuditSealNo}).")),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(Guid runId, string kind, DateTimeOffset started, Guid? requestedBy, string error, CancellationToken cancellationToken)
    {
        var message = error.Length > 1000 ? error[..1000] : error;
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = await OpenAsync(context, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO backup.backup_runs (id, node_id, company_id, kind, started_at, finished_at, succeeded, verified, error, requested_by)
            VALUES (@runId, @node, @company, @kind, @started, @finished, false, false, @message, @requestedBy)
            """,
            new { runId, node = installation.NodeId, company = installation.CompanyId, kind, started, finished = clock.UtcNow, message, requestedBy },
            cancellationToken: cancellationToken));
        await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(
            new AuditEntry("backup", "BACKUP_FAILED", "Backup", runId, kind, $"El backup {kind} falló: {message}", Severity: AuditSeverity.Critical),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CopyAsync(Guid runId, string kind, string file, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var connection = await OpenAsync(context, cancellationToken);
        var destinations = (await connection.QueryAsync<DestinationRecord>(new CommandDefinition(
            """
            SELECT id AS Id, kind AS Kind, name AS Name, path AS Path, volume_label AS VolumeLabel, endpoint AS Endpoint, region AS Region, bucket AS Bucket,
                   prefix AS Prefix, access_key AS AccessKey, secret AS Secret, keep_daily AS KeepDaily, keep_weekly AS KeepWeekly, keep_monthly AS KeepMonthly,
                   on_scheduled AS OnScheduled, on_nightly AS OnNightly, on_closing AS OnClosing, on_manual AS OnManual
            FROM backup.destinations WHERE company_id = @company AND is_active
            """,
            new { company = installation.CompanyId }, cancellationToken: cancellationToken))).ToList();
        foreach (var destination in destinations.Where(d => d.Kind == DestinationKinds.Local || d.Applies(kind)))
        {
            var copyId = Guid.CreateVersion7();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO backup.backup_copies (id, run_id, destination_id, status) VALUES (@copyId, @runId, @destination, 'PENDING')",
                new { copyId, runId, destination = destination.Id }, cancellationToken: cancellationToken));
            await CopyOneAsync(connection, copyId, destination, file, cancellationToken);
        }
    }

    private async Task CopyOneAsync(NpgsqlConnection connection, Guid copyId, DestinationRecord destination, string file, CancellationToken cancellationToken)
    {
        string status;
        string? location = null;
        string? error = null;
        try
        {
            location = await Client(destination).UploadAsync(file, cancellationToken);
            status = "COPIED";
        }
        catch (DestinationUnavailableException ex)
        {
            status = "PENDING";
            error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            status = "FAILED";
            error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE backup.backup_copies SET status = @status, location = COALESCE(@location, location), attempts = attempts + 1, error = @error,
                copied_at = CASE WHEN @status = 'COPIED' THEN now() ELSE copied_at END
            WHERE id = @copyId;
            UPDATE backup.destinations SET last_status = CASE @status WHEN 'COPIED' THEN 'OK' WHEN 'PENDING' THEN 'PENDING' ELSE 'FAILED' END,
                last_error = @error, last_success_at = CASE WHEN @status = 'COPIED' THEN now() ELSE last_success_at END
            WHERE id = @destination;
            """,
            new { status, location, error, copyId, destination = destination.Id }, cancellationToken: cancellationToken));
    }

    /// <summary>Retención abuelo-padre-hijo por destino (D11-07): borra el archivo y marca la copia como DELETED.</summary>
    private async Task ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var connection = await OpenAsync(scope.ServiceProvider.GetRequiredService<PosDbContext>(), cancellationToken);
        var destinations = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM backup.destinations WHERE company_id = @company AND is_active", new { company = installation.CompanyId },
            cancellationToken: cancellationToken))).ToList();
        foreach (var destinationId in destinations)
        {
            var destination = await LoadDestinationAsync(connection, destinationId, cancellationToken);
            if (destination is null)
            {
                continue;
            }

            var copies = (await connection.QueryAsync<(Guid CopyId, string Location, string FileName, DateTime StartedAt, string Kind, bool Verified)>(
                new CommandDefinition(
                    """
                    SELECT c.id, c.location, r.file_name, r.started_at, r.kind, r.verified FROM backup.backup_copies c
                    JOIN backup.backup_runs r ON r.id = c.run_id WHERE c.destination_id = @id AND c.status = 'COPIED'
                    """,
                    new { id = destinationId }, cancellationToken: cancellationToken))).ToList();
            var policy = new RetentionPolicy(destination.KeepDaily, destination.KeepWeekly, destination.KeepMonthly);
            var byName = copies.ToDictionary(c => c.FileName, StringComparer.Ordinal);
            var delete = policy.ToDelete([.. copies.Select(c => new RetainedBackup(
                c.FileName, DateOnly.FromDateTime(clock.ToBusinessTime(BackupStore.Utc(c.StartedAt)).DateTime), c.Kind, c.Verified))]);
            foreach (var item in delete)
            {
                var copy = byName[item.Name];
                try
                {
                    await Client(destination).DeleteAsync(copy.Location, cancellationToken);
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE backup.backup_copies SET status = 'DELETED', deleted_at = now() WHERE id = @id", new { id = copy.CopyId },
                        cancellationToken: cancellationToken));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException
                    or DestinationUnavailableException)
                {
                    LogRetentionFailed(logger, item.Name, ex);
                }
            }
        }
    }

    private async Task DropAsync(string database)
    {
        try
        {
            await using var admin = new NpgsqlConnection(environment.BackupConnectionString);
            await admin.OpenAsync();
#pragma warning disable CA2100 // Nombre generado por el sistema.
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
#pragma warning restore CA2100
            await drop.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException ex)
        {
            LogDropFailed(logger, database, ex);
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
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el backup {Kind}.")]
    private static partial void LogFailed(ILogger logger, string kind, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {Kind} verificado: {File} ({Size} bytes).")]
    private static partial void LogCompleted(ILogger logger, string kind, string file, long size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo borrar {Name} por retención; se reintentará.")]
    private static partial void LogRetentionFailed(ILogger logger, string name, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo borrar la BD temporal {Database} de la restauración de prueba.")]
    private static partial void LogDropFailed(ILogger logger, string database, Exception exception);
}

/// <summary>Fila de un destino con su secreto cifrado (solo en la infraestructura).</summary>
internal sealed class DestinationRecord
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Path { get; set; }

    public string? VolumeLabel { get; set; }

    public string? Endpoint { get; set; }

    public string? Region { get; set; }

    public string? Bucket { get; set; }

    public string? Prefix { get; set; }

    public string? AccessKey { get; set; }

    public byte[]? Secret { get; set; }

    public int KeepDaily { get; set; }

    public int KeepWeekly { get; set; }

    public int KeepMonthly { get; set; }

    public bool OnScheduled { get; set; }

    public bool OnNightly { get; set; }

    public bool OnClosing { get; set; }

    public bool OnManual { get; set; }

    public bool Applies(string kind) => kind switch
    {
        BackupKinds.Scheduled => OnScheduled,
        BackupKinds.Nightly => OnNightly,
        BackupKinds.CashClosing => OnClosing,
        BackupKinds.Manual => OnManual,
        _ => false,
    };
}

/// <summary>
/// Proceso en segundo plano (D11-05): atiende los backups manuales en cuanto llegan y, cada minuto, revisa si toca el nocturno, el
/// programado (cada N horas en el horario), el de cierre de caja o la restauración de prueba semanal; reintenta las copias pendientes.
/// </summary>
internal sealed partial class BackupWorker(
    BackupRunner runner,
    IServiceScopeFactory scopes,
    DatabaseReadiness readiness,
    IInstallationContext installation,
    IClock clock,
    ILogger<BackupWorker> logger) : BackgroundService
{
    private readonly Channel<Guid?> _manual = Channel.CreateUnbounded<Guid?>();

    public void Enqueue(Guid? requestedBy) => _manual.Writer.TryWrite(requestedBy);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromMinutes(1));
                Guid? requestedBy = null;
                var manual = false;
                try
                {
                    requestedBy = await _manual.Reader.ReadAsync(wait.Token);
                    manual = true;
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Pasó el minuto sin pedidos manuales.
                }

                if (!readiness.IsReady || installation.CompanyId is null || !runner.IsConfigured)
                {
                    continue;
                }

                if (manual)
                {
                    await runner.RunAsync(BackupKinds.Manual, requestedBy, stoppingToken);
                    continue;
                }

                await RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    internal async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var companyId = installation.CompanyId!.Value;
        await using var scope = scopes.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsReader>();
        var context = new SettingContext(companyId);
        var connection = await BackupRunner.OpenAsync(scope.ServiceProvider.GetRequiredService<PosDbContext>(), cancellationToken);
        var last = (await connection.QueryAsync<(string Kind, DateTime StartedAt)>(new CommandDefinition(
            "SELECT kind, max(started_at) FROM backup.backup_runs WHERE node_id = @node GROUP BY kind",
            new { node = installation.NodeId }, cancellationToken: cancellationToken))).ToDictionary(r => r.Kind, r => BackupStore.Utc(r.StartedAt));
        var now = clock.UtcNow;
        var local = clock.ToBusinessTime(now);

        // Nocturno.
        var nightly = new DateTimeOffset(local.Date.AddHours(await settings.GetAsync(BackupSettings.NightlyHour, context, cancellationToken))
            .AddMinutes(await settings.GetAsync(BackupSettings.NightlyMinute, context, cancellationToken)), local.Offset);
        if (local >= nightly && (!last.TryGetValue(BackupKinds.Nightly, out var lastNightly) || lastNightly < nightly))
        {
            await runner.RunAsync(BackupKinds.Nightly, null, cancellationToken);
            return;
        }

        // Cierre de jornada.
        var lastClosed = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT max(closed_at) FROM cash.cash_sessions WHERE status = 'CLOSED'", cancellationToken: cancellationToken));
        var closingGap = TimeSpan.FromMinutes(await settings.GetAsync(BackupSettings.ClosingMinMinutes, context, cancellationToken));
        var lastClosing = last.TryGetValue(BackupKinds.CashClosing, out var c) ? c : DateTimeOffset.MinValue;
        if (lastClosed is { } closed && BackupStore.Utc(closed) > lastClosing && now - lastClosing >= closingGap)
        {
            await runner.RunAsync(BackupKinds.CashClosing, null, cancellationToken);
            return;
        }

        // Programado en el horario de la tienda.
        var start = await settings.GetAsync(BackupSettings.WindowStartHour, context, cancellationToken);
        var end = await settings.GetAsync(BackupSettings.WindowEndHour, context, cancellationToken);
        var interval = TimeSpan.FromHours(await settings.GetAsync(BackupSettings.IntervalHours, context, cancellationToken));
        var lastAny = last.Count == 0 ? DateTimeOffset.MinValue : last.Values.Max();
        if (local.Hour >= start && local.Hour < end && now - lastAny >= interval)
        {
            await runner.RunAsync(BackupKinds.Scheduled, null, cancellationToken);
            return;
        }

        // Restauración de prueba semanal.
        var weekday = await settings.GetAsync(BackupSettings.RestoreTestWeekday, context, cancellationToken);
        var hour = await settings.GetAsync(BackupSettings.RestoreTestHour, context, cancellationToken);
        if ((int)local.DayOfWeek == weekday && local.Hour >= hour)
        {
            var lastTest = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT max(t.started_at) FROM backup.restore_tests t JOIN backup.backup_runs r ON r.id = t.run_id WHERE r.node_id = @node",
                new { node = installation.NodeId }, cancellationToken: cancellationToken));
            if (lastTest is null || now - BackupStore.Utc(lastTest.Value) > TimeSpan.FromDays(6))
            {
                await runner.RestoreTestAsync(cancellationToken);
                return;
            }
        }

        await runner.RetryPendingAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el ciclo de backups; se reintentará.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>Operaciones para los casos de uso (puerto de la capa de aplicación).</summary>
internal sealed class BackupOperations(BackupRunner runner, BackupWorker worker, BackupEnvironment environment, IServiceScopeFactory scopes) : IBackupOperations
{
    public bool IsConfigured => runner.IsConfigured;

    public void EnqueueManual(Guid? requestedBy) => worker.Enqueue(requestedBy);

    public async Task<BackupVerificationDto> VerifyAsync(Guid runId, CancellationToken cancellationToken)
    {
        var path = await LocateAsync(runId, cancellationToken);
        if (path is null)
        {
            return new BackupVerificationDto(runId, false, 0, "El archivo ya no está en el destino LOCAL.");
        }

        try
        {
            var verification = await new BackupEngine(scopes.CreateScope().ServiceProvider.GetRequiredService<IPgTools>())
                .VerifyAsync(path, runner.Keys.Read(), cancellationToken);
            return new BackupVerificationDto(runId, true, verification.DumpEntries, null);
        }
        catch (Exception ex) when (ex is BackupPackageException or InvalidOperationException or IOException)
        {
            return new BackupVerificationDto(runId, false, 0, ex.Message);
        }
    }

    public async Task<string?> LocateAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var connection = await BackupRunner.OpenAsync(scope.ServiceProvider.GetRequiredService<PosDbContext>(), cancellationToken);
        var fileName = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT file_name FROM backup.backup_runs WHERE id = @runId", new { runId }, cancellationToken: cancellationToken));
        var path = fileName is null ? null : Path.Combine(environment.LocalDirectory, fileName);
        return path is not null && File.Exists(path) ? path : null;
    }

    public (string Code, string KeyId, string WrappedKeyJson) NewRecoveryKey(int version)
    {
        var key = runner.Keys.GetOrCreate();
        var code = RecoveryCode.Generate();
        var wrapped = WrappedKey.Wrap(key, code, version);
        return (code, BackupKeyStore.KeyId(key), JsonSerializer.Serialize(wrapped, BackupJson.Options));
    }

    public bool RecoveryCodeMatches(string wrappedKeyJson, string code) =>
        JsonSerializer.Deserialize<WrappedKey>(wrappedKeyJson, BackupJson.Options)?.Unwrap(code) is not null;

    public byte[] ProtectSecret(string secret)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(secret);
        return OperatingSystem.IsWindows() ? ProtectedSecret.ProtectBytes(bytes, BackupRunner.DestinationPurpose) : bytes;
    }

    public async Task<string?> TestDestinationAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var connection = await BackupRunner.OpenAsync(scope.ServiceProvider.GetRequiredService<PosDbContext>(), cancellationToken);
        var destination = await BackupRunner.LoadDestinationAsync(connection, destinationId, cancellationToken);
        return destination is null ? "El destino no existe." : await runner.Client(destination).TestAsync(cancellationToken);
    }
}

internal static class BackupJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Alertas de backups (D11-10).</summary>
internal sealed class BackupStatus(PosDbContext context, IInstallationContext installation, ISettingsReader settings, IClock clock) : IBackupStatus
{
    public async Task<BackupAlertsDto> GetAlertsAsync(CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return new BackupAlertsDto(null, false, 0, false, false);
        }

        var connection = await BackupRunner.OpenAsync(context, cancellationToken);
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        var lastVerified = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT max(finished_at) FROM backup.backup_runs WHERE node_id = @node AND verified", new { node = installation.NodeId }, transaction,
            cancellationToken: cancellationToken));
        var failing = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM backup.destinations WHERE company_id = @companyId AND is_active AND last_status = 'FAILED'", new { companyId },
            transaction, cancellationToken: cancellationToken));
        var lastTest = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            """
            SELECT t.succeeded FROM backup.restore_tests t JOIN backup.backup_runs r ON r.id = t.run_id
            WHERE r.node_id = @node ORDER BY t.started_at DESC LIMIT 1
            """,
            new { node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
        var recoveryConfirmed = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT confirmed_at IS NOT NULL FROM backup.recovery_keys ORDER BY version DESC LIMIT 1", transaction: transaction,
            cancellationToken: cancellationToken));
        var maxAge = TimeSpan.FromHours(await settings.GetAsync(BackupSettings.MaxAgeHours, new SettingContext(companyId), cancellationToken));
        DateTimeOffset? last = lastVerified is { } v ? BackupStore.Utc(v) : null;
        return new BackupAlertsDto(last, last is null || clock.UtcNow - last > maxAge, failing, lastTest == false, recoveryConfirmed != true);
    }
}

public static class BackupInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddScoped<IBackupStore, BackupStore>();
        services.AddScoped<IBackupStatus, BackupStatus>();
        services.AddSingleton<BackupRunner>();
        services.AddSingleton<BackupWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupWorker>());
        services.AddSingleton<IBackupOperations, BackupOperations>();
    }
}
