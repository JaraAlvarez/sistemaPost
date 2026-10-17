using System.Net;
using System.Net.Http.Json;
using Pos.Licensing.Contracts;
using Pos.Modules.Identity.Contracts;
using Pos.Server.IntegrationTests.Phase2;

namespace Pos.Server.IntegrationTests.Phase13;

/// <summary>Servidor que confía en una clave de firma de prueba (la "nube" que firma el enlace del propietario).</summary>
public sealed class PortalOwnerServerFactory : PosServerFactory
{
    public LicenseSigningKey CloudKey { get; } = LicenseSigningKey.Generate();

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
        [new("Pos:Licensing:DevelopmentTrustedKeys:0", CloudKey.PublicKey.X)];

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        CloudKey.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Fase 13-B (D13B-09): el instalador premium crea al Propietario con la cuenta del portal. El asistente inicial acepta el enlace firmado
/// por la nube: el usuario es el correo del portal, la contraseña la misma (hash local) y queda marcado como cuenta del portal.
/// </summary>
public class PortalOwnerSetupTests
{
    private const string Email = "Dueno.Tienda+pos@Correo.co";
    private const string PortalPassword = "MiClaveDelPortal2026";

    private static string Link(LicenseSigningKey key, string nit = "900123456-8", string email = "dueno.tienda+pos@correo.co", TimeSpan? age = null)
    {
        var issued = DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero);
        return OwnerLinkToken.Sign(new OwnerLinkClaims
        {
            TokenId = Guid.CreateVersion7(),
            Email = email,
            DisplayName = "Dueña del negocio",
            OrganizationNit = nit,
            IssuedAt = issued,
            ExpiresAt = issued + OwnerLinkToken.Lifetime,
        }, key);
    }

    private static object Body(string? token, string username = "se-ignora", string password = PortalPassword)
    {
        var body = (dynamic)ApiClient.SetupBody();
        return new
        {
            company = body.company,
            branch = body.branch,
            owner = new { username, displayName = "Dueña del negocio", password, portalLinkToken = token },
            terminal = body.terminal,
        };
    }

    [Fact]
    public async Task El_asistente_crea_al_propietario_con_el_correo_y_la_contrasena_del_portal()
    {
        await using var factory = new PortalOwnerServerFactory();
        var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/setup", Body(Link(factory.CloudKey)), ApiClient.Json, ApiClient.Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ApiClient.Ct));

        // Entra con el correo (sin importar mayúsculas) y la contraseña del portal, aunque no cumpla la política local de contraseñas.
        var login = await ApiClient.LoginAsync(client, Email, PortalPassword);
        login.User.Username.ShouldBe("dueno.tienda+pos@correo.co");
        login.User.DisplayName.ShouldBe("Dueña del negocio");

        var users = await ApiClient.GetAsync<List<UserDto>>(client, "/api/v1/identity/users");
        var owner = users.ShouldHaveSingleItem();
        owner.Email.ShouldBe("dueno.tienda+pos@correo.co");
        owner.MustChangePassword.ShouldBeFalse();
    }

    [Fact]
    public async Task Un_enlace_alterado_vencido_de_otra_empresa_o_de_otra_clave_se_rechaza()
    {
        await using var factory = new PortalOwnerServerFactory();
        var client = factory.CreateClient();
        using var stranger = LicenseSigningKey.Generate();

        async Task<string?> Setup(string token)
        {
            using var response = await client.PostAsJsonAsync("/api/v1/setup", Body(token), ApiClient.Json, ApiClient.Ct);
            response.IsSuccessStatusCode.ShouldBeFalse();
            return await ApiClient.ErrorCodeAsync(response);
        }

        var valid = Link(factory.CloudKey);
        var parts = valid.Split('.');
        (await Setup($"{parts[0]}.{parts[1][..^2]}AA.{parts[2]}")).ShouldBe("SETUP.PORTAL_LINK_INVALID");
        (await Setup(Link(factory.CloudKey, age: TimeSpan.FromHours(3)))).ShouldBe("SETUP.PORTAL_LINK_INVALID");
        (await Setup(Link(stranger))).ShouldBe("SETUP.PORTAL_LINK_INVALID");
        (await Setup(Link(factory.CloudKey, nit: "800197268-4"))).ShouldBe("SETUP.PORTAL_LINK_OTHER_COMPANY");

        // Nada quedó a medias: el asistente sigue disponible y funciona con un enlace bueno.
        (await ApiClient.GetAsync<Pos.Modules.Organization.Contracts.SetupStatusDto>(client, "/api/v1/setup/status")).IsCompleted.ShouldBeFalse();
        using var ok = await client.PostAsJsonAsync("/api/v1/setup", Body(valid), ApiClient.Json, ApiClient.Ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(ApiClient.Ct));
    }

    [Fact]
    public async Task Sin_enlace_el_propietario_local_sigue_igual_y_puede_ser_un_correo()
    {
        await using var factory = new PortalOwnerServerFactory();
        var client = factory.CreateClient();
        var body = (dynamic)ApiClient.SetupBody();
        var request = new
        {
            company = body.company,
            branch = body.branch,
            owner = new { username = "prueba@minegocio.co", displayName = "Prueba", password = ApiClient.OwnerPassword },
            terminal = body.terminal,
        };

        using var response = await client.PostAsJsonAsync("/api/v1/setup", request, ApiClient.Json, ApiClient.Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ApiClient.Ct));
        (await ApiClient.LoginAsync(client, "prueba@minegocio.co", ApiClient.OwnerPassword)).User.Username.ShouldBe("prueba@minegocio.co");
    }
}
