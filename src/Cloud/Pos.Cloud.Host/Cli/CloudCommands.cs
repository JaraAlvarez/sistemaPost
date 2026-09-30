using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Infrastructure;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Api;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.PortalIdentity.Api;
using Pos.Cloud.PortalIdentity.Application;
using Pos.Infrastructure;
using Pos.Infrastructure.Auditing;
using Pos.Licensing.Contracts;
using Pos.Server.Migrations;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Host.Cli;

/// <summary>
/// Consola de operación del servidor de licencias (mismo ejecutable; en Docker: <c>docker compose run --rm app &lt;comando&gt;</c>).
/// Las cadenas de conexión y rutas se toman de la configuración (<c>appsettings.json</c> + variables <c>Cloud__…</c> y
/// <c>Licensing__…</c>) salvo que se pasen como opción.
/// Códigos de salida: 0 correcto · 1 error · 2 uso incorrecto · 3 migraciones pendientes · 4 auditoría con hallazgos.
/// </summary>
public static class CloudCommands
{
    private static readonly string[] Commands =
    [
        "setup-database", "migrate", "status", "create-superadmin", "recover-user", "generate-signing-key", "register-standby-key",
        "revoke-signing-key", "verify-audit", "healthcheck", "help",
    ];

    public static bool IsCommand(string argument) => Commands.Contains(argument, StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        configuration ??= new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        try
        {
            var options = ParseOptions(args.Skip(1).ToArray());
            return args[0] switch
            {
                "setup-database" => await SetupDatabaseAsync(options, output),
                "migrate" => await MigrateAsync(MigratorConnection(options, configuration), output),
                "status" => await StatusAsync(MigratorConnection(options, configuration), output),
                "create-superadmin" => await WithServicesAsync(configuration, options, output, error, async d =>
                    Print(await d.Send(new BootstrapSuperadminCommand(Required(options, "email"), Required(options, "name"))), output, error)),
                "recover-user" => await WithServicesAsync(configuration, options, output, error, async d =>
                    Print(await d.Send(new EmergencyRecoverUserCommand(Required(options, "email"), Required(options, "reason"))), output, error)),
                "generate-signing-key" => GenerateSigningKey(Required(options, "out"), output),
                "generate-sync-key" => GenerateSyncKey(Required(options, "out"), output),
                "register-standby-key" => await WithServicesAsync(configuration, options, output, error, async d =>
                    Print(await d.Send(new RegisterStandbySigningKeyCommand(Required(options, "public-key"))), output, error, kid => $"Clave de reserva publicada: {kid}")),
                "revoke-signing-key" => await WithServicesAsync(configuration, options, output, error, async d =>
                {
                    var result = await d.Send(new RevokeSigningKeyCommand(Required(options, "kid")));
                    return Print(result.IsSuccess ? Pos.SharedKernel.Results.Result.Success("Clave revocada.") : Pos.SharedKernel.Results.Result.Failure<string>(result.Error), output, error, s => s);
                }),
                "verify-audit" => await VerifyAuditAsync(AppConnection(options, configuration), output),
                "healthcheck" => await HealthCheckAsync(options.GetValueOrDefault("url") ?? "http://localhost:8080/health/live", error),
                _ => Help(output),
            };
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 2;
        }
        catch (MigrationException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return ex.Code == MigrationException.PendingMigrations ? 3 : 1;
        }
        catch (NpgsqlException ex)
        {
            await error.WriteLineAsync($"No se pudo usar la base de datos: {ex.Message}");
            return 1;
        }
    }

