using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Updates.Contracts;

namespace Pos.Server.Updater;

/// <summary>Qué instala este equipo: el servidor (Todo en uno o Servidor Multicaja) o solo el agente (Caja).</summary>
public enum UpdaterMode
{
    Server,
    Terminal,
}

/// <summary>
/// Operaciones del equipo que usa el orquestador (servicios, carpetas de versiones, migrador, salud). La implementación real es
/// <see cref="WindowsUpdateHost"/>; las pruebas usan una simulada.
/// </summary>
public interface IUpdateHost
{
    /// <summary>Versión activa (la carpeta a la que apunta <c>app\current</c>); vacía si no se sabe.</summary>
    string CurrentVersion { get; }

    /// <summary>¿Existe <c>app\{versión}</c> como carpeta real (no un enlace), hija directa de <c>app</c>? La versión debe ser SemVer estricta.</summary>
    bool HasVersion(string version);

    /// <summary>¿Está preparada esa versión, del paquete con esa huella, con su contenido verificado (U1, M3)?</summary>
    bool IsStaged(string version, string packageSha256);

    /// <summary>
    /// Verifica el paquete contra el manifiesto firmado con un handle exclusivo y lo descomprime en <c>app\{versión}</c> (a una carpeta
    /// temporal y luego la renombra).
    /// </summary>
    Task StageAsync(string version, string packagePath, UpdateManifest manifest, CancellationToken cancellationToken);

    /// <summary>Jornadas de caja abiertas (el servidor no se actualiza con cajas vendiendo, salvo "instalar ahora").</summary>
    Task<int> OpenCashSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Backup PRE_UPDATE con el migrador de la versión ACTUAL. Devuelve la ruta del paquete.</summary>
    Task<string> BackupAsync(string version, CancellationToken cancellationToken);

    /// <summary>Migraciones con el migrador de la versión NUEVA (cada script en su transacción: si falla, pueden quedar las anteriores).</summary>
    Task<bool> MigrateAsync(string version, CancellationToken cancellationToken);

    /// <summary>Restaura el backup con el migrador de la versión indicada (vuelta atrás después de migrar).</summary>
    Task<bool> RestoreAsync(string version, string backupPath, CancellationToken cancellationToken);

    Task StopServicesAsync(CancellationToken cancellationToken);

    Task StartServicesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Apunta <c>app\current</c> a la versión (cambio atómico del enlace). Idempotente: si la unión ya apunta ahí no la cambia, y si falta
    /// la crea.
    /// </summary>
    void Activate(string version);

    /// <summary>¿Respondió sano el servicio dentro del tiempo de espera?</summary>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);

    /// <summary>Borra las versiones viejas y deja las ⚙️ 2 más recientes (la activa siempre se conserva).</summary>
    void CleanupOldVersions(int keep);
}

public enum UpdateResult
{
    Applied,
    Deferred,
    FailedNoChanges,
    RolledBack,
    RollbackFailed,

    /// <summary>Hay una actualización a medias que no se pudo deshacer: no se empieza otra (requiere soporte).</summary>
    Pending,
}

/// <summary>Pasos de una actualización en curso (U4), en el orden en que ocurren.</summary>
public enum UpdateStep
{
    Started,
    BackedUp,
    Stopped,
    Migrated,
    Activated,

    /// <summary>La vuelta atrás ya restauró el backup previo: no se vuelve a restaurar.</summary>
    Restored,
}

/// <summary>U4: registro persistente de la actualización en curso (<c>{DataRoot}\updates\update-in-progress.json</c>, carpeta con ACL).</summary>
public sealed record UpdateJournal(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("step")] UpdateStep Step,
    [property: JsonPropertyName("backup")] string? Backup,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("attempts")] int Attempts = 0,
    [property: JsonPropertyName("needsSupport")] bool NeedsSupport = false);

