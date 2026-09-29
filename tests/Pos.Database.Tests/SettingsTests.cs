using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure.Outbox;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Settings;
using Pos.SharedKernel.Results;

namespace Pos.Database.Tests;

/// <summary>Configuración jerárquica con los ejemplos de la revisión arquitectónica §5.2.</summary>
public class SettingsTests(PostgresFixture postgres)
{
    private static readonly SettingDefinition<decimal> MaxDiscount = new(
        "sales.max_discount_percent_without_authorization",
        0m,
        SettingScope.Company | SettingScope.Branch | SettingScope.Terminal,
        "Descuento máximo sin autorización",
        v => v is < 0 or > 100 ? "Debe estar entre 0 y 100." : null);

    private static readonly SettingDefinition<int> CashIncrement = new(
        "finance.cash_increment", 50, SettingScope.Company | SettingScope.Branch, "Redondeo del efectivo");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SettingTarget Company => new(InfrastructureHarness.CompanyId, SettingScope.Company, InfrastructureHarness.CompanyId);

    private static SettingTarget Branch => new(InfrastructureHarness.CompanyId, SettingScope.Branch, InfrastructureHarness.BranchS01);

    private static SettingTarget C01 => new(InfrastructureHarness.CompanyId, SettingScope.Terminal, InfrastructureHarness.TerminalC01);

    [Fact]
    public async Task Gana_el_nivel_mas_especifico_y_eliminar_vuelve_a_heredar()
    {
        await using var harness = await CreateAsync();
        var c01 = InfrastructureHarness.TerminalContext(InfrastructureHarness.TerminalC01);
        var c02 = InfrastructureHarness.TerminalContext(InfrastructureHarness.TerminalC02);
        var otherBranch = new SettingContext(InfrastructureHarness.CompanyId, InfrastructureHarness.BranchS02);

        (await ResolveAsync(harness, c01)).ShouldBe((0m, SettingSource.Default));

        await SetAsync(harness, Company, 10m);
        (await ResolveAsync(harness, c01)).ShouldBe((10m, SettingSource.Company));

        await SetAsync(harness, Branch, 5m);
        (await ResolveAsync(harness, c01)).ShouldBe((5m, SettingSource.Branch));
        (await ResolveAsync(harness, otherBranch)).ShouldBe((10m, SettingSource.Company));

        await SetAsync(harness, C01, 15m);
        (await ResolveAsync(harness, c01)).ShouldBe((15m, SettingSource.Terminal));
        (await ResolveAsync(harness, c02)).ShouldBe((5m, SettingSource.Branch));

        await RemoveAsync(harness, C01);
        (await ResolveAsync(harness, c01)).ShouldBe((5m, SettingSource.Branch));

        await RemoveAsync(harness, Branch);
        (await ResolveAsync(harness, c01)).ShouldBe((10m, SettingSource.Company));

        await RemoveAsync(harness, Company);
        (await ResolveAsync(harness, c01)).ShouldBe((0m, SettingSource.Default));
    }

    [Fact]
    public async Task Eliminar_un_nivel_superior_no_toca_las_excepciones_inferiores()
    {
        await using var harness = await CreateAsync();
        await SetAsync(harness, Company, 10m);
        await SetAsync(harness, C01, 15m);

        await RemoveAsync(harness, Company);

        (await ResolveAsync(harness, InfrastructureHarness.TerminalContext(InfrastructureHarness.TerminalC01)))
            .ShouldBe((15m, SettingSource.Terminal));
    }

    [Fact]
    public async Task Se_rechazan_claves_desconocidas_alcances_no_permitidos_y_valores_invalidos()
    {
        await using var harness = await CreateAsync();

        (await SetRawAsync(harness, Company, "no.existe", JsonSerializer.SerializeToElement(1))).Error.Code.ShouldBe("SETTINGS.UNKNOWN_KEY");
        (await SetRawAsync(harness, C01, CashIncrement.Key, JsonSerializer.SerializeToElement(100)))
            .Error.Code.ShouldBe("SETTINGS.SCOPE_NOT_ALLOWED");
        (await SetRawAsync(harness, Company, MaxDiscount.Key, JsonSerializer.SerializeToElement(150)))
            .Error.Code.ShouldBe("SETTINGS.INVALID_VALUE");
        (await SetRawAsync(harness, Company, MaxDiscount.Key, JsonSerializer.SerializeToElement("diez")))
            .Error.Code.ShouldBe("SETTINGS.INVALID_VALUE");
        (await SetRawAsync(harness, new SettingTarget(InfrastructureHarness.CompanyId, SettingScope.Branch, Guid.CreateVersion7()),
            MaxDiscount.Key, JsonSerializer.SerializeToElement(5))).Error.Code.ShouldBe("SETTINGS.SCOPE_NOT_FOUND");
    }

