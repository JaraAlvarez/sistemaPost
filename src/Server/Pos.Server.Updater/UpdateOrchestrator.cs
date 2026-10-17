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
    /// <summary>Versión activa (la carpeta a la que apunta <c>app\current</c>).</summary>
    string CurrentVersion { get; }

    /// <summary>¿Está preparada esa versión, con su contenido verificado (U1)?</summary>
    bool IsStaged(string version);

    /// <summary>
    /// Verifica el paquete contra el manifiesto firmado con un handle exclusivo y lo descomprime en <c>app\{versión}</c> (a una carpeta
    /// temporal y luego la renombra).
    /// </summary>
    Task StageAsync(string version, string packagePath, UpdateManifest manifest, CancellationToken cancellationToken);

    /// <summary>Jornadas de caja abiertas (el servidor no se actualiza con cajas vendiendo, salvo "instalar ahora").</summary>
    Task<int> OpenCashSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Backup PRE_UPDATE con el migrador de la versión ACTUAL. Devuelve la ruta del paquete.</summary>
    Task<string> BackupAsync(string version, CancellationToken cancellationToken);

    /// <summary>Migraciones con el migrador de la versión NUEVA (en una transacción: o se aplican todas o ninguna).</summary>
    Task<bool> MigrateAsync(string version, CancellationToken cancellationToken);

    /// <summary>Restaura el backup con el migrador de la versión indicada (vuelta atrás después de migrar).</summary>
    Task<bool> RestoreAsync(string version, string backupPath, CancellationToken cancellationToken);

    Task StopServicesAsync(CancellationToken cancellationToken);

    Task StartServicesAsync(CancellationToken cancellationToken);

    /// <summary>Apunta <c>app\current</c> a la versión (cambio atómico del enlace).</summary>
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
}

/// <summary>Pasos de una actualización en curso (U4), en el orden en que ocurren.</summary>
public enum UpdateStep
{
    Started,
    BackedUp,
    Stopped,
    Migrated,
    Activated,
}

/// <summary>U4: registro persistente de la actualización en curso (<c>{DataRoot}\updates\update-in-progress.json</c>, carpeta con ACL).</summary>
public sealed record UpdateJournal(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("step")] UpdateStep Step,
    [property: JsonPropertyName("backup")] string? Backup,
    [property: JsonPropertyName("at")] DateTimeOffset At);

