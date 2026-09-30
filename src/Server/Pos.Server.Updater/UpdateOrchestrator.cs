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

    /// <summary>¿Existe ya la carpeta completa de esa versión?</summary>
    bool IsStaged(string version);

    /// <summary>Descomprime el paquete en <c>app\{versión}</c> (a una carpeta temporal y luego la renombra).</summary>
    Task StageAsync(string version, string packagePath, CancellationToken cancellationToken);

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

/// <summary>
/// Aplica una versión ya descargada y verificada (docs/fases/fase-13-propuesta.md §5 flujo 5): ventana segura → backup → detener →
/// migrar → cambiar de versión → arrancar → salud. Si falla, vuelve atrás: versión anterior y, si la migración alcanzó a correr, la BD
/// del backup previo. Todo queda en el historial que el servidor audita al arrancar.
/// </summary>
public sealed class UpdateOrchestrator(IUpdateHost host, UpdaterMode mode, string dataRoot, TimeProvider time)
{
    public async Task<UpdateResult> ApplyAsync(UpdateManifest manifest, string packagePath, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var from = host.CurrentVersion;
        var to = manifest.Version;
        if (!host.IsStaged(to))
        {
            await host.StageAsync(to, packagePath, cancellationToken);
        }

        if (mode == UpdaterMode.Terminal)
        {
            return await SwitchAsync(from, to, backup: null, cancellationToken);
        }

        if (!force && await host.OpenCashSessionsAsync(cancellationToken) > 0)
        {
            return UpdateResult.Deferred;
        }

        string backup;
        try
        {
            backup = await host.BackupAsync(from, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(UpdateOutcomes.Failed, from, to, $"No se actualizó: falló el backup previo obligatorio ({ex.Message}).");
            return UpdateResult.FailedNoChanges;
        }

        await host.StopServicesAsync(cancellationToken);
        if (!await host.MigrateAsync(to, cancellationToken))
        {
            // Las migraciones corren en una transacción: la BD quedó como estaba.
            await host.StartServicesAsync(cancellationToken);
            Record(UpdateOutcomes.Failed, from, to, "Falló la migración de la base de datos; no se aplicó ningún cambio y sigue la versión anterior.");
            return UpdateResult.FailedNoChanges;
        }

        return await SwitchAsync(from, to, backup, cancellationToken);
    }

    private async Task<UpdateResult> SwitchAsync(string from, string to, string? backup, CancellationToken cancellationToken)
    {
        if (mode == UpdaterMode.Terminal)
        {
            await host.StopServicesAsync(cancellationToken);
        }

        host.Activate(to);
        await host.StartServicesAsync(cancellationToken);
        if (await host.IsHealthyAsync(cancellationToken))
        {
            Record(UpdateOutcomes.Applied, from, to, $"Actualizado de {from} a {to}.");
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
        return restored && healthy ? UpdateResult.RolledBack : UpdateResult.RollbackFailed;
    }

    private void Record(string outcome, string from, string to, string detail) =>
        UpdateFiles.AppendHistory(dataRoot, new UpdateHistoryEntry(Guid.CreateVersion7(), time.GetUtcNow(), outcome, from, to, detail));
}
