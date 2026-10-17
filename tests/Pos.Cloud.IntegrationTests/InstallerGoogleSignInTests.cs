using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Cloud.Abstractions;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>Reloj del servidor que la prueba adelanta (intervalos de consulta y vencimiento de la solicitud).</summary>
public sealed class ShiftableTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
}

/// <summary>Servidor con el reloj adelantable y el doble factor opcional (el dueño entra al portal solo con su contraseña).</summary>
public sealed class ShiftableClockCloudServerFactory : CloudServerFactory
{
    public ShiftableTimeProvider Clock { get; } = new();

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Portal:RequireTotp", "false")];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }
}

public sealed class ShiftableClockCloudFixture : IAsyncLifetime
{
    public ShiftableClockCloudServerFactory Factory { get; } = new();

    public HttpClient Superadmin { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Factory.StartAsync();
        (Superadmin, _) = await PortalApi.BootstrapSuperadminAsync(Factory);
    }

    public async ValueTask DisposeAsync()
    {
        Superadmin?.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>
/// "Continuar con Google" del instalador premium (Fase 13-B, parte E): como OAuth 2.0 Device Authorization. El instalador crea la
/// solicitud con la clave, el dueño (usuario CLIENTE de la cuenta dueña) la autoriza en el portal con su sesión normal y el instalador
/// recoge UNA vez el mismo enlace firmado de <c>/v1/owner-links</c>. Vence, limita las consultas (slow_down) y no se reutiliza.
/// </summary>
public class InstallerGoogleSignInTests(ShiftableClockCloudFixture cloud) : IClassFixture<ShiftableClockCloudFixture>
{
    private static readonly Uri Base = new("https://licencias.test/");

    /// <summary>Un poco más que el intervalo de 5 s entre consultas.</summary>
    private static readonly TimeSpan NextPoll = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task Crear_la_solicitud_devuelve_la_pagina_del_portal_y_guarda_solo_el_hash_del_secreto()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var client = cloud.Factory.CreateClient();

        var (status, code, body) = await PostAsync(client, LicensingRoutes.OwnerLinkRequests, new OwnerLinkRequestStart(customer.License.Key.ToLowerInvariant()));
        status.ShouldBe(HttpStatusCode.OK, body);
        code.ShouldBeNull();
        var started = JsonSerializer.Deserialize<OwnerLinkRequestStarted>(body, PortalApi.Json)!;
        started.BrowserUrl.ShouldEndWith($"/cuenta/vincular-instalador?r={started.RequestId:D}");
        started.UserCode.ShouldMatch("^[A-Z0-9]{4}-[A-Z0-9]{4}$");
        started.PollToken.Length.ShouldBe(43);
        started.ExpiresIn.ShouldBe(300, "Vence rápido: un enlace enviado por otra persona deja de servir enseguida.");
        started.Interval.ShouldBe(5);
        body.ShouldNotContain(customer.License.Key, Case.Insensitive);
        body.ShouldNotContain(customer.OrganizationId.ToString(), Case.Insensitive);

        // En la BD: el hash SHA-256 del secreto, nunca el secreto.
        await using (var connection = new NpgsqlConnection(cloud.Factory.Database.SuperuserConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT poll_token_hash, status, user_code FROM licensing.owner_link_requests WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", started.RequestId);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            reader.GetString(0).ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(started.PollToken))));
            reader.GetString(1).ShouldBe("PENDING");
            reader.GetString(2).ShouldBe(started.UserCode);
        }

        // Clave inválida: la misma respuesta que la vista previa.
        (await PostAsync(client, LicensingRoutes.OwnerLinkRequests, new OwnerLinkRequestStart(LicenseKey.Generate()))).Code.ShouldBe(LicenseErrorCodes.KeyInvalid);

        // Como máximo 5 solicitudes abiertas por licencia (la clave filtrada no llena la tabla).
        for (var i = 0; i < 4; i++)
        {
            (await PostAsync(client, LicensingRoutes.OwnerLinkRequests, new OwnerLinkRequestStart(customer.License.Key))).Status.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await PostAsync(client, LicensingRoutes.OwnerLinkRequests, new OwnerLinkRequestStart(customer.License.Key));
        (limited.Status, limited.Code).ShouldBe((HttpStatusCode.TooManyRequests, LicenseErrorCodes.TooManyRequests));
    }

    [Fact]
    public async Task El_dueno_autoriza_en_el_navegador_y_el_instalador_recibe_una_sola_vez_el_mismo_enlace_firmado()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, owner) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", customer.AccountId);
        var installer = cloud.Factory.CreateClient();
        var started = await StartAsync(installer, customer);

        (await PollAsync(installer, started)).Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Pending);

        // Sin sesión, la página lleva al ingreso normal del portal y vuelve a ella después.
        using var browser = cloud.Factory.CreateDefaultClient(Base, new CookieContainerHandler());
        var page = new Uri(new Uri(started.BrowserUrl).PathAndQuery.TrimStart('/'), UriKind.Relative);
        using (var anonymous = await browser.GetAsync(page, Ct))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            anonymous.Headers.Location!.ToString().ShouldContain($"cuenta/ingresar?returnUrl={Uri.EscapeDataString($"/cuenta/vincular-instalador?r={started.RequestId:D}")}");
        }

