using System.Text.Json;
using Pos.Server.Migrations;

namespace Pos.Database.Tests;

/// <summary>
/// V2026.10.100 (revisión R6): al actualizar, los mensajes SYNC ya guardados pierden los hashes de contraseña y PIN y enmascaran los datos
/// personales de usuarios y terceros, igual que la auditoría. Los demás campos y mensajes no cambian.
/// </summary>
public class RedactSyncOutboxMigrationTests(PostgresFixture postgres)
{
    private const string UserMessage = "01920000-0000-7000-8000-0000000001a1";
    private const string PartyMessage = "01920000-0000-7000-8000-0000000001a2";
    private const string ProductMessage = "01920000-0000-7000-8000-0000000001a3";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Los_mensajes_SYNC_guardados_quedan_sin_secretos_y_con_los_datos_personales_enmascarados()
    {
        var db = await postgres.CreateDatabaseAsync();
        var all = ScriptCatalog.Default;
        var migration = all.Versioned.Single(s => s.Name.StartsWith("V2026.10.100__", StringComparison.Ordinal));
        var before = new ScriptCatalog(all.Versioned.Where(s => s != migration).Concat(all.Repeatable).Concat(all.Always));
        await new DatabaseMigrator(before).MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        await db.ExecuteAsync(
            $$$"""
            INSERT INTO system.outbox_messages (id, occurred_at, destination, type, payload, next_attempt_at)
            VALUES
              ('{{{UserMessage}}}', now(), 'SYNC', 'sync.entity_changed.v1',
               '{"entity": "identity.users", "id": "u1", "operation": "CREATED", "changes": {"username": "laura", "password_hash": "$argon2id$v=19$abc",
                 "pin_hash": "$argon2id$v=19$def", "email": "laura.mejia@gmail.com", "display_name": "Laura"}}', now()),
              ('{{{PartyMessage}}}', now(), 'SYNC', 'sync.entity_changed.v1',
               '{"entity": "parties.parties", "id": "p1", "operation": "UPDATED", "changes": {"phone": "+57 300 123 4567", "address": "Calle 10 # 20-30",
                 "notes": "", "email": "sinarroba", "name": "Laura Mejía"}}', now()),
              ('{{{ProductMessage}}}', now(), 'SYNC', 'sync.entity_changed.v1',
               '{"entity": "catalog.products", "id": "x1", "operation": "UPDATED", "changes": {"name": "Arroz", "notes": "texto del producto"}}', now());
            """);

        var report = await new DatabaseMigrator(all).MigrateAsync(db.MigratorConnectionString, "tests", Ct);
        report.AppliedScripts.ShouldContain(migration.Name);

        var user = await ChangesAsync(db, UserMessage);
        user.TryGetProperty("password_hash", out _).ShouldBeFalse();
        user.TryGetProperty("pin_hash", out _).ShouldBeFalse();
        user.GetProperty("email").GetString().ShouldBe("l***@gmail.com");
        user.GetProperty("username").GetString().ShouldBe("laura");
        user.GetProperty("display_name").GetString().ShouldBe("Laura");

        var party = await ChangesAsync(db, PartyMessage);
        party.GetProperty("phone").GetString().ShouldBe("***4567");
        party.GetProperty("address").GetString().ShouldBe("(registrado)");
        party.GetProperty("notes").ValueKind.ShouldBe(JsonValueKind.Null);
        party.GetProperty("email").GetString().ShouldBe("***");
        party.GetProperty("name").GetString().ShouldBe("Laura Mejía");

        // Otras entidades no se tocan.
        (await ChangesAsync(db, ProductMessage)).GetProperty("notes").GetString().ShouldBe("texto del producto");
        (await db.ScalarAsync<long>("SELECT count(*) FROM system.outbox_messages WHERE payload::text LIKE '%argon2id%'")).ShouldBe(0);
    }

    private static async Task<JsonElement> ChangesAsync(TestDatabase db, string id)
    {
        using var document = JsonDocument.Parse((await db.ScalarAsync<string>($"SELECT (payload->'changes')::text FROM system.outbox_messages WHERE id = '{id}'"))!);
        return document.RootElement.Clone();
    }
}
