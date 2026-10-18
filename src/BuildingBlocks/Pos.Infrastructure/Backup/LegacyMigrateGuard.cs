using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Infrastructure.Backup;

/// <summary>Cómo terminó un <c>migrate</c> pedido por un actualizador anterior a la 1.2.0 (<see cref="LegacyMigrateGuard"/>).</summary>
public enum LegacyMigrateOutcome
{
    /// <summary>Migró bien.</summary>
    Updated,

    /// <summary>No se migró: falló el backup previo. La BD no se tocó.</summary>
    BackupFailed,

    /// <summary>Falló una migración y se volvió al backup PRE_UPDATE (server.json apunta a la BD restaurada).</summary>
    FailedRestored,

    /// <summary>Falló una migración y NO se pudo volver a los datos de antes: restaurar con soporte.</summary>
    FailedNeedsSupport,

    /// <summary>Esta versión ya falló sobre estos mismos datos: no se vuelve a intentar (no se tocó la BD).</summary>
    AlreadyFailed,
}

/// <summary>Resultado de <see cref="LegacyMigrateGuard.RunAsync"/>.</summary>
public sealed record LegacyMigrateResult(LegacyMigrateOutcome Outcome, string? BackupFile, Exception? Error, string? RestoredDatabase)
{
    /// <summary>Código de salida del migrador para este resultado.</summary>
    public int ExitCode => Outcome switch
    {
        LegacyMigrateOutcome.Updated => 0,
        LegacyMigrateOutcome.BackupFailed => LegacyMigrateGuard.ExitBackupFailed,
        LegacyMigrateOutcome.FailedRestored => LegacyMigrateGuard.ExitFailedRestored,
        LegacyMigrateOutcome.AlreadyFailed => LegacyMigrateGuard.ExitAlreadyFailed,
        _ => LegacyMigrateGuard.ExitNeedsSupport,
    };
}

/// <summary>Lo que queda en <c>updates\legacy-migrate-failed.json</c> cuando falla un <c>migrate</c> del actualizador anterior.</summary>
/// <param name="Version">Versión del migrador que falló (la que se intentaba instalar).</param>
/// <param name="FromSchemaVersion">Versión del esquema de la BD ANTES de intentar migrar (la que queda si se restauró).</param>
/// <param name="NeedsSupport">No se pudo volver a los datos de antes.</param>
/// <param name="At">Cuándo falló.</param>
/// <param name="Error">Mensaje de la falla (para el paquete de soporte).</param>
public sealed record LegacyMigrateFailure(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("fromSchemaVersion")] string? FromSchemaVersion,
    [property: JsonPropertyName("needsSupport")] bool NeedsSupport,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>
/// Integración 1.2.0 (ALTA 1). El primer salto de 1.0.x/1.1.0 a 1.2.0 por actualización automática lo hace el actualizador VIEJO: hace el
/// backup PRE_UPDATE con el migrador anterior (antes de detener el servidor), detiene los servicios, llama al migrador nuevo con
/// <c>migrate --no-backup --data-root D</c> y, si falla, vuelve a arrancar el servidor anterior SIN restaurar nada (cree que las migraciones
/// van en una sola transacción). Como cada script va en la suya, la BD quedaba a medio migrar y la tienda en SCHEMA_OUTDATED.
/// <para>
/// Esa llamada se reconoce porque no hay registro en curso del actualizador nuevo (<c>updates\update-in-progress.json</c>, que existe
/// durante todo su <c>migrate</c>). En ese caso el migrador nuevo se protege solo: hace su PROPIO backup PRE_UPDATE (con los servicios ya
/// detenidos, así es exacto), migra con <see cref="GuardedSchemaUpdate"/> y, si falla, restaura ese backup con
/// <see cref="PreUpdateRollback"/> y cambia server.json a la BD restaurada; el servidor anterior vuelve a arrancar sobre una BD sana.
/// Deja una marca para no volver a intentar cada noche la misma versión sobre los mismos datos (<see cref="LegacyMigrateOutcome.AlreadyFailed"/>).
/// </para>
/// </summary>
public static class LegacyMigrateGuard
{
    /// <summary>No se migró: falló el backup previo obligatorio (el mismo código que <c>migrate</c> ya usaba).</summary>
    public const int ExitBackupFailed = 6;

    /// <summary>Falló la migración y no se pudo volver a los datos de antes (el mismo código que <c>install</c>).</summary>
    public const int ExitNeedsSupport = 8;

    /// <summary>Falló la migración y se restauró el backup PRE_UPDATE: la versión anterior puede volver a arrancar.</summary>
    public const int ExitFailedRestored = 9;

    /// <summary>Esta versión ya falló sobre estos datos: no se intentó de nuevo y la BD no se tocó.</summary>
    public const int ExitAlreadyFailed = 10;

    /// <summary>Registro en curso del actualizador 1.2.0 o posterior (en <c>{DataRoot}\updates</c>).</summary>
    public const string UpdaterJournal = "update-in-progress.json";

