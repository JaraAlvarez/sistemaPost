using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pos.Infrastructure;
using Pos.Infrastructure.Backup;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Backup.Contracts;
using Pos.Modules.Backup.Infrastructure;
using Pos.Server.IntegrationTests.Phase7;
using Pos.Server.Migrations;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase11;

/// <summary>pg_dump y pg_restore ejecutados dentro del contenedor de PostgreSQL 18 de las pruebas (este equipo no los tiene instalados).</summary>
internal sealed class ContainerPgTools : IPgTools
{
    public async Task DumpAsync(string connectionString, string outputFile, string? snapshot, CancellationToken cancellationToken)
    {
        var container = await TestPostgres.ContainerAsync();
        var remote = $"/tmp/{Guid.NewGuid():N}.dump";
        List<string> command = ["env", $"PGPASSWORD={Password(connectionString)}", "pg_dump", "--format=custom", "--compress=6", "--no-owner", "--no-privileges",
            $"--file={remote}"];
        if (snapshot is not null)
        {
            command.Add($"--snapshot={snapshot}");
        }

        command.Add($"--dbname={Inside(connectionString)}");
        await RunAsync(command, cancellationToken);
        await File.WriteAllBytesAsync(outputFile, await container.ReadFileAsync(remote, cancellationToken), cancellationToken);
    }

    public async Task<int> ListAsync(string dumpFile, CancellationToken cancellationToken)
    {
        var remote = await UploadAsync(dumpFile, cancellationToken);
        var output = await RunAsync(["pg_restore", "--list", remote], cancellationToken);
        return output.Split('\n').Count(l => l.Length > 0 && !l.StartsWith(';'));
    }

    public async Task RestoreAsync(string connectionString, string dumpFile, string? role, CancellationToken cancellationToken)
    {
        var remote = await UploadAsync(dumpFile, cancellationToken);
        List<string> command = ["env", $"PGPASSWORD={Password(connectionString)}", "pg_restore", "--no-owner", "--no-privileges", "--disable-triggers",
            "--exit-on-error", "--single-transaction", $"--dbname={Inside(connectionString)}"];
        if (role is not null)
        {
            command.Add($"--role={role}");
        }

        command.Add(remote);
        await RunAsync(command, cancellationToken);
    }

    private static string Inside(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        return $"host=localhost port=5432 dbname={b.Database} user={b.Username}";
    }

    private static string? Password(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).Password;

    private static async Task<string> UploadAsync(string file, CancellationToken cancellationToken)
    {
        var container = await TestPostgres.ContainerAsync();
        var remote = $"/tmp/{Guid.NewGuid():N}.dump";
        await container.CopyAsync(await File.ReadAllBytesAsync(file, cancellationToken), remote, ct: cancellationToken);
        return remote;
    }

    private static async Task<string> RunAsync(List<string> command, CancellationToken cancellationToken)
    {
        var result = await (await TestPostgres.ContainerAsync()).ExecAsync(command, cancellationToken);
        return result.ExitCode == 0 ? result.Stdout : throw new InvalidOperationException($"{command[0]} {command.ElementAtOrDefault(2)}: {result.Stderr}");
    }
}

/// <summary>Caja Única con backups configurados (rol pos_backup) y las herramientas del contenedor.</summary>
public sealed class BackupServerFactory : PosServerFactory
{
    protected override string Edition => "SINGLE";

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Pos:Audit:Interval", "00:00:00.500"),
        new("Pos:Audit:SafetyHorizon", "00:00:02"),
        new("Pos:Database:BackupConnectionString", TestPostgres.RoleConnectionString(ConnectionString, "pos_backup")),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IPgTools, ContainerPgTools>()));
    }
}

