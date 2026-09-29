using System.Net;
using System.Net.Http.Json;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>Límites de peticiones (§6): por IP en el host (429 SECURITY.TOO_MANY_REQUESTS) y por licencia en el caso de uso (429 LICENSE.TOO_MANY_REQUESTS).</summary>
public class RateLimitTests
{
    [Fact]
    public async Task La_API_del_POS_limita_las_peticiones_por_IP()
    {
        await using var factory = new LowIpLimitCloudServerFactory();
        var client = await factory.StartAsync();

        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync(new Uri(LicensingRoutes.PublicKeys, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var limited = await client.PostAsJsonAsync(LicensingRoutes.Activations,
            new ActivationRequest(LicenseKey.Generate(), Guid.CreateVersion7(), "fp1.-.-.-", DeviceRoles.StoreServer, "1.0.0", "900123456-8"), Ct);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await PortalApi.ErrorCodeAsync(limited)).ShouldBe("SECURITY.TOO_MANY_REQUESTS");

        // El límite del POS no afecta la salud del servidor ni el portal.
        (await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Los intentos de acceso al portal tienen su propio límite por IP.
        for (var i = 0; i < 3; i++)
        {
            (await client.PostAsJsonAsync("/admin/auth/login", new { email = "nadie@licencias.co", password = "incorrecta-123" }, Ct)).StatusCode
                .ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var loginLimited = await client.PostAsJsonAsync("/admin/auth/login", new { email = "nadie@licencias.co", password = "incorrecta-123" }, Ct);
        loginLimited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Cada_licencia_tiene_su_propio_limite_por_hora()
    {
        await using var factory = new LowLicenseLimitCloudServerFactory();
        await factory.StartAsync();
        var (superadmin, _) = await PortalApi.BootstrapSuperadminAsync(factory);
        var customer = await LicensedCustomer.CreateAsync(superadmin);
        var other = await LicensedCustomer.CreateAsync(superadmin);
        var pos = new Pos.License.Simulator.SimulatedPos(factory.CreateClient(), Pos.License.Simulator.SimulatorIdentity.Create(customer.Nit));
        await pos.RefreshPublicKeysAsync(Ct);

        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        (await pos.CheckinAsync(cancellationToken: Ct)).Succeeded.ShouldBeTrue();
        (await pos.CheckinAsync(cancellationToken: Ct)).Succeeded.ShouldBeTrue();
        var limited = await pos.CheckinAsync(cancellationToken: Ct);

        limited.StatusCode.ShouldBe(429);
        limited.ErrorCode.ShouldBe(LicenseErrorCodes.TooManyRequests);

        // Otra licencia no se ve afectada.
        var otherPos = new Pos.License.Simulator.SimulatedPos(factory.CreateClient(), Pos.License.Simulator.SimulatorIdentity.Create(other.Nit));
        await otherPos.RefreshPublicKeysAsync(Ct);
        (await otherPos.ActivateAsync(other.License.Key, Ct)).Succeeded.ShouldBeTrue();
    }
}
