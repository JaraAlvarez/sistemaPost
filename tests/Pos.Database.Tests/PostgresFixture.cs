using Npgsql;
using Pos.Server.Migrations;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Pos.Database.Tests.PostgresFixture))]

namespace Pos.Database.Tests;

/// <summary>
/// Un contenedor PostgreSQL 18 para todo el ensamblado de pruebas. Cada prueba que modifica datos crea su propia
/// base de datos con <see cref="CreateDatabaseAsync"/> (aislamiento sin reiniciar el contenedor).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:18";

    public static readonly DatabaseRolePasswords Passwords = new(
        Migrator: "migrator-test-password",
        App: "app-test-password-123",
        Backup: "backup-test-password");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public string SuperuserConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        _createLock.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>Crea una BD vacía con los roles del producto (comando create-database).</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(bool migrate = false)
    {
        var name = "pos_test_" + Guid.NewGuid().ToString("N");

        // Los roles son del clúster: se crean de a una base de datos para no chocar en ALTER ROLE concurrentes.
        await _createLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await DatabaseCreator.CreateAsync(SuperuserConnectionString, name, Passwords);
        }
        finally
        {
            _createLock.Release();
        }

        var database = new TestDatabase(this, name);
        if (migrate)
        {
            await new DatabaseMigrator(ScriptCatalog.Default).MigrateAsync(database.MigratorConnectionString, "tests", TestContext.Current.CancellationToken);
        }

        return database;
    }

    public string ConnectionString(string database, string? user = null, string? password = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(SuperuserConnectionString) { Database = database, Pooling = false };
        if (user is not null)
        {
            builder.Username = user;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }
}

/// <summary>Base de datos de una prueba, con las cadenas de conexión de cada rol.</summary>
public sealed record TestDatabase(PostgresFixture Fixture, string Name)
{
    public string SuperuserConnectionString => Fixture.ConnectionString(Name);

    public string MigratorConnectionString =>
        Fixture.ConnectionString(Name, DatabaseCreator.MigratorRole, PostgresFixture.Passwords.Migrator);

    public string AppConnectionString =>
        Fixture.ConnectionString(Name, DatabaseCreator.AppRole, PostgresFixture.Passwords.App);

    public async Task<T?> ScalarAsync<T>(string sql, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public async Task ExecuteAsync(string sql, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<T>> ListAsync<T>(string sql, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<T>();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetFieldValue<T>(0));
        }

        return result;
    }
}
