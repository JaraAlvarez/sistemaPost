namespace Pos.Infrastructure.Backup;

/// <summary>Cómo terminó una actualización del esquema protegida con el backup PRE_UPDATE (IN4).</summary>
public enum GuardedUpdateOutcome
{
    /// <summary>Las migraciones se aplicaron: la versión nueva queda activa.</summary>
    Updated,

    /// <summary>
    /// Una migración falló pero los datos quedaron como antes de actualizar (se restauró el backup PRE_UPDATE, o no había cambios de
    /// esquema pendientes que deshacer): la versión anterior de los programas puede volver a arrancar.
    /// </summary>
    FailedRestored,

    /// <summary>
    /// Una migración falló y NO se pudo volver a los datos de antes (la restauración falló o no había backup): la versión anterior NO debe
    /// arrancar sobre un esquema a medias. Hay que restaurar con soporte.
    /// </summary>
    FailedNeedsSupport,
}

/// <summary>Resultado de <see cref="GuardedSchemaUpdate.RunAsync"/>.</summary>
public sealed record GuardedUpdateResult(
    GuardedUpdateOutcome Outcome,
    string? BackupFile,
    Exception? MigrationError,
    Exception? RestoreError,
    string? RestoredDatabase);

/// <summary>
/// Actualización del esquema con vuelta atrás automática (IN4): cada script de migración va en su propia transacción, así que si falla uno
/// a mitad quedan aplicados los anteriores y la versión vieja de los programas no sirve con ese esquema. Si hay un backup PRE_UPDATE
/// (hecho con el servidor detenido, sin escrituras desde entonces) se restaura en una BD nueva y la configuración pasa a usarla; la BD a
/// medio migrar queda para revisión. Lógica pura: los pasos reales (migrar y restaurar) los pasa quien llama.
/// </summary>
public static class GuardedSchemaUpdate
{
    /// <param name="migrate">Aplica las migraciones pendientes.</param>
    /// <param name="preUpdateBackup">Backup PRE_UPDATE verificado; <c>null</c> si no se hizo.</param>
    /// <param name="schemaChangesPending">Si había migraciones versionadas pendientes (cambios de esquema que habría que deshacer).</param>
    /// <param name="restore">Restaura el backup y devuelve el nombre de la BD que queda activa.</param>
    /// <param name="cancellationToken">Cancela la migración (la vuelta atrás, una vez empezada, no se cancela).</param>
    public static async Task<GuardedUpdateResult> RunAsync(
        Func<CancellationToken, Task> migrate,
        string? preUpdateBackup,
        bool schemaChangesPending,
        Func<string, CancellationToken, Task<string>> restore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentNullException.ThrowIfNull(restore);
        try
        {
            await migrate(cancellationToken);
            return new GuardedUpdateResult(GuardedUpdateOutcome.Updated, preUpdateBackup, null, null, null);
        }
        catch (Exception migrationError) when (migrationError is not OperationCanceledException)
        {
            if (preUpdateBackup is null)
            {
                // Sin cambios de esquema pendientes (solo scripts repetibles, cada uno en su transacción) no hay nada que deshacer.
                return new GuardedUpdateResult(
                    schemaChangesPending ? GuardedUpdateOutcome.FailedNeedsSupport : GuardedUpdateOutcome.FailedRestored, null, migrationError, null, null);
            }

            try
            {
                // La vuelta atrás no se cancela a mitad: dejaría la configuración apuntando a una BD incompleta.
                var database = await restore(preUpdateBackup, CancellationToken.None);
                return new GuardedUpdateResult(GuardedUpdateOutcome.FailedRestored, preUpdateBackup, migrationError, null, database);
            }
            catch (Exception restoreError)
            {
                return new GuardedUpdateResult(GuardedUpdateOutcome.FailedNeedsSupport, preUpdateBackup, migrationError, restoreError, null);
            }
        }
    }
}
