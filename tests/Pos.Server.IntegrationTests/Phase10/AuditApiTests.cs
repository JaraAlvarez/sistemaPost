using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Pos.Modules.Audit.Contracts;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Parties.Contracts;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase10;

/// <summary>
/// Verificación de coherencia de la Fase 10 (propuesta §10): catálogo de acciones = BD, eventos nuevos, datos personales enmascarados,
/// historial con diferencias, verificación manual, incidente por manipulación directa, reconocimiento del propietario y constancia.
/// Las pruebas funcionales las hace el propietario con http/fase-10.http.
/// </summary>
public class AuditApiTests
{
    [Fact]
    public async Task Cobertura_consultas_verificacion_e_incidente_de_integridad()
    {
        await using var factory = new PosServerFactory();
        var owner = await OwnerClientAsync(factory);

        // Catálogo de acciones: el código y la BD coinciden (D10-01).
        var actions = await GetAsync<List<AuditActionDefinition>>(owner, "/api/v1/audit/actions");
        actions.Count.ShouldBe(AuditActions.All.Count);
        var fromDb = new List<string>();
        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT code FROM audit.action_types", connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                fromDb.Add(reader.GetString(0));
            }
        }

        fromDb.ShouldBe(AuditActions.All.Select(a => a.Code), ignoreOrder: true);

        // Evento nuevo: ingreso fallido. (SERVER_STARTED se registra al arrancar con la instalación ya configurada: aquí el asistente
        // inicial corre después del arranque.)
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username = OwnerUsername, password = "equivocada-123" }, Json, Ct);
        var failed = (await GetAsync<AuditLogPage>(owner, "/api/v1/audit/logs?action=LOGIN_FAILED")).Items.ShouldHaveSingleItem();
        failed.ActionName.ShouldBe("Ingreso fallido");

        // Datos personales enmascarados en la bitácora (D10-07) e historial con diferencias en español (D10-03).
        var created = await owner.PostAsJsonAsync("/api/v1/parties", new
        {
            personType = "Natural", identificationType = "CC", identificationNumber = "1098765432", firstNames = "Juana", lastNames = "Pérez",
            email = "juana.perez@correo.com", phone = "300 555 1234",
        }, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var party = (await created.Content.ReadFromJsonAsync<PartyDto>(Json, Ct))!;
        var history = await GetAsync<AuditLogPage>(owner, $"/api/v1/audit/entities/Party/{party.Id}/history");
        var changes = history.Items[0].Changes!;
        changes.Single(c => c.Field == "email").ShouldSatisfyAllConditions(c => c.After.ShouldBe("j***@correo.com"), c => c.Label.ShouldBe("Correo"));
        changes.Single(c => c.Field == "phone").After.ShouldBe("***1234");
        history.Items[0].NewValues!.ShouldNotContain("juana.perez");

        // Verificación manual sin hallazgos.
        var clean = await PostAsync<AuditVerificationDto>(owner, "/api/v1/audit/verify");
        clean.IsValid.ShouldBeTrue(string.Join("\n", clean.Findings.Select(f => f.Message)));
        clean.RunId.ShouldNotBeNull();

        // Alguien con acceso de superusuario altera una fila: la verificación abre un incidente crítico (D10-05).
        await using (var connection = new NpgsqlConnection(await TestPostgres.SuperuserConnectionStringAsync(factory.ConnectionString)))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SET session_replication_role = replica; UPDATE audit.audit_log SET summary = 'alterado' WHERE action = 'LOGIN_FAILED';", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var tampered = await PostAsync<AuditVerificationDto>(owner, "/api/v1/audit/verify");
        tampered.IsValid.ShouldBeFalse();
        tampered.Findings.ShouldContain(f => f.Kind == "RowAltered");
        tampered.IncidentId.ShouldNotBeNull();
        (await GetAsync<MeDto>(owner, "/api/v1/auth/me")).OpenIntegrityIncidents.ShouldBe(1);

        // Solo el propietario lo reconoce, con una nota; después el aviso desaparece y el mismo hallazgo no abre otro incidente.
        var url = $"/api/v1/audit/incidents/{tampered.IncidentId}/acknowledge";
        await owner.PostAsJsonAsync(url, new { note = "corta" }, Json, Ct).ShouldFailWithAsync(HttpStatusCode.BadRequest, "AUDIT.NOTE_REQUIRED");
        var acknowledged = await PostAsync<IntegrityIncidentDto>(owner, url, new { note = "Revisado: prueba controlada de manipulación de la bitácora." });
        acknowledged.Acknowledged.ShouldBeTrue();
        (await GetAsync<MeDto>(owner, "/api/v1/auth/me")).OpenIntegrityIncidents.ShouldBe(0);
        var again = await PostAsync<AuditVerificationDto>(owner, "/api/v1/audit/verify");
        again.IsValid.ShouldBeFalse();
        again.IncidentId.ShouldBeNull();
        (await GetAsync<List<AuditVerificationRunDto>>(owner, "/api/v1/audit/verifications")).Count.ShouldBe(3);

        // Constancia de integridad (ancla externa manual, D10-09).
        var certificate = await owner.GetAsync("/api/v1/audit/integrity-certificate", Ct);
        certificate.StatusCode.ShouldBe(HttpStatusCode.OK);
        certificate.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        (await certificate.Content.ReadAsByteArrayAsync(Ct))[..4].ShouldBe("%PDF"u8.ToArray());
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object? body = null)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { }, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }
}
