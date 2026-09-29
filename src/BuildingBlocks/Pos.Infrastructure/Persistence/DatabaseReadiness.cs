namespace Pos.Infrastructure.Persistence;

/// <summary>Estado de la base de datos para el servidor.</summary>
public enum DatabaseStatus
{
    /// <summary>Aún no se ha comprobado (arranque).</summary>
    Starting,

    /// <summary>Conectada y con la versión de esquema esperada.</summary>
    Ready,

    /// <summary>La BD tiene una versión de esquema distinta de la que espera la aplicación (falta migrar).</summary>
    SchemaOutdated,

    /// <summary>No se pudo conectar.</summary>
    Unavailable,
}

/// <summary>
/// Estado compartido de la BD. El host lo actualiza al arrancar (y reintenta si falla); los procesos en segundo plano
/// y los endpoints de negocio no trabajan mientras no esté <see cref="DatabaseStatus.Ready"/>.
/// </summary>
public sealed class DatabaseReadiness
{
    private volatile State _state = new(DatabaseStatus.Starting, "Comprobando la base de datos…", null, null);

    public DatabaseStatus Status => _state.Status;

    public bool IsReady => _state.Status == DatabaseStatus.Ready;

    public string Detail => _state.Detail;

    public string? SchemaVersion => _state.SchemaVersion;

    public string? ExpectedVersion => _state.ExpectedVersion;

    public void Set(DatabaseStatus status, string detail, string? schemaVersion = null, string? expectedVersion = null) =>
        _state = new State(status, detail, schemaVersion, expectedVersion);

    private sealed record State(DatabaseStatus Status, string Detail, string? SchemaVersion, string? ExpectedVersion);
}