    [Fact]
    public async Task Cada_cambio_se_audita_y_emite_evento_de_sincronizacion()
    {
        await using var harness = await CreateAsync();
        await SetAsync(harness, Branch, 5m);
        await SetAsync(harness, Branch, 7m);
        await RemoveAsync(harness, Branch);

        (await harness.Database.ListAsync<string>("SELECT action FROM audit.audit_log ORDER BY seq"))
            .ShouldBe(["SETTING_CHANGED", "SETTING_CHANGED", "SETTING_OVERRIDE_REMOVED"]);
        (await harness.Database.ScalarAsync<string>("SELECT summary FROM audit.audit_log ORDER BY seq OFFSET 1 LIMIT 1"))
            .ShouldBe("sales.max_discount_percent_without_authorization en BRANCH: 5 → 7");
        (await harness.Database.ListAsync<long>(
                $"SELECT (payload->>'rowVersion')::bigint FROM system.outbox_messages WHERE type = '{SettingsWriter.SyncEventType}' ORDER BY node_seq"))
            .ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public async Task Los_valores_efectivos_informan_su_origen_y_lo_que_heredarian()
    {
        await using var harness = await CreateAsync();
        await SetAsync(harness, Company, 10m);
        await SetAsync(harness, C01, 15m);

        await using var scope = harness.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<ISettingsWriter>()
            .GetEffectiveAsync(InfrastructureHarness.TerminalContext(InfrastructureHarness.TerminalC01), Ct);

        var discount = all.Single(s => s.Key == MaxDiscount.Key);
        discount.Value.GetDecimal().ShouldBe(15m);
        discount.Source.ShouldBe(SettingSource.Terminal);
        discount.InheritedValue.GetDecimal().ShouldBe(10m);
        discount.InheritedFrom.ShouldBe(SettingSource.Company);
        all.Single(s => s.Key == CashIncrement.Key).Source.ShouldBe(SettingSource.Default);
    }

    [Fact]
    public async Task La_inbox_no_aplica_dos_veces_el_mismo_evento()
    {
        await using var harness = await CreateAsync();
        var messageId = Guid.CreateVersion7();
        var cloud = Guid.CreateVersion7();

        (await RegisterAsync(harness, messageId, cloud, 1, InboxChannel.Online)).ShouldBeTrue();
        (await RegisterAsync(harness, messageId, cloud, 1, InboxChannel.File)).ShouldBeFalse();
        (await RegisterAsync(harness, Guid.CreateVersion7(), cloud, 1, InboxChannel.File)).ShouldBeFalse();
        (await RegisterAsync(harness, Guid.CreateVersion7(), cloud, 2, InboxChannel.File)).ShouldBeTrue();
    }

    private Task<InfrastructureHarness> CreateAsync() =>
        InfrastructureHarness.CreateAsync(postgres, s => s.AddSingleton<ISettingDefinitionProvider>(new TestDefinitions()));

    private static async Task<(decimal, SettingSource)> ResolveAsync(InfrastructureHarness harness, SettingContext context)
    {
        var resolved = await harness.Services.GetRequiredService<ISettingsReader>().ResolveAsync(MaxDiscount, context, Ct);
        return (resolved.Value, resolved.Source);
    }

    private static async Task SetAsync(InfrastructureHarness harness, SettingTarget target, decimal value) =>
        (await SetRawAsync(harness, target, MaxDiscount.Key, JsonSerializer.SerializeToElement(value))).IsSuccess.ShouldBeTrue();

    private static Task<Result> SetRawAsync(InfrastructureHarness harness, SettingTarget target, string key, JsonElement value) =>
        InCommandAsync(harness, w => w.SetAsync(target, key, value, Ct));

    private static async Task RemoveAsync(InfrastructureHarness harness, SettingTarget target) =>
        (await InCommandAsync(harness, w => w.RemoveAsync(target, MaxDiscount.Key, Ct))).IsSuccess.ShouldBeTrue();

    /// <summary>Simula el TransactionBehavior: transacción, guardado, commit y callbacks posteriores al commit.</summary>
    private static async Task<Result> InCommandAsync(InfrastructureHarness harness, Func<ISettingsWriter, Task<Result>> action)
    {
        await using var scope = harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var result = await action(scope.ServiceProvider.GetRequiredService<ISettingsWriter>());
        if (result.IsSuccess)
        {
            await context.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            scope.ServiceProvider.GetRequiredService<CommitCallbacks>().RunAndClear();
        }

        return result;
    }

    private static async Task<bool> RegisterAsync(InfrastructureHarness harness, Guid messageId, Guid node, long seq, InboxChannel channel)
    {
        await using var scope = harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var registered = await scope.ServiceProvider.GetRequiredService<InboxStore>()
            .TryRegisterAsync(messageId, node, seq, "catalog.product_changed.v1", channel, Ct);
        await context.SaveChangesAsync(Ct);
        return registered;
    }

    private sealed class TestDefinitions : ISettingDefinitionProvider
    {
        public IEnumerable<SettingDefinition> GetDefinitions() => [MaxDiscount, CashIncrement];
    }
}
