using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Pos.Modules.Organization.Contracts;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase2;

/// <summary>Criterios de aceptación de la Fase 2 sobre la API real (docs/fases/fase-02-propuesta.md §22).</summary>
public class OrganizationApiTests
{
    [Fact]
    public async Task El_asistente_crea_toda_la_estructura_en_una_transaccion_y_no_se_repite()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();

        var setup = await SetupAsync(client);

        setup.Edition.ShouldBe("MULTI");
        var branch = await GetAsync<BranchDetailDto>(client, $"/api/v1/organization/branches/{setup.BranchId}");
        branch.Branch.Code.ShouldBe("S01");
        branch.Branch.IsHomeBranch.ShouldBeTrue();
        branch.Branch.DefaultWarehouseId.ShouldBe(setup.SalesFloorWarehouseId);
        branch.Warehouses.Select(w => w.Kind).ShouldBe(["Damaged", "SalesFloor", "InTransit"], ignoreOrder: true);
        branch.Terminals.Single().Code.ShouldBe("C01");

        (await GetAsync<List<JsonElement>>(client, "/api/v1/identity/roles")).Count.ShouldBe(7);
        (await ScalarAsync<long>(factory, "SELECT count(*) FROM system.document_series")).ShouldBeGreaterThan(0);
        (await ScalarAsync<string>(factory, "SELECT prefix FROM system.document_series WHERE document_type = 'SALE'")).ShouldBe("S01C01");
        (await ScalarAsync<long>(factory, "SELECT count(*) FROM identity.users WHERE kind = 'SYSTEM'")).ShouldBe(1);
        (await ScalarAsync<long>(factory, $"SELECT count(*) FROM identity.users WHERE id = '{setup.OwnerUserId}' AND kind = 'HUMAN'")).ShouldBe(1);
        (await ScalarAsync<string>(factory, "SELECT kind FROM org.nodes")).ShouldBe("STORE_SERVER");
        (await ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action = 'SETUP_COMPLETED'")).ShouldBe(1);

        await client.PostAsJsonAsync("/api/v1/setup", SetupBody(), Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "SETUP.ALREADY_COMPLETED");
    }

    [Fact]
    public async Task Antes_del_asistente_el_negocio_pide_la_configuracion_inicial()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();

        (await GetAsync<SetupStatusDto>(client, "/api/v1/setup/status")).IsCompleted.ShouldBeFalse();
        await client.GetAsync("/api/v1/organization/company", Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.REQUIRED");
    }

    [Fact]
    public async Task Un_NIT_con_digito_de_verificacion_incorrecto_se_rechaza_y_nada_queda_guardado()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/setup", SetupBody(checkDigit: "3"), Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "ORGANIZATION.INVALID_NIT_CHECK_DIGIT");