/// <summary>
/// Aplica una versión ya descargada y verificada (docs/fases/fase-13-propuesta.md §5 flujo 5): ventana segura → backup → detener →
/// migrar → cambiar de versión → arrancar → salud. Si falla, vuelve atrás: versión anterior y, si la migración alcanzó a correr, la BD
/// del backup previo. Todo queda en el historial que el servidor audita al arrancar.
/// U4: cada paso queda en un registro persistente; si el equipo se apaga a mitad, al arrancar <see cref="RecoverAsync"/> vuelve a la versión
/// anterior (y a la BD del backup previo si ya se había detenido el servidor), como máximo <see cref="MaxRecoveryAttempts"/> veces; después
/// queda "requiere soporte" y no se empieza otra actualización. El servidor no atiende con un esquema que no es el suyo (SCHEMA_OUTDATED).
/// </summary>
/// <param name="host">El equipo (servicios, versiones, migrador).</param>
/// <param name="mode">Servidor o caja.</param>
/// <param name="dataRoot">Carpeta de datos (historial y registro en curso en <c>updates</c>).</param>
/// <param name="time">Reloj.</param>
/// <param name="isTrustedFile">
/// ¿El archivo es de SYSTEM o de los Administradores? (ALTA 1: un registro o una secuencia que dejó un usuario normal se ignora y se borra).
/// </param>
public sealed class UpdateOrchestrator(IUpdateHost host, UpdaterMode mode, string dataRoot, TimeProvider time, Func<string, bool>? isTrustedFile = null)
{
    public const int MaxRecoveryAttempts = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private string JournalPath => Path.Combine(UpdateFiles.Directory(dataRoot), UpdateFiles.InProgress);

    /// <summary>B2: el registro en curso estaba dañado, no era confiable o nombraba versiones inválidas (se avisa en el estado).</summary>
    public string? JournalProblem { get; private set; }

