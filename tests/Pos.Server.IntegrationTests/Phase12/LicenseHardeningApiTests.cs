using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Infrastructure.Persistence;
using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;
using Pos.Modules.Licensing.Infrastructure;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase12;

/// <summary>
/// Endurecimiento de la licencia, ronda 2 (docs/fases/fase-licencia-endurecimiento.md): vuelta atrás del actualizador o backup
/// restaurado sin firma, primer sellado revertido, demo que no se alarga y ventana de 24 h que no se reinicia con una fila vieja.
/// </summary>
public class LicenseHardeningApiTests
{
    [Fact]
    public async Task Restaurar_una_bd_sin_firma_con_el_archivo_sellado_restringe_pero_deja_vender_24_horas()
    {
        await using var factory = new LicenseServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/activate", new { licenseKey = FakeLicenseCloud.ValidKey });
        await shop.OpenSessionAsync();
        await RefreshAsync(factory);
        factory.Services.GetRequiredService<LicenseVault>().Sealed.ShouldBeTrue();

        // Backup de antes de la actualización (o vuelta atrás del actualizador): la fila vuelve sin firma.
        await ExecuteAsync(factory, "UPDATE licensing.license_state SET state_mac = NULL");
        (await RefreshAsync(factory)).State.ShouldBe(LicenseStates.Restricted);
        var gate = factory.Services.GetRequiredService<ILicenseGate>().Current;
        (gate.Restricted, gate.SalesWindowClosed).ShouldBe((true, false));

        // La jornada abierta sigue vendiendo (ventana normal de 24 h) y el check-in lo arregla.
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 1);
        (await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 5_000m)])).ShouldNotBeNull();
        (await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/check")).State.ShouldBe(LicenseStates.Valid);
    }

    [Fact]
    public async Task Restaurar_una_bd_sin_firma_en_demo_sigue_en_demo()
    {
        await using var factory = new LicenseServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await RefreshAsync(factory);
        await RefreshAsync(factory);
        factory.Services.GetRequiredService<LicenseVault>().Sealed.ShouldBeTrue();

        await ExecuteAsync(factory, "UPDATE licensing.license_state SET state_mac = NULL");
        var status = await RefreshAsync(factory);
        status.State.ShouldBe(LicenseStates.Demo);
        status.DaysLeft.ShouldBe(30);
        (await GetAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license")).State.ShouldBe(LicenseStates.Demo);
    }

    [Fact]
    public async Task Un_primer_sellado_revertido_no_deja_el_archivo_sellado()
    {
        await using var factory = new LicenseServerFactory();
        await SalesScenario.CreateAsync(factory);
        await ExecuteAsync(factory, "UPDATE licensing.license_state SET state_mac = NULL"); // fila de antes de la firma
        var vault = new LicenseVault(null, NullLogger<LicenseVault>.Instance);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var (context, store) = Store(scope.ServiceProvider, vault);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await store.RaiseMaxObservedAsync(DateTimeOffset.UtcNow, Ct); // firma dentro de la transacción…
            (await store.GetAsync(Ct)).ShouldNotBeNull().Tampered.ShouldBeFalse();
            vault.Sealed.ShouldBeFalse(); // …pero leer lo que ella misma escribió no cuenta como confirmado
            await transaction.RollbackAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var (context, store) = Store(scope.ServiceProvider, vault);
            (await store.GetAsync(Ct)).ShouldNotBeNull().Tampered.ShouldBeFalse(); // sigue sin firma y se adopta
            vault.Sealed.ShouldBeFalse();

            await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
            {
                await store.RaiseMaxObservedAsync(DateTimeOffset.UtcNow, Ct);
                await transaction.CommitAsync(Ct);
            }

            (await store.GetAsync(Ct)).ShouldNotBeNull().Tampered.ShouldBeFalse();
            vault.Sealed.ShouldBeTrue(); // confirmado: desde ahora una fila sin firma es una alteración
        }

        await ExecuteAsync(factory, "UPDATE licensing.license_state SET state_mac = NULL");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await Store(scope.ServiceProvider, vault).Store.GetAsync(Ct)).ShouldNotBeNull().Tampered.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task La_demo_no_se_alarga_cambiando_la_fecha_de_creacion()
    {
        await using var factory = new LicenseServerFactory();
        await SalesScenario.CreateAsync(factory);
        await RefreshAsync(factory);

        var error = await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync(factory, "UPDATE system.installation SET created_at = now() + interval '20 days'"));
        error.MessageText.ShouldContain("inmutable");

        // La copia firmada en license_state tampoco sirve: no cuadra la firma y la demo sigue contando desde la instalación.
        await ExecuteAsync(factory, "UPDATE licensing.license_state SET demo_started_at = now() + interval '20 days'");
        var status = await RefreshAsync(factory);
        status.State.ShouldBe(LicenseStates.Demo);
        status.DaysLeft.ShouldBe(30);
    }

    [Fact]
    public async Task Volver_a_escribir_una_fila_firmada_vieja_no_reinicia_la_ventana_de_24_horas()
    {
        await using var factory = new LicenseServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/activate", new { licenseKey = FakeLicenseCloud.ValidKey });
        await RefreshAsync(factory);

        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        var old = await connection.QuerySingleAsync<Snapshot>(
            """
            SELECT token AS Token, last_checkin_at AS LastCheckinAt, max_observed_utc AS MaxObservedUtc, clock_offset_seconds AS ClockOffsetSeconds,
                   last_state AS LastState, state_mac AS StateMac
            FROM licensing.license_state
            """);
        old.StateMac.ShouldNotBeNull();

        // La nube suspende: restringida desde ahora.
        factory.Cloud.SubscriptionStatus = SubscriptionStatuses.Suspended;
        (await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/check")).State.ShouldBe(LicenseStates.Restricted);
        await RefreshAsync(factory);
        var since = (await RecordAsync(factory)).RestrictedSince.ShouldNotBeNull();

        // Alguien vuelve a escribir la fila firmada de antes (sin restricted_since): ni el token viejo ni la ventana valen.
        await connection.ExecuteAsync(
            """
            UPDATE licensing.license_state SET token = @Token, last_checkin_at = @LastCheckinAt, max_observed_utc = @MaxObservedUtc,
                clock_offset_seconds = @ClockOffsetSeconds, last_state = @LastState, restricted_since = NULL, state_mac = @StateMac
            """,
            old);
        (await RefreshAsync(factory)).State.ShouldBe(LicenseStates.Restricted);
        var record = await RecordAsync(factory);
        record.Tampered.ShouldBeTrue();
        record.RestrictedSince.ShouldNotBeNull().ShouldBe(since, TimeSpan.FromMilliseconds(1));
    }

    private static (PosDbContext Context, LicenseStateStore Store) Store(IServiceProvider services, LicenseVault vault)
    {
        var context = services.GetRequiredService<PosDbContext>();
        return (context, new LicenseStateStore(context, services.GetRequiredService<IInstallationContext>(), vault, services.GetRequiredService<ILicenseKeys>()));
    }

    private static async Task<LicenseRecord> RecordAsync(LicenseServerFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ILicenseStateStore>().GetAsync(Ct)).ShouldNotBeNull();
    }

    private static async Task ExecuteAsync(LicenseServerFactory factory, string sql)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.ExecuteAsync(sql);
    }

    private static async Task<LicenseStatusDto> RefreshAsync(LicenseServerFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RefreshLicenseCommand(), Ct);
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    private sealed record Snapshot(string? Token, DateTime? LastCheckinAt, DateTime MaxObservedUtc, long ClockOffsetSeconds, string? LastState, string? StateMac);
}