        (await ScalarAsync<long>(factory, "SELECT count(*) FROM org.companies")).ShouldBe(0);
        (await GetAsync<SetupStatusDto>(client, "/api/v1/setup/status")).IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task La_edicion_Caja_Unica_admite_una_sola_caja()
    {
        await using var factory = new SingleTerminalServerFactory();
        var client = factory.CreateClient();
        var setup = await SetupAsync(client);
        setup.Edition.ShouldBe("SINGLE");

        await client.PostAsJsonAsync($"/api/v1/organization/branches/{setup.BranchId}/terminals", new { code = "C02", name = "Caja 2" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "LICENSE.EDITION_SINGLE_TERMINAL");
        (await ScalarAsync<string>(factory, "SELECT kind FROM org.nodes")).ShouldBe("ALL_IN_ONE");
    }

    [Fact]
    public async Task La_edicion_Multicaja_admite_varias_cajas_cada_una_con_su_serie()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();
        var setup = await SetupAsync(client);

        foreach (var code in new[] { "C02", "C03", "C04" })
        {
            var response = await client.PostAsJsonAsync(
                $"/api/v1/organization/branches/{setup.BranchId}/terminals", new { code, name = $"Caja {code}" }, Json, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        }

        (await ScalarAsync<long>(factory, "SELECT count(DISTINCT prefix) FROM system.document_series WHERE document_type = 'SALE'"))
            .ShouldBe(4);
        await client.PostAsJsonAsync($"/api/v1/organization/branches/{setup.BranchId}/terminals", new { code = "C02", name = "Repetida" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "ORGANIZATION.TERMINAL_CODE_DUPLICATED");
    }

    [Fact]
    public async Task Reglas_de_inactivacion_de_sucursales_bodegas_y_cajas()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();
        var setup = await SetupAsync(client);

        // La sucursal de la instalación no se inactiva.
        await client.PostAsync($"/api/v1/organization/branches/{setup.BranchId}/deactivate", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "ORGANIZATION.HOME_BRANCH");

        var created = await client.PostAsJsonAsync(
            "/api/v1/organization/branches", new { code = "S02", name = "Norte", municipalityCode = "11001", address = "Cra 7" }, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var s02 = (await created.Content.ReadFromJsonAsync<BranchDetailDto>(Json, Ct))!;
        var terminal = await client.PostAsJsonAsync($"/api/v1/organization/branches/{s02.Branch.Id}/terminals", new { code = "C01", name = "Caja 1" }, Json, Ct);
        var terminalId = (await terminal.Content.ReadFromJsonAsync<TerminalDto>(Json, Ct))!.Id;

        // Con cajas activas no se inactiva; al inactivar la caja, sí.
        await client.PostAsync($"/api/v1/organization/branches/{s02.Branch.Id}/deactivate", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "ORGANIZATION.BRANCH_HAS_ACTIVE_TERMINALS");
        (await client.PostAsJsonAsync($"/api/v1/organization/terminals/{terminalId}/status", new { status = "Inactive" }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync($"/api/v1/organization/branches/{s02.Branch.Id}/deactivate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Bodegas: las de sistema y la bodega por defecto no se inactivan.
        var damaged = s02.Warehouses.Single(w => w.Kind == "Damaged");
        await client.PostAsync($"/api/v1/organization/warehouses/{damaged.Id}/deactivate", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "ORGANIZATION.SYSTEM_WAREHOUSE");
        await client.PostAsync($"/api/v1/organization/warehouses/{setup.SalesFloorWarehouseId}/deactivate", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "ORGANIZATION.WAREHOUSE_IN_USE");
    }

    [Fact]
    public async Task Las_violaciones_de_restricciones_concurrentes_llegan_como_409_y_nunca_como_500()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();
        await SetupAsync(client);

        // Diez creaciones simultáneas con el mismo código: la comprobación previa no ve a las demás;
        // la restricción única de la BD decide y su error se traduce a un código de negocio.
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.PostAsJsonAsync(
            "/api/v1/organization/branches", new { code = "S09", name = "Carrera", municipalityCode = "05001", address = "x" }, Json, Ct)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            (await ErrorCodeAsync(conflict)).ShouldBe("ORGANIZATION.BRANCH_CODE_DUPLICATED");
        }
    }

    [Fact]
    public async Task Los_cambios_de_maestros_quedan_auditados_con_antes_y_despues()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();
        var setup = await SetupAsync(client);

        var update = await client.PutAsJsonAsync(
            $"/api/v1/organization/branches/{setup.BranchId}",
            new { name = "Centro Principal", municipalityCode = "05001", address = "Calle 50 # 45-20", phone = "3001234567" },
            Json,
            Ct);
        update.StatusCode.ShouldBe(HttpStatusCode.OK, await update.Content.ReadAsStringAsync(Ct));

        var page = await GetAsync<JsonElement>(client, $"/api/v1/audit/logs?entityId={setup.BranchId}&action=BRANCH_UPDATED");
        var entry = page.GetProperty("items").EnumerateArray().Single();
        var oldValues = JsonDocument.Parse(entry.GetProperty("oldValues").GetString()!).RootElement;
        var newValues = JsonDocument.Parse(entry.GetProperty("newValues").GetString()!).RootElement;
        oldValues.GetProperty("name").GetString().ShouldBe("Centro");
        newValues.GetProperty("name").GetString().ShouldBe("Centro Principal");
        newValues.GetProperty("phone").GetString().ShouldBe("3001234567");
        newValues.TryGetProperty("address", out _).ShouldBeFalse("solo se registran los campos modificados");
        entry.GetProperty("correlationId").GetString().ShouldNotBeNullOrEmpty();
        (await ScalarAsync<long>(factory, $"SELECT row_version FROM org.branches WHERE id = '{setup.BranchId}'")).ShouldBe(2);

        var verification = await client.PostAsync("/api/v1/audit/verify", null, Ct);
        (await verification.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("isValid").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Configuracion_heredada_por_la_API_con_origen_y_eliminacion()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();
        var setup = await SetupAsync(client);
        const string key = "documents.receipt_footer_message";

        (await client.PutAsJsonAsync($"/api/v1/settings/{key}", new { scope = "Company", value = "Empresa" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync($"/api/v1/settings/{key}", new { scope = "Branch", scopeId = setup.BranchId, value = "Sucursal" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync($"/api/v1/settings/{key}", new { scope = "Terminal", scopeId = setup.PosTerminalId, value = "Caja" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var effective = await FooterAsync(client, $"/api/v1/settings?terminalId={setup.PosTerminalId}");
        effective.GetProperty("value").GetString().ShouldBe("Caja");
        effective.GetProperty("source").GetString().ShouldBe("Terminal");
        effective.GetProperty("inheritedValue").GetString().ShouldBe("Sucursal");

        (await client.DeleteAsync($"/api/v1/settings/{key}?scope=Terminal&scopeId={setup.PosTerminalId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        effective = await FooterAsync(client, $"/api/v1/settings?terminalId={setup.PosTerminalId}");
        effective.GetProperty("value").GetString().ShouldBe("Sucursal");
        effective.GetProperty("source").GetString().ShouldBe("Branch");

        await client.PutAsJsonAsync($"/api/v1/settings/finance.money_decimals", new { scope = "Branch", scopeId = setup.BranchId, value = 0 }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SETTINGS.SCOPE_NOT_ALLOWED");
        await client.PutAsJsonAsync($"/api/v1/settings/no.existe", new { scope = "Company", value = 1 }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.NotFound, "SETTINGS.UNKNOWN_KEY");
    }

    [Fact]
    public async Task Los_catalogos_de_referencia_responden_ordenados_y_sin_tildes_en_la_busqueda()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();

        var municipalities = await GetAsync<List<JsonElement>>(client, "/api/v1/reference/municipalities?search=bogota");
        municipalities.Single().GetProperty("code").GetString().ShouldBe("11001");
        (await GetAsync<List<JsonElement>>(client, "/api/v1/reference/departments")).Count.ShouldBe(33);
        (await GetAsync<List<JsonElement>>(client, "/api/v1/reference/identification-types"))
            .ShouldContain(t => t.GetProperty("code").GetString() == "NIT" && t.GetProperty("fiscalCode").GetString() == "31");
    }

    private static async Task<JsonElement> FooterAsync(HttpClient client, string url) =>
        (await GetAsync<List<JsonElement>>(client, url)).Single(s => s.GetProperty("key").GetString() == "documents.receipt_footer_message");

    internal static async Task<T?> ScalarAsync<T>(PosServerFactory factory, string sql)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? default : (T)value;
    }
}