    private static int Help(TextWriter output)
    {
        output.WriteLine("""
            Consola del servidor de licencias:
              setup-database --superuser "<cadena>" [--database pos_cloud] --migrator-password <p> --app-password <p> --backup-password <p>
                             Crea la BD y sus roles (pos_owner, pos_migrator, pos_app, pos_backup) y aplica las migraciones.
              migrate [--connection "<cadena pos_migrator>"]      Aplica las migraciones pendientes.
              status  [--connection "<cadena>"]                   Versión del esquema y pendientes.
              create-superadmin --email <correo> --name "<nombre>" Primer superadministrador (contraseña temporal; solo si no hay otro).
              recover-user --email <correo> --reason "<motivo>"   Emergencia: contraseña temporal, reinicia el doble factor y desbloquea.
              generate-signing-key --out <archivo.pem>            Genera un par Ed25519 (privada en el archivo; imprime kid y pública).
              register-standby-key --public-key <x>               Publica una clave de reserva (su privada queda fuera del servidor).
              revoke-signing-key --kid <kid>                      Revoca una clave comprometida.
              generate-sync-key --out <archivo>                   Clave de los paquetes .possync (Fase 16): privada en el archivo; imprime la pública.
              verify-audit                                        Verifica filas, sellos y cadena de la auditoría.
              healthcheck [--url http://localhost:8080/health/live]  Sonda de salud del contenedor.
            """);
        return 0;
    }

    private static async Task<int> SetupDatabaseAsync(Dictionary<string, string> options, TextWriter output)
    {
        var superuser = Required(options, "superuser");
        var database = options.GetValueOrDefault("database") ?? "pos_cloud";
        var passwords = new DatabaseRolePasswords(Required(options, "migrator-password"), Required(options, "app-password"), Required(options, "backup-password"));
        await DatabaseCreator.CreateAsync(superuser, database, passwords);
        var migrator = new NpgsqlConnectionStringBuilder(superuser)
        {
            Database = database,
            Username = DatabaseCreator.MigratorRole,
            Password = passwords.Migrator,
        };
        await output.WriteLineAsync($"Base de datos '{database}' y roles listos.");
        return await MigrateAsync(migrator.ConnectionString, output);
    }

    private static async Task<int> MigrateAsync(string connection, TextWriter output)
    {
        var report = await CloudDatabase.CreateMigrator().MigrateAsync(connection, Hosting.CloudStartup.AppVersion);
        await output.WriteLineAsync($"Versión de esquema: {report.SchemaVersion} ({report.AppliedScripts.Count} scripts aplicados).");
        return 0;
    }

    private static async Task<int> StatusAsync(string connection, TextWriter output)
    {
        var status = await CloudDatabase.CreateMigrator().GetStatusAsync(connection);
        await output.WriteLineAsync($"Versión en la BD: {status.DatabaseVersion ?? "(sin migrar)"} · esperada: {status.ExpectedVersion}");
        foreach (var script in status.PendingVersioned.Concat(status.PendingRepeatable))
        {
            await output.WriteLineAsync($"  pendiente: {script.Name}");
        }

        foreach (var name in status.ChecksumMismatches)
        {
            await output.WriteLineAsync($"  MODIFICADO: {name}");
        }

        return status.HasErrors ? 1 : status.IsUpToDate ? 0 : 3;
    }

    /// <summary>Genera la clave privada en un archivo NUEVO con permisos solo para el dueño (no la sobrescribe nunca).</summary>
    /// <summary>Clave de sincronización (Fase 16, D16-04): la privada abre los paquetes .possync; la pública se embebe en el POS.</summary>
    private static int GenerateSyncKey(string path, TextWriter output)
    {
        var (key, publicKey) = Pos.Sync.Contracts.SyncPackage.GenerateKeyPair();
        using (key)
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(Pos.Sync.Contracts.SyncPackage.ExportPrivate(key));
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            output.WriteLine($"Clave privada de sincronización guardada en {full} (permisos 600). Configure Sync__PrivateKeyPath y haga su respaldo cifrado.");
            output.WriteLine($"clave pública (para src/Modules/Sync/Pos.Modules.Sync.Infrastructure/sync-key.json): {publicKey}");
        }

