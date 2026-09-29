using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Time;

namespace Pos.Database.Tests;

/// <summary>
/// Infraestructura de persistencia real (rol pos_app) sobre una BD migrada, con una empresa sembrada:
/// sucursales S01 y S02, cajas C01 y C02 en S01, bodega de piso de venta y usuario system.
/// </summary>
public sealed class InfrastructureHarness : IAsyncDisposable
{
    public static readonly Guid CompanyId = Guid.Parse("01920000-0000-7000-8000-000000000001");
    public static readonly Guid BranchS01 = Guid.Parse("01920000-0000-7000-8000-00000000000a");
    public static readonly Guid BranchS02 = Guid.Parse("01920000-0000-7000-8000-00000000000b");
    public static readonly Guid TerminalC01 = Guid.Parse("01920000-0000-7000-8000-0000000000c1");
    public static readonly Guid TerminalC02 = Guid.Parse("01920000-0000-7000-8000-0000000000c2");
    public static readonly Guid WarehouseS01 = Guid.Parse("01920000-0000-7000-8000-0000000000d1");
    public static readonly Guid SystemUserId = Guid.Parse("01920000-0000-7000-8000-0000000000ff");

    private InfrastructureHarness(TestDatabase database, ServiceProvider services)
    {
        Database = database;
        Services = services;
    }

    public TestDatabase Database { get; }

    public ServiceProvider Services { get; }

    public static async Task<InfrastructureHarness> CreateAsync(PostgresFixture postgres, Action<IServiceCollection>? configure = null)
    {
        var database = await postgres.CreateDatabaseAsync(migrate: true);
        await database.ExecuteAsync(SeedSql, database.AppConnectionString);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPosInfrastructure(BusinessTimeZones.Colombia);
        services.AddPosPersistence(new PersistenceOptions
        {
            ConnectionString = database.AppConnectionString,
            RunBackgroundServices = false,
        });
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await provider.GetRequiredService<IInstallationContext>().RefreshAsync(TestContext.Current.CancellationToken);
        return new InfrastructureHarness(database, provider);
    }

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    public static SettingContext TerminalContext(Guid terminal) => new(CompanyId, BranchS01, terminal);

    private static readonly string SeedSql = $"""
        INSERT INTO org.companies (id, legal_name, trade_name, person_type, identification_type, identification_number,
            check_digit, tax_regime, country_code, municipality_code, address, currency_code, timezone, status, created_at, created_by)
        VALUES ('{CompanyId}', 'Supermercado Prueba SAS', 'Súper Prueba', 'LEGAL', 'NIT', '900123456', '8', '48', 'CO',
            '05001', 'Calle 1 # 2-3', 'COP', 'America/Bogota', 'ACTIVE', now(), '{SystemUserId}');
        INSERT INTO identity.users (id, company_id, username, display_name, kind, status, created_at, created_by)
        VALUES ('{SystemUserId}', '{CompanyId}', 'system', 'Sistema', 'SYSTEM', 'DISABLED', now(), '{SystemUserId}');
        INSERT INTO org.branches (id, company_id, code, name, municipality_code, address, status, created_at, created_by)
        VALUES ('{BranchS01}', '{CompanyId}', 'S01', 'Centro', '05001', 'Calle 1', 'ACTIVE', now(), '{SystemUserId}'),
               ('{BranchS02}', '{CompanyId}', 'S02', 'Norte', '05001', 'Calle 2', 'ACTIVE', now(), '{SystemUserId}');
        INSERT INTO org.warehouses (id, company_id, branch_id, code, name, kind, allows_sales, status, created_at, created_by)
        VALUES ('{WarehouseS01}', '{CompanyId}', '{BranchS01}', 'PISO', 'Piso de venta', 'SALES_FLOOR', true, 'ACTIVE', now(), '{SystemUserId}');
        INSERT INTO org.pos_terminals (id, company_id, branch_id, code, name, warehouse_id, status, created_at, created_by)
        VALUES ('{TerminalC01}', '{CompanyId}', '{BranchS01}', 'C01', 'Caja 1', '{WarehouseS01}', 'ACTIVE', now(), '{SystemUserId}'),
               ('{TerminalC02}', '{CompanyId}', '{BranchS01}', 'C02', 'Caja 2', '{WarehouseS01}', 'ACTIVE', now(), '{SystemUserId}');
        INSERT INTO system.installation (installation_id, node_role, home_company_id, home_branch_id, setup_mode, setup_completed_at, created_at)
        VALUES (gen_random_uuid(), 'STORE_SERVER', '{CompanyId}', '{BranchS01}', 'NEW_COMPANY', now(), now());
        """;
}
