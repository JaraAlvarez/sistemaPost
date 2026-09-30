namespace Pos.Server.Host.Database;

/// <summary>Sección <c>Pos:Database</c>.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Pos:Database";

    /// <summary>Cadena del rol <c>pos_app</c>. Puede venir protegida con DPAPI (<c>dpapi:BASE64</c>, la escribe el instalador).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Cadena del rol <c>pos_backup</c> (lectura total) para los backups (Fase 11). Puede venir protegida con DPAPI.</summary>
    public string? BackupConnectionString { get; set; }

    /// <summary>Cadena del rol <c>pos_migrator</c>; solo se usa si <see cref="MigrateOnStartup"/> está activo.</summary>
    public string? MigratorConnectionString { get; set; }

    /// <summary>
    /// Migrar al arrancar. SOLO en desarrollo: en producción migra el actualizador, después de un backup obligatorio
    /// (docs/fases/fase-02-propuesta.md §7, regla 7).
    /// </summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Edición instalada: <c>SINGLE</c> (Caja Única) o <c>MULTI</c> (Multicaja). La escribe el instalador.</summary>
    public string Edition { get; set; } = "SINGLE";
}