    /// <summary>Marca de la falla (en <c>{DataRoot}\updates</c>).</summary>
    public const string FailureFile = "legacy-migrate-failed.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// ¿Es la llamada de un actualizador anterior a la 1.2.0? Exactamente <c>migrate --no-backup --data-root D</c> sobre una instalación
    /// (<c>config\server.json</c>), sin <c>--connection</c> (desarrollo y pruebas) ni <c>--no-guard</c> (soporte), y sin el registro en curso
    /// del actualizador nuevo (que hace él mismo el backup y la vuelta atrás).
    /// </summary>
    public static bool IsLegacyInvocation(IReadOnlyDictionary<string, string> options, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ContainsKey("no-backup")
            && options.ContainsKey("data-root")
            && !options.ContainsKey("connection")
            && !options.ContainsKey("no-guard")
            && File.Exists(Path.Combine(dataRoot, "config", "server.json"))
            && !File.Exists(Path.Combine(dataRoot, "updates", UpdaterJournal));
    }

    /// <summary>La marca de la falla anterior, o <c>null</c> si no hay (o no se puede leer: entonces se vuelve a intentar).</summary>
    public static LegacyMigrateFailure? ReadFailure(string dataRoot)
    {
        var path = FailurePath(dataRoot);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<LegacyMigrateFailure>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// ¿Ya falló esta versión sobre estos datos? Si se restauró, la BD volvió a <see cref="LegacyMigrateFailure.FromSchemaVersion"/>; si
    /// quedó "requiere soporte", no se reintenta hasta que soporte lo resuelva (instalador, <c>--retry-failed</c> o borrar la marca).
    /// </summary>
    public static bool AlreadyFailed(LegacyMigrateFailure? failure, string appVersion, string? databaseVersion) =>
        failure is not null
        && string.Equals(failure.Version, appVersion, StringComparison.Ordinal)
        && (failure.NeedsSupport || string.Equals(failure.FromSchemaVersion, databaseVersion, StringComparison.Ordinal));

    /// <summary>
    /// Migra protegido. Sin migraciones versionadas pendientes no hay nada que deshacer: solo migra (como antes). Con cambios de esquema:
    /// marca previa → backup → <see cref="GuardedSchemaUpdate"/> → marca.
    /// </summary>
    /// <param name="dataRoot">Carpeta de datos (la marca va en <c>updates</c>).</param>
    /// <param name="appVersion">Versión de este migrador.</param>
    /// <param name="databaseVersion">Versión del esquema de la BD activa antes de migrar.</param>
    /// <param name="schemaChangesPending">Si hay migraciones versionadas pendientes.</param>
    /// <param name="retryFailed">Ignorar la marca (soporte).</param>
    /// <param name="backup">Hace y verifica el backup PRE_UPDATE; devuelve el archivo.</param>
    /// <param name="migrate">Aplica las migraciones.</param>
    /// <param name="restore">Restaura el backup y devuelve la BD que queda activa.</param>
    /// <param name="time">Reloj.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public static async Task<LegacyMigrateResult> RunAsync(
        string dataRoot,
        string appVersion,
        string? databaseVersion,
        bool schemaChangesPending,
        bool retryFailed,
        Func<CancellationToken, Task<string>> backup,
        Func<CancellationToken, Task> migrate,
        Func<string, CancellationToken, Task<string>> restore,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentNullException.ThrowIfNull(time);
        if (!schemaChangesPending)
        {
            await migrate(cancellationToken);
            return new LegacyMigrateResult(LegacyMigrateOutcome.Updated, null, null, null);
        }

        if (!retryFailed && AlreadyFailed(ReadFailure(dataRoot), appVersion, databaseVersion))
        {
            return new LegacyMigrateResult(LegacyMigrateOutcome.AlreadyFailed, null, null, null);
        }

        string file;
        try
        {
            file = await backup(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LegacyMigrateResult(LegacyMigrateOutcome.BackupFailed, null, ex, null);
        }

        var update = await GuardedSchemaUpdate.RunAsync(migrate, file, schemaChangesPending: true, restore, cancellationToken);
        switch (update.Outcome)
        {
            case GuardedUpdateOutcome.Updated:
                ClearFailure(dataRoot);
                return new LegacyMigrateResult(LegacyMigrateOutcome.Updated, file, null, null);
            case GuardedUpdateOutcome.FailedRestored:
                SaveFailure(dataRoot, new LegacyMigrateFailure(appVersion, databaseVersion, false, time.GetUtcNow(), update.MigrationError?.Message));
                return new LegacyMigrateResult(LegacyMigrateOutcome.FailedRestored, file, update.MigrationError, update.RestoredDatabase);
            default:
                SaveFailure(dataRoot, new LegacyMigrateFailure(
                    appVersion, databaseVersion, true, time.GetUtcNow(), (update.RestoreError ?? update.MigrationError)?.Message));
                return new LegacyMigrateResult(LegacyMigrateOutcome.FailedNeedsSupport, file, update.RestoreError ?? update.MigrationError, null);
        }
    }

    /// <summary>Borra la marca (una migración que terminó bien).</summary>
    public static void ClearFailure(string dataRoot)
    {
        var path = FailurePath(dataRoot);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"No se pudo borrar {path}: {ex.Message}");
        }
    }

    private static void SaveFailure(string dataRoot, LegacyMigrateFailure failure)
    {
        var path = FailurePath(dataRoot);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(failure, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin la marca solo se pierde la protección contra el reintento de la noche siguiente; la vuelta atrás ya se hizo.
            Console.Error.WriteLine($"No se pudo guardar {path}: {ex.Message}");
        }
    }

    private static string FailurePath(string dataRoot) => Path.Combine(dataRoot, "updates", FailureFile);
}
