namespace Pos.Server.Migrations;

/// <summary>Fallo del migrador con un código estable (se muestra al instalador y al soporte).</summary>
public sealed class MigrationException : Exception
{
    public const string ChecksumMismatch = "MIGRATION.CHECKSUM_MISMATCH";
    public const string DatabaseNewer = "MIGRATION.DATABASE_NEWER";
    public const string ScriptFailed = "MIGRATION.SCRIPT_FAILED";
    public const string PendingMigrations = "MIGRATION.PENDING";

    public MigrationException()
        : this(ScriptFailed, "Falló la migración.")
    {
    }

    public MigrationException(string message)
        : this(ScriptFailed, message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : this(ScriptFailed, message, innerException)
    {
    }

    public MigrationException(string code, string message, Exception? innerException = null)
        : base($"{code}: {message}", innerException) => Code = code;

    public string Code { get; } = ScriptFailed;
}