        await SignInAsync(browser, owner, $"/cuenta/vincular-instalador?r={started.RequestId:D}");
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync(page, Ct));
        html.ShouldContain($"Supermercado {customer.Nit.Split('-')[0]} SAS");
        html.ShouldNotContain(started.UserCode, customMessage: "El dueño ESCRIBE el código de su instalador: la página no lo muestra.");
        html.ShouldContain("Solicitud creada hace");
        html.ShouldContain("Autorizar");
        html.ShouldNotContain("aplicación autenticadora", customMessage: "Sin TOTP activo no se pide el código del autenticador.");

        // Sin el código (o con otro): no autoriza y sigue pendiente.
        foreach (var wrong in new string?[] { null, "ZZZZ-ZZZZ" })
        {
            using var rejected = await BrowserForms.PostFormAsync(browser, page, html, new() { ["Input.Decision"] = "autorizar", ["Input.Code"] = wrong ?? string.Empty });
            WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync(Ct)).ShouldContain("El código no coincide");
        }

        cloud.Factory.Clock.Advance(NextPoll);
        (await PollAsync(installer, started)).Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Pending);

        // Escrito en minúsculas y sin guion: se normaliza.
        var typed = started.UserCode.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant();
        using (var approved = await BrowserForms.PostFormAsync(browser, page, html, new() { ["Input.Decision"] = "autorizar", ["Input.Code"] = typed }))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK);
            WebUtility.HtmlDecode(await approved.Content.ReadAsStringAsync(Ct)).ShouldContain("la instalación quedó autorizada");
        }

        cloud.Factory.Clock.Advance(NextPoll);
        var (status, poll, _) = await PollAsync(installer, started);
        status.ShouldBe(HttpStatusCode.OK);
        poll!.Status.ShouldBe(OwnerLinkRequestStatuses.Approved);
        var link = poll.Link!;
        link.Email.ShouldBe(owner.Email);
        link.DisplayName.ShouldBe("Usuario CUSTOMER");

        // El MISMO enlace de /v1/owner-links: tipo pos-owner-link+jws firmado con la clave de las licencias, que el POS verifica sin conexión.
        var header = JsonDocument.Parse(Base64UrlDecode(link.Token.Split('.')[0])).RootElement;
        header.GetProperty("typ").GetString().ShouldBe(OwnerLinkToken.Type);
        header.GetProperty("alg").GetString().ShouldBe("EdDSA");
        var verification = OwnerLinkToken.Verify(link.Token, new LicenseKeyRing([cloud.Factory.PublicKey]), cloud.Factory.Clock.GetUtcNow());
        verification.Status.ShouldBe(OwnerLinkStatus.Valid);
        verification.Claims!.OrganizationNit.ShouldBe(customer.Nit);
        verification.Claims.Email.ShouldBe(owner.Email);
        LicenseToken.Verify(link.Token, new LicenseKeyRing([cloud.Factory.PublicKey])).IsValid.ShouldBeFalse();

        // Un solo uso: repetir la consulta (o reenviar el secreto) ya no entrega nada.
        cloud.Factory.Clock.Advance(NextPoll);
        var replay = await PollAsync(installer, started);
        (replay.Status, replay.Code).ShouldBe((HttpStatusCode.Conflict, LicenseErrorCodes.OwnerLinkRequestUsed));
        WebUtility.HtmlDecode(await browser.GetStringAsync(page, Ct)).ShouldContain("la instalación quedó autorizada");

        var approvedAudit = await PortalApi.GetAsync<List<CloudAuditEntryDto>>(cloud.Superadmin, "/admin/audit?action=OWNER_LINK_REQUEST_APPROVED");
        approvedAudit.ShouldContain(a => (a.Summary ?? string.Empty).Contains(owner.Email, StringComparison.Ordinal) && a.Summary!.Contains(started.UserCode, StringComparison.Ordinal));
        var issuedAudit = await PortalApi.GetAsync<List<CloudAuditEntryDto>>(cloud.Superadmin, "/admin/audit?action=OWNER_LINK_ISSUED");
        issuedAudit.ShouldContain(a => (a.Summary ?? string.Empty).Contains("Continuar con Google", StringComparison.Ordinal) && a.Summary!.Contains(owner.Email, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_cliente_de_otra_cuenta_no_ve_la_empresa_ni_puede_autorizar()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var other = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, stranger) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", other.AccountId);
        var installer = cloud.Factory.CreateClient();
        var started = await StartAsync(installer, customer);

        using var browser = cloud.Factory.CreateDefaultClient(Base, new CookieContainerHandler());
        var page = new Uri($"cuenta/vincular-instalador?r={started.RequestId:D}", UriKind.Relative);
        await SignInAsync(browser, stranger, page.OriginalString.Insert(0, "/"));
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync(page, Ct));
        html.ShouldContain("No encontramos una solicitud del instalador para su usuario");
        html.ShouldNotContain($"Supermercado {customer.Nit.Split('-')[0]}");
        html.ShouldNotContain(started.UserCode);

        // La MISMA respuesta que una solicitud inexistente: no se distingue "existe pero es de otro".
        var missing = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri($"cuenta/vincular-instalador?r={Guid.CreateVersion7():D}", UriKind.Relative), Ct));
        AlertOf(missing).ShouldBe(AlertOf(html));

        // Abrir la página (GET) no escribe en la auditoría.
        (await RejectedAuditAsync(stranger.Email)).ShouldBe(0);

        // Aunque envíe el formulario a mano (con un token antifalsificación válido y el código correcto), no autoriza.
        using (var forged = await BrowserForms.PostFormAsync(browser, page, html, new()
               {
                   ["_handler"] = "vincular-instalador",
                   ["Input.Decision"] = "autorizar",
                   ["Input.Code"] = started.UserCode,
               }))
        {
            forged.StatusCode.ShouldBe(HttpStatusCode.OK);
            WebUtility.HtmlDecode(await forged.Content.ReadAsStringAsync(Ct)).ShouldContain("No encontramos una solicitud del instalador para su usuario");
        }

        cloud.Factory.Clock.Advance(NextPoll);
        (await PollAsync(installer, started)).Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Pending);
        (await RejectedAuditAsync(stranger.Email)).ShouldBe(1, "Solo el POST de decisión queda en la auditoría.");
    }

    [Fact]
    public async Task Cinco_codigos_incorrectos_cancelan_la_solicitud()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, owner) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", customer.AccountId);
        var installer = cloud.Factory.CreateClient();
        var started = await StartAsync(installer, customer);

        using var browser = cloud.Factory.CreateDefaultClient(Base, new CookieContainerHandler());
        var page = new Uri($"cuenta/vincular-instalador?r={started.RequestId:D}", UriKind.Relative);
        await SignInAsync(browser, owner, "/" + page.OriginalString);
        var html = await browser.GetStringAsync(page, Ct);
        for (var i = 1; i <= 5; i++)
        {
            using var wrong = await BrowserForms.PostFormAsync(browser, page, html, new() { ["Input.Decision"] = "autorizar", ["Input.Code"] = "ZZZZ-ZZZZ" });
            var text = WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync(Ct));
            text.ShouldContain(i < 5 ? $"le quedan {5 - i} intentos" : "Solicitud cancelada");
        }

        // Ni con el código correcto después.
        using (var late = await BrowserForms.PostFormAsync(browser, page, html, new() { ["Input.Decision"] = "autorizar", ["Input.Code"] = started.UserCode }))
        {
            WebUtility.HtmlDecode(await late.Content.ReadAsStringAsync(Ct)).ShouldContain("Solicitud cancelada");
        }

        (await PollAsync(installer, started)).Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Denied);
    }

    [Fact]
    public async Task Con_un_ingreso_antiguo_pide_ingresar_de_nuevo_antes_de_autorizar()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, owner) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", customer.AccountId);
        using var browser = cloud.Factory.CreateDefaultClient(Base, new CookieContainerHandler());
        await SignInAsync(browser, owner, "/cuenta/vincular-instalador?r=" + Guid.Empty.ToString("D"));

        // La sesión sigue abierta (se usa cada pocos minutos), pero el ingreso fue hace más de 10 minutos.
        for (var i = 0; i < 3; i++)
        {
            cloud.Factory.Clock.Advance(TimeSpan.FromMinutes(4));
            (await browser.GetAsync(new Uri("cuenta/vincular-instalador?r=x", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var installer = cloud.Factory.CreateClient();
        var started = await StartAsync(installer, customer);
        var page = new Uri($"cuenta/vincular-instalador?r={started.RequestId:D}", UriKind.Relative);
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync(page, Ct));
        html.ShouldContain("Ingresar de nuevo");
        html.ShouldNotContain("name=\"Input.Decision\" value=\"autorizar\"");
        using (var forced = await BrowserForms.PostFormAsync(browser, page, html, new()
               {
                   ["_handler"] = "vincular-instalador", ["Input.Decision"] = "autorizar", ["Input.Code"] = started.UserCode,
               }))
        {
            WebUtility.HtmlDecode(await forced.Content.ReadAsStringAsync(Ct)).ShouldContain("Ingresar de nuevo");
        }

        (await PollAsync(installer, started)).Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Pending);
    }

    [Fact]
    public async Task Si_la_suscripcion_se_suspende_antes_de_recoger_no_se_entrega_el_enlace()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, owner) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", customer.AccountId);
        var installer = cloud.Factory.CreateClient();
        var started = await StartAsync(installer, customer);
        using var browser = cloud.Factory.CreateDefaultClient(Base, new CookieContainerHandler());
        var page = new Uri($"cuenta/vincular-instalador?r={started.RequestId:D}", UriKind.Relative);
        await SignInAsync(browser, owner, "/" + page.OriginalString);
        var html = await browser.GetStringAsync(page, Ct);
        using (var approved = await BrowserForms.PostFormAsync(browser, page, html, new() { ["Input.Decision"] = "autorizar", ["Input.Code"] = started.UserCode }))
        {
            WebUtility.HtmlDecode(await approved.Content.ReadAsStringAsync(Ct)).ShouldContain("la instalación quedó autorizada");
        }

        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{customer.SubscriptionId}/suspend",
            new { reason = "Pago rechazado por el banco" })).Code.ShouldBeNull();
        var poll = await PollAsync(installer, started);
        poll.Code.ShouldBe(LicenseErrorCodes.SubscriptionInactive);
    }

    /// <summary>N2: con un ingreso de hace más de 10 minutos, restablecer a otro usuario pide confirmar con la contraseña (o el TOTP).</summary>
    [Fact]
    public async Task Restablecer_a_otro_usuario_con_un_ingreso_antiguo_pide_confirmar_la_identidad()
    {
        var (_, support) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "SUPPORT");
        var id = (await PortalApi.GetAsync<List<Pos.Cloud.PortalIdentity.Application.PortalUserDto>>(cloud.Superadmin, "/admin/users")).Single(u => u.Email == support.Email).Id;
        using var admin = await PortalApi.SignInAsync(cloud.Factory, new PortalCredentials("dueno@licencias.co", PortalApi.NewPassword));
        var url = $"/admin/users/{id}/reset-password";

        for (var i = 0; i < 3; i++)
        {
            cloud.Factory.Clock.Advance(TimeSpan.FromMinutes(4));
            (await PortalApi.SendAsync(admin, HttpMethod.Get, "/admin/users")).Status.ShouldBe(HttpStatusCode.OK, "La sesión sigue abierta.");
        }

        (await PortalApi.SendAsync(admin, HttpMethod.Post, url)).Code.ShouldBe("PORTAL.REAUTH_REQUIRED");
        (await PortalApi.SendAsync(admin, HttpMethod.Post, url, new { confirmation = "otra-contrasena-123" })).Code.ShouldBe("PORTAL.INVALID_CONFIRMATION");
        (await PortalApi.SendAsync(admin, HttpMethod.Post, url, new { confirmation = PortalApi.NewPassword })).Status.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<int> RejectedAuditAsync(string email) =>
        (await PortalApi.GetAsync<List<CloudAuditEntryDto>>(cloud.Superadmin, $"/admin/audit?action=PORTAL_OWNER_LINK_REJECTED&text={Uri.EscapeDataString(email)}")).Count;

    /// <summary>El texto del aviso de la página (sin el token antifalsificación, que cambia en cada respuesta).</summary>
    private static string AlertOf(string html)
    {
        var start = html.IndexOf("No encontramos", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        return html[start..html.IndexOf('<', start)];
    }

    [Fact]
    public async Task Cancelar_en_el_portal_rechaza_la_solicitud()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, owner) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", customer.AccountId);
        var installer = cloud.Factory.CreateClient();
        var started = await StartAsync(installer, customer);

        using var browser = cloud.Factory.CreateDefaultClient(Base, new CookieContainerHandler());
        var page = new Uri($"cuenta/vincular-instalador?r={started.RequestId:D}", UriKind.Relative);
        await SignInAsync(browser, owner, "/" + page.OriginalString);
        var html = await browser.GetStringAsync(page, Ct);
        using (var denied = await BrowserForms.PostFormAsync(browser, page, html, new() { ["Input.Decision"] = "cancelar" }))
        {
            WebUtility.HtmlDecode(await denied.Content.ReadAsStringAsync(Ct)).ShouldContain("Solicitud cancelada");
        }

        (await PollAsync(installer, started)).Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Denied);

        // Ya decidida: otro "Autorizar" (pestaña vieja) no la cambia.
        using (var late = await BrowserForms.PostFormAsync(browser, page, html, new() { ["Input.Decision"] = "autorizar" }))
        {
            WebUtility.HtmlDecode(await late.Content.ReadAsStringAsync(Ct)).ShouldContain("Solicitud cancelada");
        }

        cloud.Factory.Clock.Advance(NextPoll);
        (await PollAsync(installer, started)).Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Denied);
    }

    [Fact]
    public async Task La_solicitud_vence_a_los_5_minutos_y_ya_no_se_puede_autorizar()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, owner) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", customer.AccountId);
        var installer = cloud.Factory.CreateClient();
        var started = await StartAsync(installer, customer);

        using var browser = cloud.Factory.CreateDefaultClient(Base, new CookieContainerHandler());
        var page = new Uri($"cuenta/vincular-instalador?r={started.RequestId:D}", UriKind.Relative);
        await SignInAsync(browser, owner, "/" + page.OriginalString);
        var html = await browser.GetStringAsync(page, Ct);

        cloud.Factory.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        (await PollAsync(installer, started)).Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Expired);
        using (var late = await BrowserForms.PostFormAsync(browser, page, html, new() { ["Input.Decision"] = "autorizar" }))
        {
            WebUtility.HtmlDecode(await late.Content.ReadAsStringAsync(Ct)).ShouldContain("La solicitud venció");
        }

        cloud.Factory.Clock.Advance(NextPoll);
        (await PollAsync(installer, started)).Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Expired);
    }

    [Fact]
    public async Task Consultar_demasiado_seguido_responde_slow_down_y_un_secreto_ajeno_no_existe()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var installer = cloud.Factory.CreateClient();
        var started = await StartAsync(installer, customer);

        (await PollAsync(installer, started)).Response!.Interval.ShouldBe(5);
        var fast = await PollAsync(installer, started);
        (fast.Status, fast.Code).ShouldBe((HttpStatusCode.TooManyRequests, LicenseErrorCodes.OwnerLinkSlowDown));

        // El intervalo creció a 10 s: a los 6 s sigue siendo demasiado pronto (y crece a 15 s).
        cloud.Factory.Clock.Advance(NextPoll);
        (await PollAsync(installer, started)).Code.ShouldBe(LicenseErrorCodes.OwnerLinkSlowDown);
        cloud.Factory.Clock.Advance(TimeSpan.FromSeconds(16));
        var calm = await PollAsync(installer, started);
        calm.Response!.Status.ShouldBe(OwnerLinkRequestStatuses.Pending);
        calm.Response.Interval.ShouldBe(15);

        // Secreto de otra solicitud, inventado o id inexistente: la misma respuesta (no se confirma que exista).
        var another = await StartAsync(installer, customer);
        var foreign = await PostAsync(installer, LicensingRoutes.OwnerLinkRequestPoll(started.RequestId), new OwnerLinkPoll(another.PollToken));
        var invented = await PostAsync(installer, LicensingRoutes.OwnerLinkRequestPoll(Guid.CreateVersion7()), new OwnerLinkPoll(started.PollToken));
        var malformed = await PostAsync(installer, LicensingRoutes.OwnerLinkRequestPoll(started.RequestId), new OwnerLinkPoll("x"));
        foreach (var rejected in new[] { foreign, invented, malformed })
        {
            (rejected.Status, rejected.Code).ShouldBe((HttpStatusCode.NotFound, LicenseErrorCodes.OwnerLinkRequestNotFound));
        }
    }

    private static async Task<OwnerLinkRequestStarted> StartAsync(HttpClient client, LicensedCustomer customer)
    {
        var (status, _, body) = await PostAsync(client, LicensingRoutes.OwnerLinkRequests, new OwnerLinkRequestStart(customer.License.Key));
        status.ShouldBe(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<OwnerLinkRequestStarted>(body, PortalApi.Json)!;
    }

    private static async Task<(HttpStatusCode Status, OwnerLinkPollResponse? Response, string? Code)> PollAsync(HttpClient client, OwnerLinkRequestStarted started)
    {
        var (status, code, body) = await PostAsync(client, LicensingRoutes.OwnerLinkRequestPoll(started.RequestId), new OwnerLinkPoll(started.PollToken));
        return (status, status == HttpStatusCode.OK ? JsonSerializer.Deserialize<OwnerLinkPollResponse>(body, PortalApi.Json) : null, code);
    }

    private static async Task<(HttpStatusCode Status, string? Code, string Body)> PostAsync(HttpClient client, string route, object body)
    {
        using var response = await client.PostAsJsonAsync(route, body, PortalApi.Json, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, response.IsSuccessStatusCode ? null : await PortalApi.ErrorCodeAsync(response), text);
    }

    /// <summary>Ingreso normal del portal en el navegador (contraseña; el doble factor es opcional en este servidor) y vuelta a la página.</summary>
    private static async Task SignInAsync(HttpClient browser, PortalCredentials user, string returnUrl)
    {
        var login = new Uri($"cuenta/ingresar?returnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative);
        var page = await browser.GetStringAsync(login, Ct);
        using var posted = await BrowserForms.PostFormAsync(browser, login, page, new() { ["Input.Email"] = user.Email, ["Input.Password"] = user.Password });
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect, await posted.Content.ReadAsStringAsync(Ct));
        posted.Headers.Location!.ToString().ShouldEndWith(returnUrl);
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
