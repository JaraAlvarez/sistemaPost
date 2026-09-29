using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Cloud.Host.Cli;
using Pos.Cloud.Licensing.Application;
using Pos.License.Simulator;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Rotación de la clave de firma de punta a punta (L-04, ADR 0038): la clave de reserva se publica (STANDBY) → el servidor se
/// reinicia con su privada (ACTIVE; la anterior pasa a RETIRED) → los tokens viejos siguen verificando con la clave retirada
/// publicada y el check-in los renueva con la nueva → revocar la retirada (comprometida) deja de aceptarlos.
/// </summary>
public class SigningKeyRotationTests
{
    [Fact]
    public async Task Rotar_la_clave_de_firma_sin_reinstalar_el_POS()
    {
        // 1. Servidor con la clave A; el POS se activa (token firmado con A).
        var first = new CloudServerFactory();
        await first.StartAsync();
        var database = first.Database;
        var keyA = first.PublicKey;
        var (superadmin, owner) = await PortalApi.BootstrapSuperadminAsync(first);
        var customer = await LicensedCustomer.CreateAsync(superadmin);
        var pos = new SimulatedPos(first.CreateClient(), SimulatorIdentity.Create(customer.Nit));
        (await pos.RefreshPublicKeysAsync(Ct)).ShouldHaveSingleItem().Kid.ShouldBe(keyA.Kid);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        var tokenSignedWithA = pos.Token!;

        // 2. Se genera la clave de reserva B (su privada queda fuera del servidor) y se publica por consola.
        using var keyB = LicenseSigningKey.Generate();
        var (exit, output) = await ConsoleAsync(database, "register-standby-key", "--public-key", keyB.PublicKey.X);
        exit.ShouldBe(0, output);
        var published = await pos.RefreshPublicKeysAsync(Ct);
        published.Select(k => (k.Kid, k.Status)).ShouldBe([(keyA.Kid, "ACTIVE"), (keyB.Kid, "STANDBY")], ignoreOrder: true);

        // Un POS instalado DESPUÉS trae A y B embebidas (lo que publicó el servidor).
        var embedded = pos.KnownKeys.ToList();
        superadmin.Dispose();
        await first.DisposeAsync();

        // 3. Reinicio con la privada de B: B pasa a ACTIVE y A a RETIRED (sigue publicada para verificar tokens viejos).
        await using var second = new CloudServerFactory { ExistingDatabase = database, PrivateKeyPem = keyB.ExportPem() };
        await second.StartAsync();
        var admin = await PortalApi.SignInAsync(second, owner);
        var keys = await PortalApi.GetAsync<List<SigningKeyDto>>(admin, "/admin/signing-keys");
        keys.Single(k => k.Kid == keyB.Kid).ShouldSatisfyAllConditions(k => k.Status.ShouldBe("ACTIVE"), k => k.LoadedInServer.ShouldBeTrue());
        keys.Single(k => k.Kid == keyA.Kid).ShouldSatisfyAllConditions(k => k.Status.ShouldBe("RETIRED"), k => k.RetiredAt.ShouldNotBeNull());

        var restarted = SimulatedPos.Restore(second.CreateClient(), pos.Export());
        LicenseToken.Verify(tokenSignedWithA, restarted.TrustedKeys).IsValid.ShouldBeTrue();
        var renewed = await restarted.CheckinAsync(cancellationToken: Ct);
        renewed.Succeeded.ShouldBeTrue(renewed.ErrorMessage);
        LicenseToken.Verify(restarted.Token, restarted.TrustedKeys).Kid.ShouldBe(keyB.Kid);

        // Un POS que solo conocía A se entera de B por /v1/public-keys al recibir un kid desconocido.
        var onlyA = new SimulatedPos(second.CreateClient(), SimulatorIdentity.Create(customer.Nit), [.. embedded.Where(k => k.Kid == keyA.Kid)]);
        (await onlyA.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        onlyA.KnownKeys.ShouldContain(k => k.Kid == keyB.Kid && k.Status == "ACTIVE");
        onlyA.KnownKeys.ShouldContain(k => k.Kid == keyA.Kid && k.Status == "RETIRED");

        // 4. La clave A se considera comprometida: se revoca; sus tokens dejan de aceptarse y deja de publicarse.
        (exit, output) = await ConsoleAsync(database, "revoke-signing-key", "--kid", keyA.Kid);
        exit.ShouldBe(0, output);
        second.Services.GetRequiredService<ITrustedSigningKeys>().Invalidate();
        using var rejected = await second.CreateClient().PostAsJsonAsync(LicensingRoutes.Checkins,
            new CheckinRequest(tokenSignedWithA, pos.Identity.Fingerprint, "1.0.0", 1, DateTimeOffset.UtcNow), Ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PortalApi.ErrorCodeAsync(rejected)).ShouldBe(LicenseErrorCodes.TokenInvalid);
        (await restarted.RefreshPublicKeysAsync(Ct)).ShouldHaveSingleItem().Kid.ShouldBe(keyB.Kid);

        // 5. Un servidor que arranque con la clave retirada/revocada NO firma: el portal funciona, la API responde 503 con código.
        await using var misconfigured = new CloudServerFactory { ExistingDatabase = database, PrivateKeyPem = first.PrivateKeyPem, ExpectSigningDisabled = true };
        var client = await misconfigured.StartAsync();
        using var unavailable = await client.PostAsJsonAsync(LicensingRoutes.Activations,
            new ActivationRequest(customer.License.Key, Guid.CreateVersion7(), pos.Identity.Fingerprint, DeviceRoles.StoreServer, "1.0.0", customer.Nit), Ct);
        unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await PortalApi.ErrorCodeAsync(unavailable)).ShouldBe(LicenseErrorCodes.SigningUnavailable);
    }

    private static async Task<(int Exit, string Output)> ConsoleAsync(CloudTestDatabase database, params string[] args)
    {
        using var output = new StringWriter();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cloud:Database:ConnectionString"] = database.AppConnectionString })
            .Build();
        var exit = await CloudCommands.RunAsync(args, output, output, configuration);
        return (exit, output.ToString());
    }
}
