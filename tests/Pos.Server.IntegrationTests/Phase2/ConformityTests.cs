using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Domain;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase2;

/// <summary>
/// Pruebas estructurales que compensan no usar migraciones de EF (decisión A6) y hacen cumplir las convenciones
/// de la Fase 2 sobre el servidor completo, con todos sus módulos.
/// </summary>
public class ConformityTests(PosServerFactory factory) : IClassFixture<PosServerFactory>
{
    [Fact]
    public async Task El_modelo_de_EF_coincide_con_el_esquema_real_de_la_BD()
    {
        _ = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var columns = await ReadColumnsAsync();
        var problems = new List<string>();

        foreach (var entity in context.Model.GetEntityTypes().Where(e => e.GetTableName() is not null))
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
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Toda_entidad_de_negocio_pertenece_a_una_empresa()
    {
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<PosDbContext>().Model;

        var missing = model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.GetSchema() is "org" or "identity" or "catalog" or "inventory" or "parties" or "cash" or "purchasing" or "expenses" or "promotions" or "sales" or "billing" or "customers")
            // Excepciones justificadas: la empresa misma, el catálogo global de permisos, el historial de contraseñas
            // (hijo del usuario), los intentos de acceso (pueden no tener empresa: usuario inexistente) y las líneas de
            // documentos y filas de importación (hijas de un documento que sí tiene empresa).
            .Where(e => e.GetTableName() is not ("companies" or "permissions" or "password_history" or "login_attempts" or "import_rows"
                or "inventory_adjustment_lines" or "inventory_count_lines" or "inventory_count_entries" or "stock_transfer_lines"
                or "party_contacts" or "purchase_order_lines" or "purchase_lines" or "purchase_line_taxes" or "purchase_withholdings" or "payable_entries"
                or "payable_payment_allocations" or "supplier_return_lines" or "cash_counts" or "cash_count_lines" or "cash_session_totals" or "promotion_items" or "promotion_branches"
                or "sale_lines" or "sale_line_taxes" or "sale_payments" or "sale_discounts" or "customer_return_lines" or "fiscal_document_events" or "customer_consents"))
            .Where(e => !typeof(ICompanyOwned).IsAssignableFrom(e.ClrType))
            .Select(e => e.ClrType.FullName)
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Todo_endpoint_de_la_API_declara_su_seguridad()
    {
        _ = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .ToList();

        endpoints.ShouldNotBeEmpty();
        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<PermissionRequirement>() is null
                        && e.Metadata.GetMetadata<AuthenticatedOnly>() is null
                        && e.Metadata.GetMetadata<AnonymousByDesign>() is null)
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}")
            .ToList();

        undeclared.ShouldBeEmpty();
    }

    [Fact]
    public async Task El_catalogo_de_permisos_del_codigo_coincide_con_el_de_la_BD()
    {
        _ = factory.CreateClient();
        var fromCode = factory.Services.GetServices<IPermissionCatalogProvider>()
            .SelectMany(p => p.GetPermissions())
            .Select(p => $"{p.Code}|{p.Module}|{p.Description}|{p.IsSensitive}")
            .Order(StringComparer.Ordinal)
            .ToList();

        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT code, module, description, is_sensitive FROM identity.permissions WHERE NOT is_deprecated ORDER BY code", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var fromDatabase = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            fromDatabase.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetBoolean(3)}");
        }

        fromDatabase.Order(StringComparer.Ordinal).ToList().ShouldBe(fromCode, customMessage: "actualice R__identity__permissions_catalog.sql");
    }

    [Fact]
    public async Task La_sucursal_de_otra_empresa_no_es_visible_por_el_filtro_de_tenencia()
    {
        var client = await OwnerClientAsync(factory);

        // Una empresa ajena insertada directamente en la BD (p. ej. llegada por sincronización).
        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            var other = Guid.CreateVersion7();
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO org.companies (id, legal_name, trade_name, person_type, identification_type, identification_number,
                    check_digit, tax_regime, country_code, municipality_code, address, currency_code, timezone, status, created_at, created_by)
                VALUES ('{other}', 'Otra SAS', 'Otra', 'LEGAL', 'NIT', '800197268', '4', '48', 'CO', '05001', 'x', 'COP', 'America/Bogota', 'ACTIVE', now(), '{other}');
                INSERT INTO org.branches (id, company_id, code, name, municipality_code, address, status, created_at, created_by)
                VALUES (gen_random_uuid(), '{other}', 'X01', 'Ajena', '05001', 'x', 'ACTIVE', now(), '{other}');
                """,
                connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var branches = await GetAsync<List<Pos.Modules.Organization.Contracts.BranchDto>>(client, "/api/v1/organization/branches");
        branches.ShouldNotContain(b => b.Code == "X01");
    }

    private async Task<Dictionary<(string Schema, string Table, string Column), (string Type, bool Nullable)>> ReadColumnsAsync()
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname, c.relname, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('system', 'ref', 'org', 'identity', 'audit', 'catalog', 'inventory', 'parties', 'cash', 'purchasing', 'expenses', 'promotions', 'sales', 'billing', 'customers', 'backup', 'licensing') AND a.attnum > 0 AND NOT a.attisdropped
              AND c.relkind IN ('r', 'p')
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new Dictionary<(string, string, string), (string, bool)>();
        while (await reader.ReadAsync(Ct))
        {
            result[(reader.GetString(0), reader.GetString(1), reader.GetString(2))] = (reader.GetString(3), reader.GetBoolean(4));
        }

        return result;
    }

    /// <summary>Familia de tipo: la longitud de varchar la valida el dominio/DTO; aquí importa que el tipo base coincida.</summary>
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
