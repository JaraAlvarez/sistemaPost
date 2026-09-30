using Npgsql;
using Pos.Server.Migrations;
using Testcontainers.PostgreSql;

namespace Pos.Server.IntegrationTests;

/// <summary>
/// Un contenedor PostgreSQL 18 compartido por todas las pruebas de integración (Testcontainers lo elimina al terminar).
/// Cada servidor de prueba recibe su propia base de datos creada y migrada con el migrador real.
/// </summary>
public static class TestPostgres
{
    public const string AppPassword = "app-test-password-123";

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
    {
        // Muchos servidores de prueba corren en paralelo, cada uno con su pool: se amplía el límite de conexiones.
        var container = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=500").Build();
        await container.StartAsync();
        return container;
    });

    /// <summary>Crea una BD migrada y devuelve la cadena de conexión del rol pos_app.</summary>
    public static async Task<string> CreateMigratedDatabaseAsync()
    {
        var container = await Container.Value;
        var name = "pos_it_" + Guid.NewGuid().ToString("N");
        var passwords = new DatabaseRolePasswords("migrator-test-password", AppPassword, "backup-test-password");

        await Lock.WaitAsync();
        try
        {
            await DatabaseCreator.CreateAsync(container.GetConnectionString(), name, passwords);
        }
        finally
        {
            Lock.Release();
        }

        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name };
        builder.Username = DatabaseCreator.MigratorRole;
        builder.Password = passwords.Migrator;
        await new DatabaseMigrator(ScriptCatalog.Default).MigrateAsync(builder.ConnectionString, "tests");

        builder.Username = DatabaseCreator.AppRole;
        builder.Password = passwords.App;
        builder.MaxPoolSize = 40;
        return builder.ConnectionString;
    }

    /// <summary>Cadena de superusuario de la misma BD (solo pruebas de manipulación de la bitácora, Fase 10).</summary>
    public static async Task<string> SuperuserConnectionStringAsync(string appConnectionString)
    {
        var container = await Container.Value;
        var database = new NpgsqlConnectionStringBuilder(appConnectionString).Database;
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = database }.ConnectionString;
    }

    /// <summary>Contenedor compartido (Fase 11: las pruebas de backup ejecutan pg_dump y pg_restore dentro de él).</summary>
    public static Task<PostgreSqlContainer> ContainerAsync() => Container.Value;

    /// <summary>Cadena de un rol de la instalación de pruebas sobre la BD de <paramref name="appConnectionString"/>.</summary>
    public static string RoleConnectionString(string appConnectionString, string role) =>
        new NpgsqlConnectionStringBuilder(appConnectionString)
        {
            Username = role,
            Password = role switch
            {
                DatabaseCreator.MigratorRole => "migrator-test-password",
                DatabaseCreator.BackupRole => "backup-test-password",
                _ => AppPassword,
            },
        }.ConnectionString;
}