    public async Task<UpdateResult> ApplyAsync(UpdateManifest manifest, string packagePath, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (ReadJournal() is not null)
        {
            // ALTA 3: con una actualización a medias pendiente no se empieza otra.
            return UpdateResult.Pending;
        }

        var from = host.CurrentVersion;
        var to = manifest.Version;
        if (!SemanticVersion.IsSafeFolderName(from) || !SemanticVersion.IsSafeFolderName(to))
        {
            Record(UpdateOutcomes.Failed, from, to, "No se actualizó: la versión instalada o la nueva no es válida.");
            return UpdateResult.FailedNoChanges;
        }

        if (!host.IsStaged(to, manifest.PackageSha256))
        {
            await host.StageAsync(to, packagePath, manifest, cancellationToken);
        }

        if (mode == UpdaterMode.Terminal)
        {
            Journal(new UpdateJournal(from, to, UpdateStep.Started, null, time.GetUtcNow()));
            return await SwitchAsync(from, to, backup: null, cancellationToken);
        }

        if (!force && await host.OpenCashSessionsAsync(cancellationToken) > 0)
        {
            return UpdateResult.Deferred;
        }

        Journal(new UpdateJournal(from, to, UpdateStep.Started, null, time.GetUtcNow()));
        string backup;
        try
        {
            backup = await host.BackupAsync(from, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(UpdateOutcomes.Failed, from, to, $"No se actualizó: falló el backup previo obligatorio ({ex.Message}).");
            ClearJournal();
            return UpdateResult.FailedNoChanges;
        }

        Journal(new UpdateJournal(from, to, UpdateStep.BackedUp, backup, time.GetUtcNow()));
        if (!await TryStopAsync(from, to, cancellationToken))
        {
            return UpdateResult.FailedNoChanges;
        }

        Journal(new UpdateJournal(from, to, UpdateStep.Stopped, backup, time.GetUtcNow()));
        if (!await host.MigrateAsync(to, cancellationToken))
        {
            // Cada script va en su transacción: los anteriores pueden haber quedado aplicados. Se vuelve a la BD del backup previo (IN4).
            var restored = await RestoreOnceAsync(new UpdateJournal(from, to, UpdateStep.Stopped, backup, time.GetUtcNow()), cancellationToken);
            await host.StartServicesAsync(cancellationToken);
            Record(UpdateOutcomes.Failed, from, to, "Falló la migración de la base de datos; sigue la versión anterior.");
            Record(UpdateOutcomes.RolledBack, from, to, restored
                ? $"Se volvió a la base de datos del backup previo ({Path.GetFileName(backup)})."
                : $"No se pudo restaurar el backup previo ({backup}): restaurar con soporte.");
            ClearJournal();
            return restored ? UpdateResult.FailedNoChanges : UpdateResult.RollbackFailed;
        }

        Journal(new UpdateJournal(from, to, UpdateStep.Migrated, backup, time.GetUtcNow()));
        return await SwitchAsync(from, to, backup, cancellationToken);
    }

    /// <summary>
    /// U4: si quedó una actualización a medias (corte de luz, reinicio), vuelve a la versión anterior: servicios detenidos, <c>app\current</c>
    /// a la versión de antes (siempre desde "detenido": Activate es idempotente y recrea la unión si falta), la BD del backup previo una sola
    /// vez (paso Restored) y servicios arrancados siempre (finally). Tras <see cref="MaxRecoveryAttempts"/> intentos fallidos queda
    /// "requiere soporte": no se toca la BD, se arrancan los servicios y no se empieza otra actualización. <c>null</c> si no había nada.
    /// </summary>
    public async Task<UpdateResult?> RecoverAsync(CancellationToken cancellationToken)
    {
        if (ReadJournal() is not { } journal)
        {
            return null;
        }

        if (journal.NeedsSupport || journal.Attempts >= MaxRecoveryAttempts)
        {
            if (!journal.NeedsSupport)
            {
                Journal(journal with { NeedsSupport = true });
                Record(UpdateOutcomes.RolledBack, journal.From, journal.To,
                    $"La actualización a {journal.To} quedó a medias y no se pudo deshacer en {MaxRecoveryAttempts} intentos: requiere soporte. " +
                    "No se tocó la base de datos; los servicios se arrancan igual.");
            }

            await host.StartServicesAsync(cancellationToken);
            return UpdateResult.Pending;
        }

        journal = journal with { Attempts = journal.Attempts + 1 };
        Journal(journal);
        var restored = true;
        var activated = true;
        try
        {
            try
            {
                await host.StopServicesAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Si no se detienen, igual se intenta volver: el cambio de versión no toca archivos en uso.
            }

            if (journal.Step >= UpdateStep.Stopped)
            {
                // ALTA 2: según el destino real de la unión (Activate es idempotente y la recrea si falta), no según current.json.
                try
                {
                    host.Activate(journal.From);
                }
                catch (InvalidOperationException)
                {
                    activated = false;
                }
            }

            if (mode == UpdaterMode.Server && journal.Step >= UpdateStep.Stopped && journal.Backup is not null)
            {
                restored = await RestoreOnceAsync(journal, cancellationToken);
            }
        }
        finally
        {
            await host.StartServicesAsync(cancellationToken);
        }

        var healthy = activated && await host.IsHealthyAsync(cancellationToken);
        if (restored && healthy)
        {
            Record(UpdateOutcomes.RolledBack, journal.From, journal.To,
                $"La actualización a {journal.To} quedó a medias (paso {journal.Step}); al arrancar se volvió a la versión {journal.From}.");
            ClearJournal();
            return UpdateResult.RolledBack;
        }

        // Queda el registro (con el intento contado) para el próximo arranque.
        Record(UpdateOutcomes.Failed, journal.From, journal.To,
            $"Intento {journal.Attempts} de {MaxRecoveryAttempts} de deshacer la actualización a {journal.To}: " +
            (restored ? activated ? "el servicio no responde." : "no se pudo volver a la versión anterior." : "no se pudo restaurar el backup previo."));
        return UpdateResult.RollbackFailed;
    }

    /// <summary>
    /// El registro en curso, si es válido: dueño SYSTEM o Administradores (si no, se borra), JSON legible y versiones SemVer estrictas con su
    /// carpeta real en <c>app</c>. Si no, <see cref="JournalProblem"/> lo explica y se devuelve <c>null</c>.
    /// </summary>
    public UpdateJournal? ReadJournal()
    {
        if (!File.Exists(JournalPath))
        {
            return null;
        }

        if (isTrustedFile is not null && !isTrustedFile(JournalPath))
        {
            JournalProblem = "Se ignoró un registro de actualización que no era de SYSTEM ni de los Administradores.";
            TryDelete(JournalPath);
            return null;
        }

        UpdateJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(JournalPath), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            JournalProblem = $"El registro de la actualización en curso está dañado ({ex.Message}); se archivó para revisión.";
            Archive();
            return null;
        }

        if (journal is null || !SemanticVersion.IsSafeFolderName(journal.From) || !SemanticVersion.IsSafeFolderName(journal.To)
            || !host.HasVersion(journal.From) || !host.HasVersion(journal.To))
        {
            JournalProblem = "El registro de la actualización en curso nombra versiones inválidas o inexistentes; se archivó para revisión.";
            Archive();
            return null;
        }

        return journal;
    }

