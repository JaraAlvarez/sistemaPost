using System.Net;
using System.Net.Http.Json;
using Pos.Cloud.Licensing.Application;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Fase 7-C parte A: la tienda informa su tipo de negocio al activar la licencia y en cada check-in (campo opcional); la nube lo guarda en
/// la empresa y el portal lo muestra ("Tipo de negocio") y filtra por él. Un POS anterior (sin el campo) no borra el dato.
/// </summary>
public class BusinessProfileTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    [Fact]
    public async Task La_tienda_informa_su_tipo_de_negocio_y_la_nube_lo_conserva()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var client = cloud.Factory.CreateClient();
        var fingerprint = DeviceFingerprint.FromHardware("placa-perfil", "disco-perfil", "maq-perfil").ToString();
        var installation = Guid.CreateVersion7();

        // Antes de que la tienda informe: sin dato.
        (await DetailAsync(customer)).Organization.BusinessProfile.ShouldBeNull();

        using var activation = await client.PostAsJsonAsync(LicensingRoutes.Activations,
            new ActivationRequest(customer.License.Key, installation, fingerprint, DeviceRoles.StoreServer, "1.0.0", customer.Nit, BusinessProfile: "PHARMACY"), Ct);
        activation.StatusCode.ShouldBe(HttpStatusCode.OK, await activation.Content.ReadAsStringAsync(Ct));
        var token = (await activation.Content.ReadFromJsonAsync<LicenseTokenResponse>(Ct))!.Token;
        (await DetailAsync(customer)).Organization.BusinessProfile.ShouldBe(BusinessProfileCodes.Pharmacy);

        // El dueño cambió el tipo en Funciones del negocio: el siguiente check-in lo actualiza.
        token = await CheckinAsync(client, token, fingerprint, "hardware");
        (await DetailAsync(customer)).Organization.BusinessProfile.ShouldBe(BusinessProfileCodes.Hardware);

        // Un POS anterior (sin el campo) o un código desconocido no borran el dato ni rechazan el check-in.
        token = await CheckinAsync(client, token, fingerprint, null);
        await CheckinAsync(client, token, fingerprint, "PANADERIA");
        (await DetailAsync(customer)).Organization.BusinessProfile.ShouldBe(BusinessProfileCodes.Hardware);

        // La lista de Empresas lo trae para el filtro del portal.
        var rows = await PortalApi.GetAsync<List<OrganizationRowDto>>(cloud.Superadmin, $"/admin/organizations?accountId={customer.AccountId}");
        rows.ShouldContain(o => o.Id == customer.OrganizationId && o.BusinessProfile == BusinessProfileCodes.Hardware);
        BusinessProfileCodes.Name(rows.Single(o => o.Id == customer.OrganizationId).BusinessProfile).ShouldBe("Ferretería / papelería / miscelánea");
    }

    [Fact]
    public void Los_codigos_del_tipo_de_negocio_se_normalizan()
    {
        BusinessProfileCodes.Normalize(" fresh_market ").ShouldBe(BusinessProfileCodes.FreshMarket);
        BusinessProfileCodes.Normalize("PANADERIA").ShouldBeNull();
        BusinessProfileCodes.Normalize(null).ShouldBeNull();
        BusinessProfileCodes.Name(null).ShouldBe("Sin informar");
        BusinessProfileCodes.All.Count.ShouldBe(6);
    }

    private Task<OrganizationDetailDto> DetailAsync(LicensedCustomer customer) =>
        PortalApi.GetAsync<OrganizationDetailDto>(cloud.Superadmin, $"/admin/organizations/{customer.OrganizationId}");

    private static async Task<string> CheckinAsync(HttpClient client, string token, string fingerprint, string? profile)
    {
        using var response = await client.PostAsJsonAsync(LicensingRoutes.Checkins,
            new CheckinRequest(token, fingerprint, "1.0.1", 2, DateTimeOffset.UtcNow, BusinessProfile: profile), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<LicenseTokenResponse>(Ct))!.Token;
    }
}
