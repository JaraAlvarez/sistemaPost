using Pos.License.Simulator;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>§11: el simulador de POS (consola) de punta a punta contra el servidor de pruebas.</summary>
public class SimulatorEndToEndTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    [Fact]
    public async Task La_consola_del_simulador_activa_hace_checkin_recibe_la_suspension_y_libera()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var state = Path.Combine(Path.GetTempPath(), $"simulador-{Guid.NewGuid():N}.json");
        try
        {
            var (exit, output) = await RunAsync("keys");
            exit.ShouldBe(0, output);
            output.ShouldContain(cloud.Factory.PublicKey.Kid);

            (exit, output) = await RunAsync("activate", "--key", customer.License.Key, "--nit", customer.Nit, "--state", state, "--branch", "Sucursal Norte");
            exit.ShouldBe(0, output);
            output.ShouldContain("Token verificado (Valid)");
            output.ShouldContain("suscripción TRIAL");
            output.ShouldContain("Modo del POS: Licensed");
            File.Exists(state).ShouldBeTrue();

            (exit, output) = await RunAsync("checkin", "--state", state, "--terminals", "3");
            exit.ShouldBe(0, output);

            (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{customer.SubscriptionId}/suspend", new { reason = "Cheque devuelto" }))
                .Status.ShouldBe(System.Net.HttpStatusCode.NoContent);
            (exit, output) = await RunAsync("checkin", "--state", state);
            exit.ShouldBe(0, output);
            output.ShouldContain("suscripción SUSPENDED");
            output.ShouldContain("Modo del POS: Restricted");
            output.ShouldContain("SUBSCRIPTION_SUSPENDED");

            (exit, output) = await RunAsync("status", "--state", state);
            exit.ShouldBe(0, output);
            output.ShouldContain("Modo del POS: Restricted");

            (exit, output) = await RunAsync("deactivate", "--state", state, "--reason", "Cambio de computador");
            exit.ShouldBe(0, output);

            (exit, output) = await RunAsync("checkin", "--state", state);
            exit.ShouldBe(1, output);
            output.ShouldContain("SIMULATOR.NOT_ACTIVATED");

            // Una clave inválida: la consola informa el código estable y sale con 1.
            (exit, output) = await RunAsync("activate", "--key", LicenseKey.Generate(), "--nit", customer.Nit, "--state", state + ".otro");
            exit.ShouldBe(1, output);
            output.ShouldContain(LicenseErrorCodes.KeyInvalid);
        }
        finally
        {
            File.Delete(state);
        }
    }

    [Fact]
    public async Task El_simulador_descarta_un_token_que_no_es_de_su_equipo_ni_firmado_por_una_clave_de_confianza()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var pos = cloud.NewPos(customer.Nit);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();

        // El mismo token visto por otro equipo o por otra empresa no se acepta.
        var otherDevice = SimulatedPos.Restore(cloud.Factory.CreateClient(), pos.Export());
        otherDevice.ChangeHardware("x", "y", "z");
        otherDevice.Verify(pos.Token!).Problem.ShouldBe("El token es de otro equipo.");
        var otherCompany = new SimulatedPos(cloud.Factory.CreateClient(), pos.Identity with { OrganizationNit = "900123456-8" }, pos.KnownKeys);
        otherCompany.Verify(pos.Token!).Problem.ShouldBe("El token es de otra empresa (NIT).");

        // Firmado con una clave que nadie publicó: UnknownKey.
        using var rogue = LicenseSigningKey.Generate();
        var forged = LicenseToken.Sign(pos.Claims! with { SubscriptionStatus = SubscriptionStatuses.Active }, rogue);
        pos.Verify(forged).Status.ShouldBe(LicenseTokenStatus.UnknownKey);
    }

    private async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var exit = await SimulatorCli.RunAsync(args, output, output, cloud.Factory.CreateClient());
        return (exit, output.ToString());
    }
}
