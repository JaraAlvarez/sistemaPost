using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pos.Infrastructure;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Security;
using Pos.Modules.Inventory.Infrastructure;
using Pos.Server.Migrations;

// Uso:
//   Pos.Server.Migrator create-database --superuser "<cadena>" [--database pos]
//                                       --migrator-password <p> --app-password <p> --backup-password <p>
//   Pos.Server.Migrator migrate  [--connection "<cadena pos_migrator>"]
//   Pos.Server.Migrator status   [--connection "<cadena>"]
//   Pos.Server.Migrator verify   [--connection "<cadena>"]
//   Pos.Server.Migrator verify-audit [--connection "<cadena>"]   (filas, sellos y cadena de la auditoría)
//   Pos.Server.Migrator reset-owner --username <usuario> [--connection "<cadena pos_migrator>"]
//                       Recuperación de emergencia del Propietario: contraseña temporal, auditoría crítica.
//   Pos.Server.Migrator verify-stock [--connection "<cadena>"]   (saldos de inventario contra el kardex)
//   Pos.Server.Migrator rebuild-stock --warehouse <id> --product <id> --reason "<motivo>" [--connection "<cadena pos_migrator>"]
//                       Reconstruye un saldo desde su kardex; auditado como crítico.
// Si no se pasa --connection se usa la variable de entorno POS_MIGRATOR_CONNECTION.
// Códigos de salida: 0 = correcto, 1 = error de migración, 2 = uso incorrecto, 3 = migraciones pendientes,
//                    4 = la auditoría tiene hallazgos (posible manipulación), 5 = saldos que no cuadran con el kardex.

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(o => o.SingleLine = true));
var logger = loggerFactory.CreateLogger<DatabaseMigrator>();
var appVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

if (args.Length == 0)
{
    Console.Error.WriteLine("Comandos: create-database | migrate | status | verify | verify-audit | reset-owner | verify-stock | rebuild-stock");
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

        case "verify-audit":
            await using (var dataSource = NpgsqlDataSource.Create(Connection(options)))
            {
                var audit = await new AuditVerifier(dataSource).VerifyAsync();
                Console.WriteLine($"Nodos: {audit.NodesChecked} · sellos: {audit.SealsChecked} · filas: {audit.RowsChecked} (sin sellar: {audit.UnsealedRows})");
                Console.WriteLine($"Último sello: {audit.LastSealShortCode ?? "(ninguno)"}");
                foreach (var finding in audit.Findings)
                {
                    Console.WriteLine($"  {finding.Kind}: {finding.Message}");
                }

                Console.WriteLine(audit.IsValid ? "Auditoría íntegra." : "LA AUDITORÍA TIENE HALLAZGOS.");
                return audit.IsValid ? 0 : 4;
            }

        case "reset-owner":
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPosInfrastructure(Pos.SharedKernel.Time.BusinessTimeZones.Colombia);
            services.AddPosPersistence(new PersistenceOptions { ConnectionString = Connection(options), RunBackgroundServices = false });
            await using var provider = services.BuildServiceProvider();
            var reset = await new OwnerEmergencyReset(provider).ResetAsync(Required(options, "username"));
            Console.WriteLine(reset.Message);
            if (reset.TemporaryPassword is not null)
            {
                Console.WriteLine($"Contraseña temporal (entréguela en persona): {reset.TemporaryPassword}");
            }

            return reset.Succeeded ? 0 : 1;
        }

        case "verify-stock":
            await using (var dataSource = NpgsqlDataSource.Create(Connection(options)))
            await using (var connection = await dataSource.OpenConnectionAsync())
            {
                var discrepancies = await StockLedgerMaintenance.FindDiscrepanciesAsync(connection, null, CancellationToken.None);
                var balances = await StockLedgerMaintenance.CountBalancesAsync(connection, null, CancellationToken.None);
                Console.WriteLine($"Saldos revisados: {balances} · diferencias: {discrepancies.Count}");
                foreach (var d in discrepancies)
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  bodega {d.WarehouseId} producto {d.ProductId}: saldo {d.BalanceQuantity} (valor {d.BalanceValue}) · kardex {d.KardexQuantity} (valor {d.KardexValue})"));
                }

                Console.WriteLine(discrepancies.Count == 0 ? "Inventario íntegro: todos los saldos cuadran con el kardex." : "HAY SALDOS QUE NO CUADRAN CON EL KARDEX.");
                return discrepancies.Count == 0 ? 0 : 5;
            }

        case "rebuild-stock":
        {
            var warehouseId = Guid.Parse(Required(options, "warehouse"), CultureInfo.InvariantCulture);
            var productId = Guid.Parse(Required(options, "product"), CultureInfo.InvariantCulture);
            var reason = Required(options, "reason");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPosInfrastructure(Pos.SharedKernel.Time.BusinessTimeZones.Colombia);
            services.AddPosPersistence(new PersistenceOptions { ConnectionString = Connection(options), RunBackgroundServices = false });
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<Pos.Application.Abstractions.Installation.IInstallationContext>().RefreshAsync();
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            await context.Database.OpenConnectionAsync();
            var (before, after) = await StockLedgerMaintenance.RebuildAsync(
                context.Database.GetDbConnection(), Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(transaction),
                warehouseId, productId, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<Pos.Application.Abstractions.Auditing.IAuditWriter>().WriteAsync(
                new Pos.Application.Abstractions.Auditing.AuditEntry("inventory", "STOCK_BALANCE_REBUILT", "StockBalance", productId, $"Saldo en bodega {warehouseId}",
                    string.Create(CultureInfo.InvariantCulture, $"Saldo reconstruido desde el kardex con el migrador ({reason}): {before.Quantity} → {after.Quantity}."),
                    Severity: Pos.Application.Abstractions.Auditing.AuditSeverity.Critical));
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Saldo reconstruido: {before.Quantity} → {after.Quantity} (valor {before.Value} → {after.Value})."));
            return 0;
        }

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