/// <summary>
/// Verificación de coherencia de la Fase 11 (entregable del plan): un backup de una tienda con ventas se restaura en OTRA BD solo con el
/// archivo y el código de recuperación (como en otro equipo), y la auditoría, el sello y los conteos coinciden.
/// </summary>
public class BackupApiTests
{
    [Fact]
    public async Task Backup_cifrado_copia_verificacion_y_restauracion_en_otro_equipo()
    {
        await using var factory = new BackupServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 2);
        await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 50_000m)]);

        // Código de recuperación: se muestra una vez y se confirma escribiéndolo de nuevo. Solo el propietario.
        var code = await PostAsync<RecoveryCodeDto>(shop.Owner, "/api/v1/backups/recovery-code");
        await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/backups/recovery-code/confirm", new { code = "0000-0000-0000-0000-0000-0000" })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "BACKUP.RECOVERY_CODE_MISMATCH");
        (await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/backups/recovery-code/confirm", new { code = code.Code })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync<BackupAlertsDto>(shop.Owner, "/api/v1/backups/alerts")).LastBackupTooOld.ShouldBeTrue();

        // Destino externo (una carpeta que hace de USB) probado antes de usarlo.
        var usb = Path.Combine(factory.DataRoot, "usb");
        var destination = await PostAsync<BackupDestinationDto>(shop.Owner, "/api/v1/backups/destinations", new
        {
            kind = "EXTERNAL", name = "USB de prueba", path = usb, onScheduled = false, onNightly = true, onClosing = false, onManual = true,
            keepDaily = 7, keepWeekly = 4, keepMonthly = 12, isActive = true,
        }, HttpStatusCode.Created);
        await PostAsync<string>(shop.Owner, $"/api/v1/backups/destinations/{destination.Id}/test");

        // Respaldar ahora: en cola; aparece en el historial verificado y copiado a LOCAL y a la USB.
        (await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/backups")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        BackupRunDto? run = null;
        for (var i = 0; i < 120 && run is not { Copies.Count: 2 } && run is not { Succeeded: false }; i++)
        {
            // El backup aparece en el historial al quedar verificado; las copias a los destinos terminan un momento después.
            await Task.Delay(500, Ct);
            run = (await GetAsync<List<BackupRunDto>>(shop.Owner, "/api/v1/backups")).FirstOrDefault();
        }

        run.ShouldNotBeNull();
        run.Succeeded.ShouldBeTrue(run.Error);
        run.Verified.ShouldBeTrue();
        run.Copies.Count.ShouldBe(2, string.Join(", ", run.Copies.Select(c => $"{c.DestinationName}: {c.Status} {c.Error}")));
        run.Copies.ShouldAllBe(c => c.Status == "COPIED");
        File.Exists(Path.Combine(usb, run.FileName!)).ShouldBeTrue();
        (await PostAsync<BackupVerificationDto>(shop.Owner, $"/api/v1/backups/{run.Id}/verify")).Valid.ShouldBeTrue();
        var alerts = await GetAsync<BackupAlertsDto>(shop.Owner, "/api/v1/backups/alerts");
        alerts.LastBackupTooOld.ShouldBeFalse();
        alerts.RecoveryCodePending.ShouldBeFalse();
        var download = await shop.Owner.GetAsync($"/api/v1/backups/{run.Id}/download", Ct);
        (await download.Content.ReadAsByteArrayAsync(Ct))[..8].ShouldBe("POSBAK1\n"u8.ToArray());
        await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/backups").ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");

        // "Otro equipo": sin la clave local, solo el archivo (copiado de la USB) y el código de recuperación.
        var package = Path.Combine(usb, run.FileName!);
        var superuser = await TestPostgres.SuperuserConnectionStringAsync(factory.ConnectionString);
        var migrator = TestPostgres.RoleConnectionString(factory.ConnectionString, "pos_migrator");
        var procedure = new BackupRestoreProcedure(new ContainerPgTools());
        RestoreRequest Request(string? recoveryCode, string database) => new(
            package, recoveryCode, null, superuser, database, ScriptCatalog.Default.LatestVersion!, Path.Combine(factory.DataRoot, "restore-" + database));
        Task Create(string admin, string database, CancellationToken ct) => DatabaseCreator.CreateDatabaseOnlyAsync(admin, database, ct);
        Task Migrate(string connection, CancellationToken ct) => new DatabaseMigrator(ScriptCatalog.Default).MigrateAsync(connection, "tests", ct);

        await Should.ThrowAsync<BackupPackageException>(() =>
            procedure.RunAsync(Request("ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ", "pos_restore_bad"), Create, Migrate, migrator, Ct));

        var target = "pos_restore_" + Guid.NewGuid().ToString("N")[..8];
        var outcome = await procedure.RunAsync(Request(code.Code, target), Create, Migrate, migrator, Ct);
        outcome.Audit.IsValid.ShouldBeTrue(string.Join("\n", outcome.Audit.Findings.Select(f => f.Message)));
        outcome.SealMatches.ShouldBeTrue();
        outcome.CountsMatch.ShouldBeTrue();
        outcome.Counts["sales.sales"].ShouldBeGreaterThanOrEqualTo(1);

        // Después de restaurar: la vida del nodo aumenta y queda BACKUP_RESTORED en la bitácora de la BD restaurada.
        var restored = new NpgsqlConnectionStringBuilder(migrator) { Database = target }.ConnectionString;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPosInfrastructure(Pos.SharedKernel.Time.BusinessTimeZones.Colombia);
        services.AddPosPersistence(new PersistenceOptions { ConnectionString = restored, RunBackgroundServices = false });
        await using (var provider = services.BuildServiceProvider())
        {
            await new BackupRestoreFinisher(provider).FinishAsync(outcome.Header, package, "pos_original", Ct);
        }

        await using (var connection = new NpgsqlConnection(restored))
        {
            (await connection.ExecuteScalarAsync<int>("SELECT node_epoch FROM system.installation")).ShouldBe(outcome.Header.NodeEpoch + 1);
            (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'BACKUP_RESTORED'")).ShouldBe(1);
        }

        // Restauración de prueba semanal (BD temporal del rol pos_backup, que se borra al terminar).
        (await factory.Services.GetRequiredService<BackupRunner>().RestoreTestAsync(Ct)).ShouldBe(true);
        (await GetAsync<List<RestoreTestDto>>(shop.Owner, "/api/v1/backups/restore-tests")).ShouldHaveSingleItem().Succeeded.ShouldBeTrue();
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { }, Json, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }
}
