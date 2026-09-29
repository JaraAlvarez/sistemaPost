using System.Text.RegularExpressions;
using Npgsql;

namespace Pos.Server.Migrations;

/// <summary>Contraseñas de los roles de inicio de sesión (las genera el instalador y se guardan protegidas con DPAPI).</summary>
public sealed record DatabaseRolePasswords(string Migrator, string App, string Backup);

/// <summary>
/// Crea la base de datos del producto y sus roles (comando <c>create-database</c>; lo ejecuta el instalador
/// con el superusuario del clúster local). Idempotente: si los roles o la BD existen, solo actualiza contraseñas
/// y parámetros.
/// Roles (docs/fases/fase-02-propuesta.md §6):
///   pos_owner    (sin login) dueño de todos los objetos;
///   pos_migrator (login) miembro de pos_owner, ejecuta migraciones;
///   pos_app      (login) servidor POS, sin DDL; transaction_timeout = 30 s (horizonte seguro del sellado);
///   pos_backup   (login) lectura total para backups.
/// </summary>
public static partial class DatabaseCreator
{
    public const string MigratorRole = "pos_migrator";
    public const string AppRole = "pos_app";
    public const string BackupRole = "pos_backup";

    /// <summary>Duración máxima de una transacción de la aplicación (ver revisión arquitectónica §4.2).</summary>
    public const string AppTransactionTimeout = "30s";

    public static async Task CreateAsync(
        string superuserConnectionString, string databaseName, DatabaseRolePasswords passwords, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(superuserConnectionString);
        ArgumentNullException.ThrowIfNull(passwords);
        if (!IdentifierPattern().IsMatch(databaseName ?? string.Empty))
        {
            throw new ArgumentException("Nombre de base de datos inválido (solo minúsculas, dígitos y _).", nameof(databaseName));
        }

        await using (var admin = new NpgsqlConnection(superuserConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await EnsureRoleAsync(admin, DatabaseMigrator.OwnerRole, login: false, password: null, cancellationToken);
            await EnsureRoleAsync(admin, MigratorRole, login: true, passwords.Migrator, cancellationToken);
            await EnsureRoleAsync(admin, AppRole, login: true, passwords.App, cancellationToken);
            await EnsureRoleAsync(admin, BackupRole, login: true, passwords.Backup, cancellationToken);

            await ExecuteAsync(admin, $"GRANT {DatabaseMigrator.OwnerRole} TO {MigratorRole}", cancellationToken);
            await ExecuteAsync(admin, $"GRANT pg_read_all_data TO {BackupRole}", cancellationToken);

            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", admin);
            exists.Parameters.AddWithValue("name", databaseName!);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                // Colación builtin C.UTF-8: estable ante actualizaciones de ICU y de Windows (decisión D-05).
                await ExecuteAsync(
                    admin,
                    $"CREATE DATABASE {databaseName} OWNER {DatabaseMigrator.OwnerRole} ENCODING 'UTF8' " +
                    "LOCALE_PROVIDER builtin BUILTIN_LOCALE 'C.UTF-8' TEMPLATE template0",
                    cancellationToken);
            }

            await ExecuteAsync(admin, $"REVOKE ALL ON DATABASE {databaseName} FROM PUBLIC", cancellationToken);
            await ExecuteAsync(
                admin, $"GRANT CONNECT ON DATABASE {databaseName} TO {MigratorRole}, {AppRole}, {BackupRole}", cancellationToken);
            await ExecuteAsync(
                admin,
                $"ALTER ROLE {AppRole} IN DATABASE {databaseName} SET transaction_timeout = '{AppTransactionTimeout}'",
                cancellationToken);
        }

        // El esquema public lo usan las extensiones; nadie más crea objetos en él.
        var builder = new NpgsqlConnectionStringBuilder(superuserConnectionString) { Database = databaseName };
        await using var database = new NpgsqlConnection(builder.ConnectionString);
        await database.OpenAsync(cancellationToken);
        await ExecuteAsync(database, "REVOKE CREATE ON SCHEMA public FROM PUBLIC", cancellationToken);
        await ExecuteAsync(database, $"ALTER SCHEMA public OWNER TO {DatabaseMigrator.OwnerRole}", cancellationToken);
    }

    private static async Task EnsureRoleAsync(
        NpgsqlConnection admin, string role, bool login, string? password, CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @role", admin);
        exists.Parameters.AddWithValue("role", role);
        var verb = await exists.ExecuteScalarAsync(cancellationToken) is null ? "CREATE" : "ALTER";

        var options = login
            ? $"LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD {QuoteLiteral(Guard(password, role))}"
            : "NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE";
        await ExecuteAsync(admin, $"{verb} ROLE {role} WITH {options}", cancellationToken);
    }

    private static string Guard(string? password, string role) =>
        string.IsNullOrWhiteSpace(password) || password.Length < 12
            ? throw new ArgumentException($"La contraseña del rol {role} debe tener al menos 12 caracteres.")
            : password;

    /// <summary>Literal SQL con comillas escapadas (CREATE ROLE no admite parámetros).</summary>
    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Identificadores validados y literales escapados; DDL no admite parámetros.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