        return 0;
    }

    private static int GenerateSigningKey(string path, TextWriter output)
    {
        using var key = LicenseSigningKey.Generate();
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(key.ExportPem());
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        output.WriteLine($"Clave privada guardada en {full} (permisos 600). Haga YA su respaldo cifrado fuera del servidor.");
        output.WriteLine($"kid:            {key.Kid}");
        output.WriteLine($"clave pública:  {key.PublicKey.X}");
        return 0;
    }

    private static async Task<int> VerifyAuditAsync(string connection, TextWriter output)
    {
        await using var dataSource = NpgsqlDataSource.Create(connection);
        var report = await new AuditVerifier(dataSource).VerifyAsync();
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Sellos: {report.SealsChecked} · filas: {report.RowsChecked} (sin sellar: {report.UnsealedRows}) · último sello: {report.LastSealShortCode ?? "(ninguno)"}"));
        foreach (var finding in report.Findings)
        {
            await output.WriteLineAsync($"  {finding.Kind}: {finding.Message}");
        }

        await output.WriteLineAsync(report.IsValid ? "Auditoría íntegra." : "LA AUDITORÍA TIENE HALLAZGOS.");
        return report.IsValid ? 0 : 4;
    }

    private static async Task<int> HealthCheckAsync(string url, TextWriter error)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(new Uri(url));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (TaskCanceledException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    /// <summary>Servicios de la aplicación (sin HTTP ni procesos de fondo) para ejecutar un caso de uso desde la consola.</summary>
    private static async Task<int> WithServicesAsync(
        IConfiguration configuration, Dictionary<string, string> options, TextWriter output, TextWriter error, Func<IDispatcher, Task<int>> action)
    {
        var connection = AppConnection(options, configuration);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddPosInfrastructure(BusinessTimeZones.Colombia);
        services.AddCloudPersistence(_ => new CloudPersistenceOptions { ConnectionString = connection }, runBackgroundServices: false);
        var keys = configuration[$"{CloudOptions.SectionName}:{nameof(CloudOptions.DataProtectionKeysPath)}"];
        var dataProtection = services.AddDataProtection().SetApplicationName("pos-cloud");
        if (!string.IsNullOrWhiteSpace(keys))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keys));
        }

        new PortalIdentityModule().Register(services, configuration);
        new LicensingModule().Register(services, configuration);
        await using var provider = services.BuildServiceProvider();
        var state = await CloudDatabase.CheckAsync(provider.GetRequiredService<NpgsqlDataSource>());
        if (!state.IsCurrent)
        {
            await error.WriteLineAsync(state.Detail);
            return 1;
        }

        await provider.GetRequiredService<CloudNodeContext>().RefreshAsync();
        await using var scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IDispatcher>());
    }

    private static int Print(Pos.SharedKernel.Results.Result<TemporaryPasswordDto> result, TextWriter output, TextWriter error) =>
        Print(result, output, error, p =>
            $"Usuario {p.Email}. Contraseña temporal (entréguela en persona; se cambia en el primer ingreso junto con el doble factor): {p.TemporaryPassword}");

    private static int Print<T>(Pos.SharedKernel.Results.Result<T> result, TextWriter output, TextWriter error, Func<T, string> message)
    {
        if (result.IsFailure)
        {
            error.WriteLine($"{result.Error.Code}: {result.Error.Message}");
            return 1;
        }

        output.WriteLine(message(result.Value));
        return 0;
    }

    private static string AppConnection(Dictionary<string, string> options, IConfiguration configuration) =>
        options.GetValueOrDefault("connection") ?? configuration["Cloud:Database:ConnectionString"]
        ?? throw new ArgumentException("Falta --connection (o Cloud__Database__ConnectionString).");

    private static string MigratorConnection(Dictionary<string, string> options, IConfiguration configuration) =>
        options.GetValueOrDefault("connection") ?? configuration["Cloud:Database:MigratorConnectionString"]
        ?? throw new ArgumentException("Falta --connection (o Cloud__Database__MigratorConnectionString).");

    private static Dictionary<string, string> ParseOptions(string[] arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Length; i++)
        {
            if (!arguments[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= arguments.Length)
            {
                throw new ArgumentException($"Opción inválida: {arguments[i]}");
            }

            result[arguments[i][2..]] = arguments[++i];
        }

        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Falta la opción --{name}.");
}
