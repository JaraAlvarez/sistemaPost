using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Pos.Server.Migrations;

// Uso:
//   Pos.Server.Migrator create-database --superuser "<cadena>" [--database pos]
//                                       --migrator-password <p> --app-password <p> --backup-password <p>
//   Pos.Server.Migrator migrate  [--connection "<cadena pos_migrator>"]
//   Pos.Server.Migrator status   [--connection "<cadena>"]
//   Pos.Server.Migrator verify   [--connection "<cadena>"]
// Si no se pasa --connection se usa la variable de entorno POS_MIGRATOR_CONNECTION.
// Códigos de salida: 0 = correcto, 1 = error de migración, 2 = uso incorrecto, 3 = migraciones pendientes.

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(o => o.SingleLine = true));
var logger = loggerFactory.CreateLogger<DatabaseMigrator>();
var appVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

if (args.Length == 0)
{
    Console.Error.WriteLine("Comandos: create-database | migrate | status | verify");
    return 2;
}

var options = ParseOptions(args.Skip(1).ToArray());
var migrator = new DatabaseMigrator(ScriptCatalog.Default, logger);

try
{
    switch (args[0])
    {
        case "create-database":
            await DatabaseCreator.CreateAsync(
                Required(options, "superuser"),
                options.GetValueOrDefault("database", "pos"),
                new DatabaseRolePasswords(
                    Required(options, "migrator-password"),
                    Required(options, "app-password"),
                    Required(options, "backup-password")));
            Console.WriteLine("Base de datos y roles listos.");
            return 0;

        case "migrate":
            var report = await migrator.MigrateAsync(Connection(options), appVersion);
            Console.WriteLine($"Versión de esquema: {report.SchemaVersion} ({report.AppliedScripts.Count} scripts aplicados).");
            return 0;

        case "status":
            var status = await migrator.GetStatusAsync(Connection(options));
            Console.WriteLine($"Versión en la BD:      {status.DatabaseVersion ?? "(sin migrar)"}");
            Console.WriteLine($"Versión esperada:      {status.ExpectedVersion}");
            foreach (var script in status.PendingVersioned.Concat(status.PendingRepeatable))
            {
                Console.WriteLine($"  pendiente: {script.Name}");
            }

            foreach (var name in status.ChecksumMismatches)
            {
                Console.WriteLine($"  MODIFICADO: {name}");
            }

            foreach (var version in status.UnknownVersions)
            {
                Console.WriteLine($"  DESCONOCIDA: {version}");
            }

            return status.HasErrors ? 1 : status.IsUpToDate ? 0 : 3;

        case "verify":
            await migrator.VerifyAsync(Connection(options));
            Console.WriteLine("Esquema verificado: checksums correctos y sin migraciones pendientes.");
            return 0;

        default:
            Console.Error.WriteLine($"Comando desconocido: {args[0]}");
            return 2;
    }
}
catch (MigrationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return ex.Code == MigrationException.PendingMigrations ? 3 : 1;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static Dictionary<string, string> ParseOptions(string[] arguments)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < arguments.Length; i++)
    {
        if (!arguments[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= arguments.Length)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Opción inválida: {arguments[i]}"));
        }

        result[arguments[i][2..]] = arguments[++i];
    }

    return result;
}

static string Required(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Falta la opción --{name}.");

static string Connection(Dictionary<string, string> options) =>
    options.TryGetValue("connection", out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : Environment.GetEnvironmentVariable("POS_MIGRATOR_CONNECTION")
          ?? throw new ArgumentException("Falta --connection (o la variable POS_MIGRATOR_CONNECTION).");