/// <summary>
/// Aplica una versión ya descargada y verificada (docs/fases/fase-13-propuesta.md §5 flujo 5): ventana segura → backup → detener →
/// migrar → cambiar de versión → arrancar → salud. Si falla, vuelve atrás: versión anterior y, si la migración alcanzó a correr, la BD
/// del backup previo. Todo queda en el historial que el servidor audita al arrancar.
/// U4: cada paso queda en un registro persistente; si el equipo se apaga a mitad, al arrancar <see cref="RecoverAsync"/> vuelve a la versión
/// anterior (y a la BD del backup previo si ya se había migrado). Mientras tanto el servidor no atiende con un esquema que no es el suyo
/// (SCHEMA_OUTDATED).
/// </summary>
public sealed class UpdateOrchestrator(IUpdateHost host, UpdaterMode mode, string dataRoot, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private string JournalPath => Path.Combine(UpdateFiles.Directory(dataRoot), UpdateFiles.InProgress);

    public async Task<UpdateResult> ApplyAsync(UpdateManifest manifest, string packagePath, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var from = host.CurrentVersion;
        var to = manifest.Version;
        if (!host.IsStaged(to))
        {
            await host.StageAsync(to, packagePath, manifest, cancellationToken);
        }

        if (mode == UpdaterMode.Terminal)
        {
            Journal(from, to, UpdateStep.Started, null);
            return await SwitchAsync(from, to, backup: null, cancellationToken);
        }

        if (!force && await host.OpenCashSessionsAsync(cancellationToken) > 0)
        {
            return UpdateResult.Deferred;
        }

        Journal(from, to, UpdateStep.Started, null);
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

        Journal(from, to, UpdateStep.BackedUp, backup);
        if (!await TryStopAsync(from, to, cancellationToken))
        {
            return UpdateResult.FailedNoChanges;
        }

        Journal(from, to, UpdateStep.Stopped, backup);
        if (!await host.MigrateAsync(to, cancellationToken))
        {
            // Cada script va en su transacción: los anteriores pueden haber quedado aplicados. Se vuelve a la BD del backup previo (IN4).
            var restored = await host.RestoreAsync(from, backup, cancellationToken);
            await host.StartServicesAsync(cancellationToken);
            Record(UpdateOutcomes.Failed, from, to, "Falló la migración de la base de datos; sigue la versión anterior.");
            if (restored)
            {
                Record(UpdateOutcomes.RolledBack, from, to, $"Se volvió a la base de datos del backup previo ({Path.GetFileName(backup)}).");
            }
            else
            {
                Record(UpdateOutcomes.RolledBack, from, to, $"No se pudo restaurar el backup previo ({backup}): restaurar con soporte.");
            }

            ClearJournal();
            return restored ? UpdateResult.FailedNoChanges : UpdateResult.RollbackFailed;
        }

        Journal(from, to, UpdateStep.Migrated, backup);
        return await SwitchAsync(from, to, backup, cancellationToken);
    }

    /// <summary>
    /// U4: si quedó una actualización a medias (corte de luz, reinicio), vuelve a la versión anterior: servicios detenidos, <c>app\current</c>
    /// a la versión de antes, la BD del backup previo si ya se había migrado y servicios arrancados. <c>null</c> si no había nada pendiente.
    /// </summary>
    public async Task<UpdateResult?> RecoverAsync(CancellationToken cancellationToken)
    {
        if (ReadJournal() is not { } journal)
        {
            return null;
        }

        try
        {
            await host.StopServicesAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Si no se detienen, igual se intenta volver: el cambio de versión no toca archivos en uso.
        }

        if (!string.Equals(host.CurrentVersion, journal.From, StringComparison.Ordinal) && journal.Step >= UpdateStep.Activated)
        {
            host.Activate(journal.From);
        }

        var restored = true;
        if (mode == UpdaterMode.Server && journal.Step >= UpdateStep.Stopped && journal.Backup is { } backup)
        {
            // Desde "detenido" el migrador nuevo pudo haber empezado a migrar: se vuelve al backup previo.
            restored = await host.RestoreAsync(journal.From, backup, cancellationToken);
        }

        await host.StartServicesAsync(cancellationToken);
        var healthy = await host.IsHealthyAsync(cancellationToken);
        Record(UpdateOutcomes.RolledBack, journal.From, journal.To, restored && healthy
            ? $"La actualización a {journal.To} quedó a medias (paso {journal.Step}); al arrancar se volvió a la versión {journal.From}."
            : $"La actualización a {journal.To} quedó a medias (paso {journal.Step}) y no se pudo volver del todo a {journal.From}: llame a soporte.");
        ClearJournal();
        return restored && healthy ? UpdateResult.RolledBack : UpdateResult.RollbackFailed;
    }

    public UpdateJournal? ReadJournal()
    {
        try
        {
            return File.Exists(JournalPath) ? JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(JournalPath), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Dañado: igual hubo una actualización a medias; sin datos no se sabe a qué volver.
            return null;
        }
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

            Journal(from, to, UpdateStep.Stopped, null);
        }

        host.Activate(to);
        Journal(from, to, UpdateStep.Activated, backup);
        await host.StartServicesAsync(cancellationToken);
        if (await host.IsHealthyAsync(cancellationToken))
        {
            Record(UpdateOutcomes.Applied, from, to, $"Actualizado de {from} a {to}.");
            ClearJournal();
            host.CleanupOldVersions(2);
            return UpdateResult.Applied;
        }

        // Vuelta atrás: versión anterior y, si se migró, la BD del backup previo.
        await host.StopServicesAsync(cancellationToken);
        host.Activate(from);
        var restored = backup is null || await host.RestoreAsync(from, backup, cancellationToken);
        await host.StartServicesAsync(cancellationToken);
        var healthy = await host.IsHealthyAsync(cancellationToken);
        Record(UpdateOutcomes.Failed, from, to, $"La versión {to} no arrancó sana.");
        Record(UpdateOutcomes.RolledBack, from, to, restored && healthy
            ? $"Se volvió a la versión {from}{(backup is null ? string.Empty : " y a la base de datos del backup previo")}."
            : $"Se volvió a la versión {from}, pero {(restored ? "el servidor no responde" : "no se pudo restaurar el backup previo")}: llame a soporte.");
        ClearJournal();
        return restored && healthy ? UpdateResult.RolledBack : UpdateResult.RollbackFailed;
    }

    private void Journal(string from, string to, UpdateStep step, string? backup)
    {
        Directory.CreateDirectory(UpdateFiles.Directory(dataRoot));
        File.WriteAllText(JournalPath + ".tmp", JsonSerializer.Serialize(new UpdateJournal(from, to, step, backup, time.GetUtcNow()), Json));
        File.Move(JournalPath + ".tmp", JournalPath, overwrite: true);
    }

    private void ClearJournal()
    {
        if (File.Exists(JournalPath))
        {
            File.Delete(JournalPath);
        }
    }

    private void Record(string outcome, string from, string to, string detail) =>
        UpdateFiles.AppendHistory(dataRoot, new UpdateHistoryEntry(Guid.CreateVersion7(), time.GetUtcNow(), outcome, from, to, detail));
}
