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
using Pos.Server.Migrator;

// Uso:
//   Pos.Server.Migrator create-database --superuser "<cadena>" [--database pos]
//                                       --migrator-password <p> --app-password <p> --backup-password <p>
//   Pos.Server.Migrator migrate  [--connection "<cadena pos_migrator>"]
//   Pos.Server.Migrator status   [--connection "<cadena>"]
//   Pos.Server.Migrator verify   [--connection "<cadena>"]
//   Pos.Server.Migrator verify-audit [--seal <n> --code <XXXX-XXXX-XXXX-XXXX>] [--connection "<cadena>"]
//                                    (filas, sellos y cadena de la auditoría; con --seal, además el sello impreso en un Z)
//   Pos.Server.Migrator reset-owner --username <usuario> [--connection "<cadena pos_migrator>"]
//                       Recuperación de emergencia del Propietario: contraseña temporal, auditoría crítica.
//   Pos.Server.Migrator verify-stock [--connection "<cadena>"]   (saldos de inventario contra el kardex)
//   Pos.Server.Migrator rebuild-stock --warehouse <id> --product <id> --reason "<motivo>" [--connection "<cadena pos_migrator>"]
//                       Reconstruye un saldo desde su kardex; auditado como crítico.
//   Pos.Server.Migrator backup [--kind MANUAL] [--backup-connection "<cadena pos_backup>"] [--data-root D] [--output carpeta] [--pg-bin carpeta]
//                       Backup cifrado y verificado (Fase 11). migrate hace uno PRE_UPDATE antes de aplicar migraciones (--no-backup lo omite).
//   Pos.Server.Migrator verify-backup --file F.posbak [--recovery-code XXXX-…] [--data-root D]
//   Pos.Server.Migrator restore --file F.posbak --superuser "<cadena>" [--recovery-code XXXX-…] [--database pos_rAAAAMMDDhhmm]
//                       [--migrator-connection "<cadena>"] [--data-root D] --yes
//                       Restaura en una BD nueva, verifica y cambia la BD activa en server.json (detenga antes el servicio).
//   Pos.Server.Migrator install --pg-bin <carpeta bin> [--data-root D] [--edition SINGLE|MULTI] [--pg-port 5488]
//                       [--license-server URL] [--update-manifest URL] [--channel stable]
//                       Instalación (Fase 13): PostgreSQL propio (initdb + servicio), BD, roles con contraseñas aleatorias, migraciones y
//                       server.json con los secretos en DPAPI. Si ya está instalado, solo migra (reinstalar / reparar).
//   Pos.Server.Migrator verify-consistency [--connection "<cadena>"]
//                       Después de una prueba de fallos (Fase 14): ventas completas en inventario y caja, pagos que cuadran, ninguna venta
//                       abierta en una jornada cerrada, kardex y auditoría. Código 7 si hay inconsistencias.
//   Pos.Server.Migrator support-bundle [--data-root D] [--output carpeta]
//                       Paquete de soporte (ZIP): registros, versiones, estado de las migraciones y configuración SIN secretos.
// Si no se pasa --connection se usa la variable de entorno POS_MIGRATOR_CONNECTION y, si no, server.json de la instalación.
// La carpeta de datos (--data-root) por defecto es POS_DATA_ROOT o %ProgramData%\PosSupermercado.
// Códigos de salida: 0 = correcto, 1 = error de migración, 2 = uso incorrecto, 3 = migraciones pendientes,
//                    4 = la auditoría tiene hallazgos (posible manipulación), 5 = saldos que no cuadran con el kardex,
//                    6 = falló el backup previo obligatorio, 7 = inconsistencias en verify-consistency.

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(o => o.SingleLine = true));
var logger = loggerFactory.CreateLogger<DatabaseMigrator>();
var appVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

if (args.Length == 0)
{
    Console.Error.WriteLine("Comandos: install | create-database | migrate | status | verify | verify-audit | verify-consistency | reset-owner | verify-stock | rebuild-stock | backup | verify-backup | restore | support-bundle");
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
            // RN-BAK-03: backup obligatorio antes de aplicar migraciones pendientes (--no-backup solo en desarrollo).
            bool hasSchema;
            await using (var probe = new NpgsqlConnection(Connection(options)))
            {
                await probe.OpenAsync();
                hasSchema = await DatabaseMigrator.GetDatabaseVersionAsync(probe) is not null;
            }

            if (!options.ContainsKey("no-backup") && hasSchema && !(await migrator.GetStatusAsync(Connection(options))).IsUpToDate)
            {
                try
                {
                    await BackupCommands.BackupAsync(options, "PRE_UPDATE", appVersion, Connection(options), CancellationToken.None);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NpgsqlException)
                {
                    Console.Error.WriteLine($"No se migró: falló el backup previo obligatorio ({ex.Message}).");
                    return BackupCommands.ExitBackupFailed;
                }
            }

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
                if (options.TryGetValue("seal", out var sealText) && long.TryParse(sealText, System.Globalization.CultureInfo.InvariantCulture, out var sealNo))
                {
                    var nodes = new List<Guid>();
                    await using (var command = dataSource.CreateCommand("SELECT DISTINCT node_id FROM audit.audit_seals WHERE seal_no = $1"))
                    {
                        command.Parameters.AddWithValue(sealNo);
                        await using var reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            nodes.Add(reader.GetGuid(0));
                        }
                    }

                    var matches = false;
                    foreach (var node in nodes)
                    {
                        matches |= (await new AuditVerifier(dataSource).CheckSealCodeAsync(node, sealNo, options.GetValueOrDefault("code") ?? string.Empty))?.Matches == true;
                    }

                    Console.WriteLine(matches ? $"El sello #{sealNo} impreso coincide con la bitácora." : $"EL SELLO #{sealNo} IMPRESO NO COINCIDE.");
                    return audit.IsValid && matches ? 0 : 4;
                }

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

        case "backup":
            await BackupCommands.BackupAsync(options, options.GetValueOrDefault("kind", "MANUAL"), appVersion,
                options.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("POS_MIGRATOR_CONNECTION"), CancellationToken.None);
            return 0;

        case "verify-backup":
            return await BackupCommands.VerifyAsync(options, CancellationToken.None);

        case "restore":
            return await BackupCommands.RestoreAsync(options, appVersion, migrator, CancellationToken.None);

        case "verify-consistency":
            return await ConsistencyCommands.VerifyAsync(Connection(options), CancellationToken.None);
        case "install":
            return await InstallCommands.InstallAsync(options, migrator, appVersion, CancellationToken.None);
        case "support-bundle":
            return await InstallCommands.SupportBundleAsync(options, migrator, appVersion, CancellationToken.None);
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
catch (Pos.Infrastructure.Backup.BackupPackageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
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
        if (!arguments[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Opción inválida: {arguments[i]}"));
        }

        // Una opción sin valor (--yes, --no-backup) es una marca.
        result[arguments[i][2..]] = i + 1 < arguments.Length && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal) ? arguments[++i] : "true";
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
          ?? BackupCommands.ConfigSecret(BackupCommands.DataRoot(options), "MigratorConnectionString")
          ?? throw new ArgumentException("Falta --connection (o la variable POS_MIGRATOR_CONNECTION, o server.json de la instalación).");
