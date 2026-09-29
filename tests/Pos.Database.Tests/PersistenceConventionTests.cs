using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pos.Application.Abstractions.Messaging;
using Pos.Infrastructure.Outbox;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Database.Tests;

/// <summary>Entidad de prueba mapeada a identity.roles para ejercitar las convenciones del interceptor.</summary>
[Audited("test")]
public sealed class TestRole : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public TestRole(Guid id, Guid companyId, string code, string name)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; set; }

    [Sensitive]
    public string? Description { get; set; }

    public bool IsSystem { get; private set; }

    public string AuditLabel => $"Rol de prueba {Code}";

    public void Touch() => Raise(new TestRoleTouched(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Id));
}

public sealed record TestRoleTouched(Guid EventId, DateTimeOffset OccurredAt, Guid RoleId) : DomainEvent(EventId, OccurredAt);

internal sealed class TestRoleContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<TestRole>(b =>
    {
        b.ToTable("roles", "identity");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasControlColumns();
        b.HasXminConcurrency();
    });
}

internal sealed class TestConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() =>
        new Dictionary<string, Error> { ["ux_roles__company_code"] = Error.Conflict("TEST.ROLE_DUPLICATED", "Duplicado.") };
}

public class PersistenceConventionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Columnas_de_control_row_version_auditoria_y_borrado_logico()
    {
        await using var harness = await CreateAsync();
        var id = Guid.CreateVersion7();

        await InScopeAsync(harness, async context =>
        {
            context.Add(new TestRole(id, InfrastructureHarness.CompanyId, "PRUEBA", "Prueba") { Description = "secreto" });
            await context.SaveChangesAsync(Ct);
        });
        (await harness.Database.ScalarAsync<Guid>($"SELECT created_by FROM identity.roles WHERE id = '{id}'"))
            .ShouldBe(InfrastructureHarness.SystemUserId);
        (await harness.Database.ScalarAsync<long>($"SELECT row_version FROM identity.roles WHERE id = '{id}'")).ShouldBe(1);

        await InScopeAsync(harness, async context =>
        {
            var role = await context.Set<TestRole>().SingleAsync(r => r.Id == id, Ct);
            role.Name = "Prueba 2";
            role.Description = "otro secreto";
            await context.SaveChangesAsync(Ct);
        });
        (await harness.Database.ScalarAsync<long>($"SELECT row_version FROM identity.roles WHERE id = '{id}'")).ShouldBe(2);
        (await harness.Database.ScalarAsync<Guid>($"SELECT updated_by FROM identity.roles WHERE id = '{id}'"))
            .ShouldBe(InfrastructureHarness.SystemUserId);

        var updated = JsonDocument.Parse((await harness.Database.ScalarAsync<string>(
            "SELECT new_values::text FROM audit.audit_log WHERE action = 'TEST_ROLE_UPDATED'"))!).RootElement;
        updated.GetProperty("name").GetString().ShouldBe("Prueba 2");
        updated.GetProperty("description").GetString().ShouldBe(PosSaveChangesInterceptor.Masked);
        updated.TryGetProperty("row_version", out _).ShouldBeFalse();

        // Sin cambios reales no hay auditoría.
        await InScopeAsync(harness, async context =>
        {
            var role = await context.Set<TestRole>().SingleAsync(r => r.Id == id, Ct);
            role.Name = "Prueba 2";
            await context.SaveChangesAsync(Ct);
        });
        (await harness.Database.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'TEST_ROLE_UPDATED'")).ShouldBe(1);

        // Borrar = borrado lógico: la fila queda y las consultas la excluyen.
        await InScopeAsync(harness, async context =>
        {
            context.Remove(await context.Set<TestRole>().SingleAsync(r => r.Id == id, Ct));
            await context.SaveChangesAsync(Ct);
        });
        (await harness.Database.ScalarAsync<bool>($"SELECT deleted_at IS NOT NULL FROM identity.roles WHERE id = '{id}'")).ShouldBeTrue();
        (await harness.Database.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'TEST_ROLE_DELETED'")).ShouldBe("WARNING");
        await InScopeAsync(harness, async context =>
            (await context.Set<TestRole>().AnyAsync(r => r.Id == id, Ct)).ShouldBeFalse());
    }

    [Fact]
    public async Task El_filtro_de_empresa_oculta_los_datos_de_otras_empresas()
    {
        await using var harness = await CreateAsync();
        var other = Guid.CreateVersion7();
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO org.companies (id, legal_name, trade_name, person_type, identification_type, identification_number,
                check_digit, tax_regime, country_code, municipality_code, address, currency_code, timezone, status, created_at, created_by)
            VALUES ('{other}', 'Otra', 'Otra', 'LEGAL', 'NIT', '800197268', '4', '48', 'CO', '05001', 'x', 'COP', 'America/Bogota', 'ACTIVE', now(), '{other}');
            INSERT INTO identity.roles (id, company_id, code, name, is_system, created_at, created_by)
            VALUES (gen_random_uuid(), '{other}', 'AJENO', 'Ajeno', false, now(), '{other}');
            """,
            harness.Database.AppConnectionString);

        await InScopeAsync(harness, async context =>
            (await context.Set<TestRole>().AnyAsync(r => r.Code == "AJENO", Ct)).ShouldBeFalse());
    }

    [Fact]
    public async Task Los_eventos_de_dominio_van_al_outbox_y_el_procesador_los_entrega()
    {
        var handled = new List<Guid>();
        await using var harness = await CreateAsync(services =>
            services.AddScoped<IOutboxMessageHandler>(_ => new RecordingHandler(typeof(TestRoleTouched).FullName!, handled)));
        var id = Guid.CreateVersion7();

        await InScopeAsync(harness, async context =>
        {
            var role = new TestRole(id, InfrastructureHarness.CompanyId, "EVENTO", "Evento");
            role.Touch();
            context.Add(role);
            await context.SaveChangesAsync(Ct);
            role.DomainEvents.ShouldBeEmpty();
        });

        var processor = CreateProcessor(harness);
        (await processor.ProcessBatchAsync(Ct)).ShouldBe(1);

        handled.ShouldBe([id]);
        (await harness.Database.ScalarAsync<string>("SELECT status FROM system.outbox_messages")).ShouldBe("PROCESSED");
        (await processor.ProcessBatchAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Un_handler_que_falla_se_reintenta_con_espera_y_SYNC_no_se_procesa_localmente()
    {
        await using var harness = await CreateAsync(services =>
            services.AddScoped<IOutboxMessageHandler>(_ => new FailingHandler()));

        await using (var scope = harness.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
            outbox.Enqueue(FailingHandler.Type, new { value = 1 }, OutboxDestination.Local);
            outbox.Enqueue("catalog.product_changed.v1", new { value = 2 }, OutboxDestination.Sync);
            await context.SaveChangesAsync(Ct);
        }

        (await CreateProcessor(harness).ProcessBatchAsync(Ct)).ShouldBe(1);

        (await harness.Database.ScalarAsync<string>($"SELECT status FROM system.outbox_messages WHERE type = '{FailingHandler.Type}'")).ShouldBe("FAILED");
        (await harness.Database.ScalarAsync<int>($"SELECT attempts FROM system.outbox_messages WHERE type = '{FailingHandler.Type}'")).ShouldBe(1);
        (await harness.Database.ScalarAsync<bool>(
            $"SELECT next_attempt_at > now() FROM system.outbox_messages WHERE type = '{FailingHandler.Type}'")).ShouldBeTrue();
        (await harness.Database.ScalarAsync<string>("SELECT status FROM system.outbox_messages WHERE destination = 'SYNC'")).ShouldBe("PENDING");
    }

    [Fact]
    public async Task Las_violaciones_de_restricciones_se_traducen_a_errores_de_negocio()
    {
        await using var harness = await CreateAsync(services => services.AddSingleton<IConstraintErrorProvider, TestConstraintErrors>());
        var translator = harness.Services.GetRequiredService<ConstraintErrorTranslator>();

        await InScopeAsync(harness, async context =>
        {
            context.Add(new TestRole(Guid.CreateVersion7(), InfrastructureHarness.CompanyId, "DUP", "Uno"));
            await context.SaveChangesAsync(Ct);
        });

        var duplicate = await Should.ThrowAsync<DbUpdateException>(() => InScopeAsync(harness, async context =>
        {
            context.Add(new TestRole(Guid.CreateVersion7(), InfrastructureHarness.CompanyId, "DUP", "Dos"));
            await context.SaveChangesAsync(Ct);
        }));
        translator.TryTranslate(duplicate, out var known).ShouldBeTrue();
        known.Code.ShouldBe("TEST.ROLE_DUPLICATED");

        var unknownFk = await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
            "INSERT INTO identity.role_permissions (role_id, permission_code) VALUES (gen_random_uuid(), 'x.y.z')",
            harness.Database.AppConnectionString));
        translator.TryTranslate(unknownFk, out var generic).ShouldBeTrue();
        generic.Code.ShouldBe(ConstraintErrorTranslator.GenericCode);

        translator.TryTranslate(new DbUpdateConcurrencyException("x"), out var concurrency).ShouldBeTrue();
        concurrency.Code.ShouldBe(ConstraintErrorTranslator.ConcurrencyCode);
        translator.TryTranslate(new InvalidOperationException("x"), out _).ShouldBeFalse();
    }

    [Fact]
    public async Task La_concurrencia_optimista_detecta_ediciones_simultaneas()
    {
        await using var harness = await CreateAsync();
        var id = Guid.CreateVersion7();
        await InScopeAsync(harness, async context =>
        {
            context.Add(new TestRole(id, InfrastructureHarness.CompanyId, "CONC", "Original"));
            await context.SaveChangesAsync(Ct);
        });

        await using var first = harness.CreateScope();
        await using var second = harness.CreateScope();
        var a = await first.ServiceProvider.GetRequiredService<PosDbContext>().Set<TestRole>().SingleAsync(r => r.Id == id, Ct);
        var b = await second.ServiceProvider.GetRequiredService<PosDbContext>().Set<TestRole>().SingleAsync(r => r.Id == id, Ct);
        a.Name = "Primero";
        b.Name = "Segundo";
        await first.ServiceProvider.GetRequiredService<PosDbContext>().SaveChangesAsync(Ct);

        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => second.ServiceProvider.GetRequiredService<PosDbContext>().SaveChangesAsync(Ct));
    }

    private Task<InfrastructureHarness> CreateAsync(Action<IServiceCollection>? configure = null) =>
        InfrastructureHarness.CreateAsync(postgres, services =>
        {
            services.AddSingleton<IModelContributor, TestRoleContributor>();
            configure?.Invoke(services);
        });

    private static async Task InScopeAsync(InfrastructureHarness harness, Func<PosDbContext, Task> action)
    {
        await using var scope = harness.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    private static OutboxProcessor CreateProcessor(InfrastructureHarness harness)
    {
        var readiness = harness.Services.GetRequiredService<DatabaseReadiness>();
        readiness.Set(DatabaseStatus.Ready, "pruebas");
        return new OutboxProcessor(
            harness.Services.GetRequiredService<NpgsqlDataSource>(),
            harness.Services.GetRequiredService<IServiceScopeFactory>(),
            readiness,
            NullLogger<OutboxProcessor>.Instance);
    }

    private sealed class RecordingHandler(string type, List<Guid> handled) : IOutboxMessageHandler
    {
        public string MessageType => type;

        public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            handled.Add(payload.GetProperty("roleId").GetGuid());
            return Task.CompletedTask;
        }
    }

    private sealed class FailingHandler : IOutboxMessageHandler
    {
        public const string Type = "test.always_fails.v1";

        public string MessageType => Type;

        public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("El proveedor externo no responde.");
    }
}
