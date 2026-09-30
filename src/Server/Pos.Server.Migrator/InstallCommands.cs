using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Pos.Infrastructure.Backup;
using Pos.Infrastructure.Security;
using Pos.Server.Migrations;

namespace Pos.Server.Migrator;

/// <summary>
/// Comandos del instalador (docs/fases/fase-13-propuesta.md D13-02, D13-04): PostgreSQL propio, BD, roles, migraciones y configuración
/// cifrada; y el paquete de soporte (§6).
/// </summary>
internal static class InstallCommands
{
    public const string DatabaseName = "pos";
    public const int DefaultPgPort = 5488;

    /// <summary>
    /// Instala (o repara) la base de datos de la tienda. Idempotente: si <c>server.json</c> ya tiene la conexión, solo aplica las migraciones
    /// pendientes; si el clúster ya existe, no lo vuelve a crear.
    /// </summary>
    public static async Task<int> InstallAsync(
        IReadOnlyDictionary<string, string> options, DatabaseMigrator migrator, string appVersion, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("La instalación solo se hace en Windows.");
            return 2;
        }

        var dataRoot = BackupCommands.DataRoot(options);
        var pgBin = Path.GetFullPath(Required(options, "pg-bin"));
        var product = Path.GetFileName(dataRoot.TrimEnd(Path.DirectorySeparatorChar));
        var edition = string.Equals(options.GetValueOrDefault("edition"), "MULTI", StringComparison.OrdinalIgnoreCase) ? "MULTI" : "SINGLE";
        var configFile = Path.Combine(dataRoot, "config", "server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
        foreach (var folder in new[] { "logs", "backups", "updates", "certs" })
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, folder));
        }

        var config = File.Exists(configFile) ? JsonNode.Parse(await File.ReadAllTextAsync(configFile, cancellationToken))!.AsObject() : new JsonObject();
        var database = Section(Section(config, "Pos"), "Database");
        if (database["MigratorConnectionString"]?.GetValue<string>() is { Length: > 0 } existing)
        {
            Console.WriteLine("La instalación ya existe: se aplican las migraciones pendientes (reparar / reinstalar).");
            await EnsureServiceRunningAsync($"{product}-DB", cancellationToken);
            var repaired = await migrator.MigrateAsync(ProtectedSecret.Reveal(existing)!, appVersion, cancellationToken);
            Console.WriteLine($"Versión de esquema: {repaired.SchemaVersion}.");
            ApplySettings(config, options, pgBin, edition);
            await WriteConfigAsync(configFile, config, cancellationToken);
            return 0;
        }

        // 1. Clúster de PostgreSQL propio: solo localhost, puerto no estándar, scram-sha-256.
        var pgData = Path.Combine(dataRoot, "data", "pg");
        var port = int.TryParse(options.GetValueOrDefault("pg-port"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : FreePort(DefaultPgPort);
        var superPassword = Secret();
        if (!File.Exists(Path.Combine(pgData, "PG_VERSION")))
        {
            Directory.CreateDirectory(pgData);
            var passwordFile = Path.Combine(Path.GetTempPath(), $"pos-{Guid.NewGuid():N}.pw");
            await File.WriteAllTextAsync(passwordFile, superPassword, cancellationToken);
            try
            {
                await RunAsync(Path.Combine(pgBin, "initdb.exe"),
                    ["-D", pgData, "-U", "postgres", "--pwfile", passwordFile, "--encoding", "UTF8", "--locale", "C", "--auth", "scram-sha-256"], cancellationToken);
            }
            finally
            {
                File.Delete(passwordFile);
            }

            await File.AppendAllTextAsync(Path.Combine(pgData, "postgresql.conf"), string.Create(CultureInfo.InvariantCulture,
                $"\n# {product} (instalador, Fase 13)\nlisten_addresses = 'localhost'\nport = {port}\nmax_connections = 100\n"), cancellationToken);
            await RunAsync("icacls.exe", [pgData, "/grant", "*S-1-5-20:(OI)(CI)F", "/T", "/Q"], cancellationToken); // NETWORK SERVICE
            await RunAsync(Path.Combine(pgBin, "pg_ctl.exe"),
                ["register", "-N", $"{product}-DB", "-U", @"NT AUTHORITY\NetworkService", "-D", pgData, "-S", "auto", "-w"], cancellationToken);
        }
        else
        {
            Console.WriteLine("El clúster de PostgreSQL ya existe: se reutiliza (sus datos no se tocan).");
            superPassword = ProtectedSecret.Reveal(database["SuperuserConnectionString"]?.GetValue<string>()) is { } saved
                ? new NpgsqlConnectionStringBuilder(saved).Password ?? superPassword
                : throw new InvalidOperationException("Hay un clúster de PostgreSQL de una instalación anterior pero no su configuración; restáurelo o bórrelo.");
        }

        await EnsureServiceRunningAsync($"{product}-DB", cancellationToken);
        var superuser = Connection(port, "postgres", "postgres", superPassword);
        await WaitReadyAsync(superuser, cancellationToken);

        // 2. BD y roles con contraseñas aleatorias (nadie las conoce; quedan en server.json con DPAPI de la máquina).
        var passwords = new DatabaseRolePasswords(Secret(), Secret(), Secret());
        await DatabaseCreator.CreateAsync(superuser, DatabaseName, passwords, cancellationToken);
        var migratorConnection = Connection(port, DatabaseName, DatabaseCreator.MigratorRole, passwords.Migrator);
        var report = await migrator.MigrateAsync(migratorConnection, appVersion, cancellationToken);
        Console.WriteLine($"Base de datos creada y migrada: esquema {report.SchemaVersion}.");

        // 3. Configuración de la instalación.
        database["Edition"] = edition;
        database["MigrateOnStartup"] = false;
        database["ConnectionString"] = ProtectedSecret.Protect(Connection(port, DatabaseName, DatabaseCreator.AppRole, passwords.App));
        database["MigratorConnectionString"] = ProtectedSecret.Protect(migratorConnection);
        database["BackupConnectionString"] = ProtectedSecret.Protect(Connection(port, DatabaseName, DatabaseCreator.BackupRole, passwords.Backup));
        database["SuperuserConnectionString"] = ProtectedSecret.Protect(Connection(port, "postgres", "postgres", superPassword));
        ApplySettings(config, options, pgBin, edition);
        await WriteConfigAsync(configFile, config, cancellationToken);
        Console.WriteLine($"Configuración escrita en {configFile} (secretos cifrados con DPAPI de este equipo).");
        return 0;
    }

    /// <summary>ZIP con registros, versiones, estado de las migraciones, servicios y configuración sin secretos (nada de datos de clientes).</summary>
    public static async Task<int> SupportBundleAsync(
        IReadOnlyDictionary<string, string> options, DatabaseMigrator migrator, string appVersion, CancellationToken cancellationToken)
    {
        var dataRoot = BackupCommands.DataRoot(options);
        var output = options.GetValueOrDefault("output") ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        Directory.CreateDirectory(output);
        var file = Path.Combine(output, $"soporte-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await using (var zip = new ZipArchive(File.Create(file), ZipArchiveMode.Create))
        {
            var summary = new StringBuilder();
            summary.AppendLine(CultureInfo.InvariantCulture, $"Generado: {DateTimeOffset.Now:O}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"Versión del migrador: {appVersion}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"Equipo: {Environment.MachineName} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"Carpeta de datos: {dataRoot}");
            try
            {
                if (BackupCommands.ConfigSecret(dataRoot, "MigratorConnectionString") is { } connection)
                {
                    var status = await migrator.GetStatusAsync(connection, cancellationToken);
                    summary.AppendLine(CultureInfo.InvariantCulture,
                        $"Esquema: {status.DatabaseVersion} (esperado {status.ExpectedVersion}) · pendientes {status.PendingVersioned.Count + status.PendingRepeatable.Count}");
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                summary.AppendLine(CultureInfo.InvariantCulture, $"Base de datos: no responde ({ex.Message})");
            }

            if (OperatingSystem.IsWindows())
            {
                AppendServices(summary, Path.GetFileName(dataRoot.TrimEnd(Path.DirectorySeparatorChar)));
            }

            await AddTextAsync(zip, "resumen.txt", summary.ToString(), cancellationToken);
            var configFile = Path.Combine(dataRoot, "config", "server.json");
            if (File.Exists(configFile))
            {
                await AddTextAsync(zip, "server.json", BackupEngine.Sanitize(await File.ReadAllTextAsync(configFile, cancellationToken)), cancellationToken);
            }

            foreach (var name in new[] { "state.json", "history.jsonl" })
            {
                var path = Path.Combine(dataRoot, "updates", name);
                if (File.Exists(path))
                {
                    await zip.CreateEntryFromFileAsync(path, $"actualizaciones/{name}", CompressionLevel.Optimal, cancellationToken);
                }
            }

            var logs = Path.Combine(dataRoot, "logs");
            if (Directory.Exists(logs))
            {
                foreach (var log in new DirectoryInfo(logs).GetFiles("*.*").OrderByDescending(f => f.LastWriteTimeUtc).Take(10))
                {
                    await using var source = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var entry = zip.CreateEntry($"registros/{log.Name}", CompressionLevel.Optimal);
                    await using var target = await entry.OpenAsync(cancellationToken);
                    await source.CopyToAsync(target, cancellationToken);
                }
            }
        }

        Console.WriteLine($"Paquete de soporte: {file} (sin contraseñas ni datos de clientes).");
        return 0;
    }

    private static void ApplySettings(JsonObject config, IReadOnlyDictionary<string, string> options, string pgBin, string edition)
    {
        var pos = Section(config, "Pos");
        Section(pos, "Backup")["PgBinPath"] = pgBin;
        Section(pos, "Database")["Edition"] = edition;
        if (options.GetValueOrDefault("license-server") is { Length: > 0 } licenseServer)
        {
            Section(pos, "Licensing")["ServerUrl"] = licenseServer;
        }

        var updates = Section(pos, "Updates");
        updates["Mode"] = "Server";
        if (options.GetValueOrDefault("update-manifest") is { Length: > 0 } manifest)
        {
            updates["ManifestUrl"] = manifest;
        }

        updates["Channel"] = options.GetValueOrDefault("channel") ?? updates["Channel"]?.GetValue<string>() ?? "stable";
    }

    private static JsonObject Section(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static async Task WriteConfigAsync(string path, JsonObject config, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path + ".tmp", config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static string Connection(int port, string database, string user, string password) =>
        new NpgsqlConnectionStringBuilder { Host = "localhost", Port = port, Database = database, Username = user, Password = password }.ConnectionString;

    /// <summary>32 bytes aleatorios en base64 sin símbolos que molesten en una cadena de conexión.</summary>
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', 'x').Replace('/', 'y').TrimEnd('=');

    private static int FreePort(int preferred)
    {
        for (var port = preferred; port < preferred + 20; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Ocupado: se prueba el siguiente.
            }
        }

        throw new InvalidOperationException($"No hay un puerto libre entre {preferred} y {preferred + 19} para PostgreSQL.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AppendServices(StringBuilder summary, string product)
    {
        foreach (var service in System.ServiceProcess.ServiceController.GetServices())
        {
            using (service)
            {
                if (service.ServiceName.StartsWith(product, StringComparison.OrdinalIgnoreCase))
                {
                    summary.AppendLine(CultureInfo.InvariantCulture, $"Servicio {service.ServiceName}: {service.Status}");
                }
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task EnsureServiceRunningAsync(string name, CancellationToken cancellationToken)
    {
        using var service = System.ServiceProcess.ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == name)
            ?? throw new InvalidOperationException($"No existe el servicio {name}.");
        if (service.Status != System.ServiceProcess.ServiceControllerStatus.Running)
        {
            service.Start();
            await Task.Run(() => service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)), cancellationToken);
        }
    }

    private static async Task WaitReadyAsync(string connectionString, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (NpgsqlException) when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static async Task RunAsync(string exe, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"No se pudo ejecutar {exe}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} terminó con código {process.ExitCode}: {(await error).Trim()} {(await output).Trim()}");
        }
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string content, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync(cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Falta la opción --{name}.");
}
