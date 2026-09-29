using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Cloud.Licensing.Application;
using Pos.Infrastructure.Persistence;
using Pos.Licensing.Contracts;
using Pos.Server.Migrations;
using Testcontainers.PostgreSql;

namespace Pos.Cloud.IntegrationTests;

/// <summary>Cadenas de conexión de una BD de la nube de prueba.</summary>
public sealed record CloudTestDatabase(string Name, string SuperuserConnectionString, string MigratorConnectionString, string AppConnectionString);

/// <summary>
/// Un contenedor PostgreSQL 18 compartido por todas las pruebas de integración de la nube. Cada servidor de prueba recibe su
/// propia BD (creada con los roles del producto, SIN migrar: la migra el propio host al arrancar).
/// </summary>
public static class CloudTestPostgres
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly DatabaseRolePasswords Passwords = new("migrator-test-password", "app-test-password-123", "backup-test-password");
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
    {
        var container = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=300").Build();
        await container.StartAsync();
        return container;
    });

    public static async Task<CloudTestDatabase> CreateDatabaseAsync()
    {
        var container = await Container.Value;
        var name = "pos_cloud_it_" + Guid.NewGuid().ToString("N");
        await Lock.WaitAsync();
        try
        {
            await DatabaseCreator.CreateAsync(container.GetConnectionString(), name, Passwords);
        }
        finally
        {
            Lock.Release();
        }

        string As(string? user, string? password)
        {
            var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name, MaxPoolSize = 20 };
            if (user is not null)
            {
                builder.Username = user;
                builder.Password = password;
            }

            return builder.ConnectionString;
        }

        return new CloudTestDatabase(
            name, As(null, null), As(DatabaseCreator.MigratorRole, Passwords.Migrator), As(DatabaseCreator.AppRole, Passwords.App));
    }
}

/// <summary>
/// Servidor de licencias en memoria (TestServer) con su propia BD PostgreSQL real (la migra al arrancar, como en producción con
/// <c>MigrateOnStartup</c>) y su clave privada de firma en un archivo temporal. Para simular un reinicio con OTRA clave (rotación),
/// se crea otra fábrica sobre la misma <see cref="Database"/> con <see cref="PrivateKeyPem"/> distinto.
/// </summary>
public class CloudServerFactory : WebApplicationFactory<Program>
{
    private readonly Lazy<CloudTestDatabase> _database;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pos-cloud-it", Guid.CreateVersion7().ToString("N"));
    private string? _keyPath;

    public CloudServerFactory()
    {
        _database = new Lazy<CloudTestDatabase>(() => ExistingDatabase ?? CloudTestPostgres.CreateDatabaseAsync().GetAwaiter().GetResult());
        using var key = LicenseSigningKey.Generate();
        PrivateKeyPem = key.ExportPem();
    }

    /// <summary>BD a reutilizar (reinicio del servidor); si es nula se crea una nueva.</summary>
    public CloudTestDatabase? ExistingDatabase { get; init; }

    /// <summary>Clave privada de firma que el servidor lee de su archivo al arrancar.</summary>
    public string PrivateKeyPem { get; init; }

    public CloudTestDatabase Database => _database.Value;

    public string PrivateKeyPath => _keyPath ??= WriteKey();

    /// <summary>Clave pública de <see cref="PrivateKeyPem"/>.</summary>
    public LicensePublicKey PublicKey
    {
        get
        {
            using var key = LicenseSigningKey.ImportPem(PrivateKeyPem);
            return key.PublicKey;
        }
    }

    /// <summary>Configuración adicional de una fábrica derivada.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [];

    /// <summary>Crea el servidor y espera a que la BD esté migrada y lista (y la firma habilitada, si la clave es utilizable).</summary>
    public async Task<HttpClient> StartAsync()
    {
        var client = CreateClient();
        var readiness = Services.GetRequiredService<DatabaseReadiness>();
        var signer = Services.GetRequiredService<ILicenseTokenSigner>();
        for (var i = 0; i < 300 && !(readiness.IsReady && (signer.IsAvailable || ExpectSigningDisabled)); i++)
        {
            await Task.Delay(100, Ct);
        }

        readiness.IsReady.ShouldBeTrue($"La BD de la nube no quedó lista: {readiness.Detail}");
        return client;
    }

    /// <summary>La clave configurada está retirada o revocada: el servidor arranca pero no firma.</summary>
    public bool ExpectSigningDisabled { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cloud:Database:ConnectionString"] = Database.AppConnectionString,
            ["Cloud:Database:MigratorConnectionString"] = Database.MigratorConnectionString,
            ["Cloud:Database:MigrateOnStartup"] = "true",
            ["Cloud:Security:PosApiPermitsPerMinute"] = "100000",
            ["Cloud:Security:LoginPermitsPerMinute"] = "100000",
            ["Cloud:Audit:Interval"] = "00:00:00.500",
            ["Cloud:Audit:SafetyHorizon"] = "00:00:01",
            ["Licensing:Signing:PrivateKeyPath"] = PrivateKeyPath,
            ["Licensing:RequestsPerLicensePerHour"] = "100000",
            ["Licensing:LatestPosVersion"] = "1.2.0",
        }).AddInMemoryCollection(ExtraSettings));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    private string WriteKey()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "signing.pem");
        File.WriteAllText(path, PrivateKeyPem);
        return path;
    }
}

/// <summary>Límite por IP de la API del POS muy bajo (3 por minuto).</summary>
public sealed class LowIpLimitCloudServerFactory : CloudServerFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Cloud:Security:PosApiPermitsPerMinute", "3"),
        new("Cloud:Security:LoginPermitsPerMinute", "3"),
    ];
}

/// <summary>Límite por licencia muy bajo (3 solicitudes por hora).</summary>
public sealed class LowLicenseLimitCloudServerFactory : CloudServerFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Licensing:RequestsPerLicensePerHour", "3")];
}
