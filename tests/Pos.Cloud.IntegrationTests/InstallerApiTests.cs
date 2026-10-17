using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// API del instalador premium (Fase 13-B): vista previa de la clave (D13B-08) y cuenta del portal del dueño (D13B-09). Anónimas por
/// diseño, con la clave comparada en tiempo constante, límite por IP y por licencia, y bloqueo del usuario del portal por intentos.
/// </summary>
public class InstallerApiTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    private static async Task<(HttpStatusCode Status, string? Code, string Body)> PostAsync(HttpClient client, string route, object body)
    {
        using var response = await client.PostAsJsonAsync(route, body, PortalApi.Json, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, response.IsSuccessStatusCode ? null : await PortalApi.ErrorCodeAsync(response), text);
    }

    [Fact]
    public async Task La_vista_previa_devuelve_solo_los_datos_publicos_de_una_clave_vigente()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var client = cloud.Factory.CreateClient();

        // Se acepta la clave como la escribe el cliente (minúsculas, sin guiones).
        var typed = customer.License.Key.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal);
        var (status, _, body) = await PostAsync(client, LicensingRoutes.LicensePreviews, new LicensePreviewRequest(typed));

        status.ShouldBe(HttpStatusCode.OK, body);
        var preview = JsonSerializer.Deserialize<LicensePreviewResponse>(body, PortalApi.Json)!;
        preview.Nit.ShouldBe(customer.Nit);
        preview.LegalName.ShouldStartWith("Supermercado ");
        preview.Edition.ShouldBe(LicenseEditions.MultiTerminal);
        preview.SubscriptionStatus.ShouldBe(SubscriptionStatuses.Trial);
        preview.ValidUntil.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        preview.City.ShouldBe("Bogotá");

        // Sin secretos: ni ids, ni la clave, ni contactos; solo estos campos.
        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["city", "edition", "legalName", "nit", "subscriptionStatus", "validUntil"]);
        body.ShouldNotContain(customer.License.Key, Case.Insensitive);
        body.ShouldNotContain(customer.OrganizationId.ToString(), Case.Insensitive);
        body.ShouldNotContain("contacto@cliente.co", Case.Insensitive);
    }

    [Fact]
    public async Task La_vista_previa_rechaza_claves_invalidas_revocadas_y_suscripciones_suspendidas_con_codigo_estable()
    {
        var client = cloud.Factory.CreateClient();
        (await PostAsync(client, LicensingRoutes.LicensePreviews, new LicensePreviewRequest("POS-123"))).Code.ShouldBe(LicenseErrorCodes.KeyFormatInvalid);

        var unknown = await PostAsync(client, LicensingRoutes.LicensePreviews, new LicensePreviewRequest(LicenseKey.Generate()));
        unknown.Status.ShouldBe(HttpStatusCode.Forbidden);
        unknown.Code.ShouldBe(LicenseErrorCodes.KeyInvalid);

        // Clave regenerada: la anterior queda revocada y responde EXACTAMENTE igual que una inexistente.
        var revoked = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        await PortalApi.PostAsync<GeneratedLicenseDtoShape>(cloud.Superadmin, $"/admin/licenses/{revoked.License.LicenseId}/regenerate",
            new { reason = "Clave filtrada por el cliente" });
        var old = await PostAsync(client, LicensingRoutes.LicensePreviews, new LicensePreviewRequest(revoked.License.Key));
        (old.Status, old.Code).ShouldBe((unknown.Status, unknown.Code));
        Detail(old.Body).ShouldBe(Detail(unknown.Body));

        var suspended = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{suspended.SubscriptionId}/suspend",
            new { reason = "Pago rechazado por el banco" })).Code.ShouldBeNull();
        (await PostAsync(client, LicensingRoutes.LicensePreviews, new LicensePreviewRequest(suspended.License.Key))).Code
            .ShouldBe(LicenseErrorCodes.SubscriptionInactive);
    }

    [Fact]
    public async Task La_cuenta_del_dueno_entrega_un_enlace_firmado_que_el_POS_verifica_sin_conexion()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, owner) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", customer.AccountId);
        var client = cloud.Factory.CreateClient();

        // Tiene el doble factor activo: sin el código no hay enlace (y no cuenta como intento fallido).
        (await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(customer.License.Key, owner.Email, owner.Password))).Code
            .ShouldBe(LicenseErrorCodes.OwnerTotpRequired);

        var (status, _, body) = await PostAsync(client, LicensingRoutes.OwnerLinks,
            new OwnerLinkRequest(customer.License.Key, owner.Email.ToUpperInvariant(), owner.Password, owner.NextCode()));

        status.ShouldBe(HttpStatusCode.OK, body);
        var link = JsonSerializer.Deserialize<OwnerLinkResponse>(body, PortalApi.Json)!;
        link.Email.ShouldBe(owner.Email);
        link.DisplayName.ShouldBe("Usuario CUSTOMER");
        body.ShouldNotContain(owner.Password, Case.Sensitive);

        var verification = OwnerLinkToken.Verify(link.Token, new LicenseKeyRing([cloud.Factory.PublicKey]), DateTimeOffset.UtcNow);
        verification.Status.ShouldBe(OwnerLinkStatus.Valid);
        verification.Claims!.OrganizationNit.ShouldBe(customer.Nit);
        verification.Claims.Email.ShouldBe(owner.Email);
        OwnerLinkToken.Verify(link.Token, new LicenseKeyRing([cloud.Factory.PublicKey]), DateTimeOffset.UtcNow.AddHours(3)).Status
            .ShouldBe(OwnerLinkStatus.Expired);

        // No es un token de licencia (tipo distinto).
        LicenseToken.Verify(link.Token, new LicenseKeyRing([cloud.Factory.PublicKey])).IsValid.ShouldBeFalse();

        // Queda en la auditoría de la nube.
        var audit = await PortalApi.GetAsync<List<Pos.Cloud.Abstractions.CloudAuditEntryDto>>(cloud.Superadmin, "/admin/audit?action=OWNER_LINK_ISSUED");
        audit.ShouldContain(a => (a.Summary ?? string.Empty).Contains(owner.Email, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Solo_el_cliente_de_la_cuenta_duena_y_con_la_contrasena_correcta_obtiene_el_enlace()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var other = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, stranger) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", other.AccountId);
        var client = cloud.Factory.CreateClient();

        // Cliente de OTRA cuenta (contraseña y código correctos): no es el dueño de esta clave.
        var foreign = await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(customer.License.Key, stranger.Email, stranger.Password, stranger.NextCode()));
        foreign.Status.ShouldBe(HttpStatusCode.Forbidden);
        foreign.Code.ShouldBe(LicenseErrorCodes.OwnerNotLinked);

        // El superadministrador tampoco: solo un usuario Cliente.
        (await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(customer.License.Key, cloud.SuperadminUser.Email, cloud.SuperadminUser.Password)))
            .Code.ShouldBe(LicenseErrorCodes.OwnerNotLinked);

        // Contraseña incorrecta y correo inexistente: la misma respuesta.
        var wrong = await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(other.License.Key, stranger.Email, "Otra-Clave-2026"));
        var nobody = await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(other.License.Key, "nadie@cliente.co", "Otra-Clave-2026"));
        (wrong.Status, wrong.Code).ShouldBe((HttpStatusCode.Unauthorized, LicenseErrorCodes.OwnerCredentialsInvalid));
        (nobody.Status, nobody.Code).ShouldBe((wrong.Status, wrong.Code));

        // Con una clave inválida ni siquiera se revisa la cuenta.
        (await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(LicenseKey.Generate(), stranger.Email, stranger.Password))).Code
            .ShouldBe(LicenseErrorCodes.KeyInvalid);
    }

    [Fact]
    public async Task Los_intentos_fallidos_bloquean_la_cuenta_del_portal()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, owner) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", customer.AccountId);
        var client = cloud.Factory.CreateClient();

        var codes = new List<string?>();
        for (var i = 0; i < 5; i++)
        {
            codes.Add((await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(customer.License.Key, owner.Email, $"Incorrecta-{i}-2026"))).Code);
        }

        codes.Take(4).ShouldAllBe(c => c == LicenseErrorCodes.OwnerCredentialsInvalid);
        codes[4].ShouldBe(LicenseErrorCodes.OwnerLocked);

        // Bloqueado: ni con la contraseña correcta, ni en el portal.
        (await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(customer.License.Key, owner.Email, owner.Password, owner.NextCode()))).Code
            .ShouldBe(LicenseErrorCodes.OwnerLocked);
        (await PortalApi.SendAsync(cloud.Factory.CreateClient(), HttpMethod.Post, "/admin/auth/login", new { email = owner.Email, password = owner.Password })).Code
            .ShouldBe("PORTAL.USER_LOCKED");
    }

    [Fact]
    public async Task La_vista_previa_y_la_cuenta_respetan_el_limite_por_licencia_y_por_IP()
    {
        await using (var factory = new LowLicenseLimitCloudServerFactory())
        {
            await factory.StartAsync();
            var (superadmin, _) = await PortalApi.BootstrapSuperadminAsync(factory);
            var customer = await LicensedCustomer.CreateAsync(superadmin);
            var client = factory.CreateClient();
            for (var i = 0; i < 3; i++)
            {
                (await PostAsync(client, LicensingRoutes.LicensePreviews, new LicensePreviewRequest(customer.License.Key))).Status.ShouldBe(HttpStatusCode.OK);
            }

            var limited = await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(customer.License.Key, "dueno@cliente.co", "Clave-Segura-2026"));
            limited.Status.ShouldBe(HttpStatusCode.TooManyRequests);
            limited.Code.ShouldBe(LicenseErrorCodes.TooManyRequests);
        }

        await using (var factory = new LowIpLimitCloudServerFactory())
        {
            var client = await factory.StartAsync();
            for (var i = 0; i < 3; i++)
            {
                (await PostAsync(client, LicensingRoutes.LicensePreviews, new LicensePreviewRequest(LicenseKey.Generate()))).Status.ShouldBe(HttpStatusCode.Forbidden);
            }

            var limited = await PostAsync(client, LicensingRoutes.OwnerLinks, new OwnerLinkRequest(LicenseKey.Generate(), "dueno@cliente.co", "Clave-Segura-2026"));
            limited.Status.ShouldBe(HttpStatusCode.TooManyRequests);
            limited.Code.ShouldBe("SECURITY.TOO_MANY_REQUESTS");
        }
    }

    private static string? Detail(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("detail").GetString();
    }

    /// <summary>Respuesta de regenerar (solo para deserializar).</summary>
    private sealed record GeneratedLicenseDtoShape(string Key);
}
