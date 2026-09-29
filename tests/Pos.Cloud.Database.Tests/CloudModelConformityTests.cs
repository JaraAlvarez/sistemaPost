using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Cloud.Infrastructure.Persistence;

namespace Pos.Cloud.Database.Tests;

/// <summary>
/// El esquema lo crean los scripts SQL (no hay migraciones de EF): el modelo EF de la nube (todos sus módulos) debe coincidir
/// con las tablas reales, igual que la prueba equivalente del POS (ConformityTests).
/// </summary>
public class CloudModelConformityTests(CloudPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task El_modelo_de_EF_de_la_nube_coincide_con_el_esquema_real_de_la_BD()
    {
        await using var harness = await CloudHarness.CreateAsync(postgres);
        await using var scope = harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var columns = await ReadColumnsAsync(harness.Database.SuperuserConnectionString);
        var problems = new List<string>();
        var mapped = context.Model.GetEntityTypes().Where(e => e.GetTableName() is not null).ToList();

        // Todas las tablas de los módulos de la nube están mapeadas (salvo las que solo escribe el sellador o el migrador).
        var tables = mapped.Select(e => $"{e.GetSchema()}.{e.GetTableName()}").ToHashSet(StringComparer.Ordinal);
        foreach (var table in columns.Keys.Select(k => $"{k.Schema}.{k.Table}").Distinct()
                     .Where(t => t is not ("audit.audit_seals" or "system.cloud_node" or "system.schema_migrations") && !t.StartsWith("audit.audit_log_", StringComparison.Ordinal)))
        {
            if (!tables.Contains(table))
            {
                problems.Add($"{table} no está mapeada en el modelo EF de la nube.");
            }
        }

        foreach (var entity in mapped)
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(table);
                if (column is null || column == "xmin")
                {
                    continue;
                }

                if (!columns.TryGetValue((entity.GetSchema()!, entity.GetTableName()!, column), out var db))
                {
                    problems.Add($"{entity.GetSchema()}.{entity.GetTableName()}.{column} ({entity.ClrType.Name}.{property.Name}) no existe en la BD.");
                    continue;
                }

                if (Family(property.GetColumnType(table)) != Family(db.Type))
                {
                    problems.Add($"{entity.GetSchema()}.{entity.GetTableName()}.{column}: EF {property.GetColumnType(table)} ≠ BD {db.Type}.");
                }

                if (!property.IsNullable && db.Nullable && !property.IsKey())
                {
                    problems.Add($"{entity.GetSchema()}.{entity.GetTableName()}.{column}: obligatorio en el dominio pero admite NULL en la BD.");
                }

                if (property.IsNullable && !db.Nullable && property.ValueGenerated == ValueGenerated.Never)
                {
                    problems.Add($"{entity.GetSchema()}.{entity.GetTableName()}.{column}: opcional en el dominio pero NOT NULL en la BD.");
                }
            }

            // Toda columna NOT NULL sin valor por defecto debe estar mapeada (si no, EF no podría insertar la fila).
            var mappedColumns = entity.GetProperties().Select(p => p.GetColumnName(table)).ToHashSet(StringComparer.Ordinal);
            foreach (var missing in columns
                         .Where(c => c.Key.Schema == entity.GetSchema() && c.Key.Table == entity.GetTableName()
                                     && !c.Value.Nullable && !c.Value.HasDefault && !mappedColumns.Contains(c.Key.Column)))
            {
                problems.Add($"{missing.Key.Schema}.{missing.Key.Table}.{missing.Key.Column}: NOT NULL sin valor por defecto y sin mapear.");
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Ninguna_entidad_de_la_nube_admite_borrado_fisico_en_cascada()
    {
        var model = CreateModel();

        var cascades = model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.DeleteBehavior == DeleteBehavior.Cascade)
            .Select(fk => $"{fk.DeclaringEntityType.ClrType.Name} → {fk.PrincipalEntityType.ClrType.Name}")
            .ToList();

        cascades.ShouldBeEmpty();
    }

    private static IModel CreateModel()
    {
        var options = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql("Host=localhost;Database=modelo")
            .UseSnakeCaseNamingConvention()
            .Options;
        var contributors = typeof(Licensing.Infrastructure.LicensingInfrastructureRegistration).Assembly.GetTypes()
            .Concat(typeof(PortalIdentity.Infrastructure.PortalIdentityInfrastructureRegistration).Assembly.GetTypes())
            .Where(t => typeof(ICloudModelContributor).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t => (ICloudModelContributor)Activator.CreateInstance(t, nonPublic: true)!)
            .ToList();
        using var context = new CloudDbContext(options, contributors);
        return context.Model;
    }

    private static async Task<Dictionary<(string Schema, string Table, string Column), (string Type, bool Nullable, bool HasDefault)>> ReadColumnsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname, c.relname, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
                   a.atthasdef OR a.attidentity <> ''
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('system', 'audit', 'portal', 'licensing') AND a.attnum > 0 AND NOT a.attisdropped
              AND c.relkind IN ('r', 'p') AND NOT c.relispartition
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new Dictionary<(string, string, string), (string, bool, bool)>();
        while (await reader.ReadAsync(Ct))
        {
            result[(reader.GetString(0), reader.GetString(1), reader.GetString(2))] = (reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5));
        }

        return result;
    }

    /// <summary>Familia de tipo: la longitud de varchar la valida el dominio; aquí importa que el tipo base coincida.</summary>
    private static string Family(string? storeType)
    {
        var type = (storeType ?? string.Empty).ToLowerInvariant();
        var paren = type.IndexOf('(', StringComparison.Ordinal);
        type = paren >= 0 ? type[..paren].Trim() : type;
        return type switch
        {
            "text" or "character varying" or "varchar" or "character" or "char" => "string",
            _ => type,
        };
    }
}