    private async Task<bool> RestoreOnceAsync(UpdateJournal journal, CancellationToken cancellationToken)
    {
        if (journal.Step == UpdateStep.Restored || journal.Backup is null)
        {
            return true;
        }

        if (!await host.RestoreAsync(journal.From, journal.Backup, cancellationToken))
        {
            return false;
        }

        if (File.Exists(JournalPath))
        {
            Journal(journal with { Step = UpdateStep.Restored, At = time.GetUtcNow() });
        }

        return true;
    }

    private async Task<bool> TryStopAsync(string from, string to, CancellationToken cancellationToken)
    {
        try
        {
            await host.StopServicesAsync(cancellationToken);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // Bajas: si no se detienen los servicios, no se sigue; el actualizador reintenta más tarde con espera creciente.
            await host.StartServicesAsync(cancellationToken);
            Record(UpdateOutcomes.Failed, from, to, $"No se actualizó: no se pudieron detener los servicios ({ex.Message}).");
            ClearJournal();
            return false;
        }
    }

    private async Task<UpdateResult> SwitchAsync(string from, string to, string? backup, CancellationToken cancellationToken)
    {
        if (mode == UpdaterMode.Terminal)
        {
            if (!await TryStopAsync(from, to, cancellationToken))
            {
                return UpdateResult.FailedNoChanges;
            }

            Journal(new UpdateJournal(from, to, UpdateStep.Stopped, null, time.GetUtcNow()));
        }

        var activatedNew = true;
        try
        {
            host.Activate(to);
            Journal(new UpdateJournal(from, to, UpdateStep.Activated, backup, time.GetUtcNow()));
            await host.StartServicesAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // M1: no se pudo activar la versión nueva: vuelta atrás igual que si no hubiera arrancado sana.
            activatedNew = false;
        }

        if (activatedNew && await host.IsHealthyAsync(cancellationToken))
        {
            Record(UpdateOutcomes.Applied, from, to, $"Actualizado de {from} a {to}.");
            ClearJournal();
            host.CleanupOldVersions(2);
            return UpdateResult.Applied;
        }

        // Vuelta atrás: versión anterior y, si se migró, la BD del backup previo. Los servicios se arrancan pase lo que pase (M1).
        var restored = true;
        var backToPrevious = true;
        try
        {
            try
            {
                await host.StopServicesAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Se intenta volver igual.
            }

            try
            {
                host.Activate(from);
            }
            catch (InvalidOperationException)
            {
                backToPrevious = false;
            }

            if (backup is not null)
            {
                restored = await RestoreOnceAsync(new UpdateJournal(from, to, UpdateStep.Activated, backup, time.GetUtcNow()), cancellationToken);
            }
        }
        finally
        {
            await host.StartServicesAsync(cancellationToken);
        }

        var healthy = backToPrevious && await host.IsHealthyAsync(cancellationToken);
        Record(UpdateOutcomes.Failed, from, to, activatedNew ? $"La versión {to} no arrancó sana." : $"No se pudo activar la versión {to}.");
        Record(UpdateOutcomes.RolledBack, from, to, restored && healthy
            ? $"Se volvió a la versión {from}{(backup is null ? string.Empty : " y a la base de datos del backup previo")}."
            : $"Se volvió a la versión {from}, pero {(!backToPrevious ? "no se pudo activar" : restored ? "el servidor no responde" : "no se pudo restaurar el backup previo")}: llame a soporte.");
        ClearJournal();
        return restored && healthy ? UpdateResult.RolledBack : UpdateResult.RollbackFailed;
    }

    private void Journal(UpdateJournal journal)
    {
        Directory.CreateDirectory(UpdateFiles.Directory(dataRoot));
        File.WriteAllText(JournalPath + ".tmp", JsonSerializer.Serialize(journal, Json));
        File.Move(JournalPath + ".tmp", JournalPath, overwrite: true);
    }

    private void ClearJournal() => TryDelete(JournalPath);

    private void Archive()
    {
        try
        {
            File.Move(JournalPath, $"{JournalPath}.{time.GetUtcNow():yyyyMMddHHmmss}.archivado", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(JournalPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mejor esfuerzo.
        }
    }

    private void Record(string outcome, string from, string to, string detail) =>
        UpdateFiles.AppendHistory(dataRoot, new UpdateHistoryEntry(Guid.CreateVersion7(), time.GetUtcNow(), outcome, from, to, detail));
}
