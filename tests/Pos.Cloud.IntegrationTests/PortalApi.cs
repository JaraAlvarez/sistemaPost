using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.PortalIdentity.Application;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Fiscal;

namespace Pos.Cloud.IntegrationTests;

/// <summary>Código TOTP calculado como lo hace una aplicación autenticadora (RFC 6238: HMAC-SHA1, 6 dígitos, 30 s).</summary>
public static class Authenticator
{
    public static byte[] FromBase32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }

    public static long CurrentStep => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    public static string Code(byte[] secret, long step)
    {
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
#pragma warning disable CA5350 // RFC 6238 fija HMAC-SHA1.
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}

/// <summary>Usuario del portal con su secreto TOTP (como si lo tuviera en el teléfono) y el último paso usado.</summary>
public sealed class PortalCredentials(string email, string password)
{
    public string Email { get; } = email;

    public string Password { get; set; } = password;

    public byte[]? TotpSecret { get; set; }

    public long LastStep { get; set; } = -1;

    /// <summary>Un código que el servidor aún no ha visto (el mismo código no sirve dos veces).</summary>
    public string NextCode()
    {
        var step = Math.Max(Authenticator.CurrentStep, LastStep + 1);
        step.ShouldBeLessThanOrEqualTo(Authenticator.CurrentStep + 1, "Demasiados ingresos del mismo usuario en 30 s.");
        LastStep = step;
        return Authenticator.Code(TotpSecret!, step);
    }
}

/// <summary>Acceso a la API interna /admin como lo haría una herramienta del propietario: contraseña + TOTP → Bearer.</summary>
public static class PortalApi
{
    public const string NewPassword = "Clave-Segura-2026";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Crea el primer superadministrador (consola <c>create-superadmin</c>) y devuelve su sesión lista, con contraseña cambiada.</summary>
    public static async Task<(HttpClient Client, PortalCredentials User)> BootstrapSuperadminAsync(CloudServerFactory factory, string email = "dueno@licencias.co")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var temporary = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new BootstrapSuperadminCommand(email, "Dueño del producto"));
        temporary.IsSuccess.ShouldBeTrue(temporary.IsFailure ? temporary.Error.Code : null);
        var user = new PortalCredentials(email, temporary.Value.TemporaryPassword);
        return (await SignInAsync(factory, user, changePassword: true), user);
    }

    /// <summary>El superadministrador crea un usuario (contraseña temporal); el usuario entra, enrola su TOTP y cambia la contraseña.</summary>
    public static async Task<(HttpClient Client, PortalCredentials User)> CreateUserAsync(
        CloudServerFactory factory, HttpClient superadmin, string role, Guid? resellerAccountId = null)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@licencias.co";
        var created = await PostAsync<TemporaryPasswordDto>(superadmin, "/admin/users", new { email, displayName = $"Usuario {role}", role, resellerAccountId });
        var user = new PortalCredentials(email, created.TemporaryPassword);
        return (await SignInAsync(factory, user, changePassword: true), user);
    }

    /// <summary>
    /// Paso 1 (contraseña) + enrolamiento del autenticador si hace falta + paso 2 (código) → cliente con Bearer. Con el doble factor
    /// opcional y sin TOTP activo, el paso 1 ya entrega la sesión activa.
    /// </summary>
    public static async Task<HttpClient> SignInAsync(CloudServerFactory factory, PortalCredentials user, bool changePassword = false)
    {
        var anonymous = factory.CreateClient();
        var challenge = await PostAsync<LoginChallengeDto>(anonymous, "/admin/auth/login", new { email = user.Email, password = user.Password });
        if (challenge.Stage == "ACTIVE")
        {
            // Doble factor opcional (Portal:RequireTotp=false) y sin TOTP activo: la contraseña basta.
            var active = factory.CreateClient();
            active.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", challenge.Token);
            if (changePassword)
            {
                await EnsureAsync(await active.PostAsJsonAsync("/admin/auth/password", new { currentPassword = user.Password, newPassword = NewPassword }), HttpStatusCode.NoContent);
                user.Password = NewPassword;
            }

            return active;
        }

        if (challenge.Stage == "ENROLLMENT_REQUIRED")
        {
            var enrollment = await PostAsync<TotpEnrollmentDto>(anonymous, "/admin/auth/totp/enrollment", new { token = challenge.Token });
            enrollment.EnrollmentLink.ShouldStartWith("otpauth://totp/");
            enrollment.EnrollmentLink.ShouldContain($"secret={enrollment.Secret}");
            user.TotpSecret = Authenticator.FromBase32(enrollment.Secret);
        }
        else
        {
            challenge.Stage.ShouldBe("TOTP_REQUIRED");
        }

        var session = await PostAsync<PortalSessionDto>(anonymous, "/admin/auth/totp", new { token = challenge.Token, code = user.NextCode() });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        if (changePassword && session.User.MustChangePassword)
        {
            await EnsureAsync(await client.PostAsJsonAsync("/admin/auth/password", new { currentPassword = user.Password, newPassword = NewPassword }), HttpStatusCode.NoContent);
            user.Password = NewPassword;
        }

        return client;
    }

    public static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Json);
        await EnsureSuccessAsync(response, url);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        await EnsureSuccessAsync(response, url);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task EnsureAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            throw new ShouldAssertException($"{response.RequestMessage?.RequestUri}: se esperaba {expected} y llegó {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    /// <summary>Código de error estable (<c>code</c> del ProblemDetails).</summary>
    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task<(HttpStatusCode Status, string? Code)> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        using var response = await client.SendAsync(request);
        return (response.StatusCode, response.IsSuccessStatusCode ? null : await ErrorCodeAsync(response));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string url)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ShouldAssertException($"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
    }
}

/// <summary>Un cliente listo para licenciar: cuenta → empresa (NIT) → suscripción → clave. Todo por la API interna.</summary>
public sealed record LicensedCustomer(Guid AccountId, Guid OrganizationId, Guid SubscriptionId, GeneratedLicenseDto License, string Nit)
{
    public static (string Number, string Dv) NewNit()
    {
        var number = RandomNumberGenerator.GetInt32(800_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture);
        return (number, Pos.SharedKernel.Fiscal.Nit.ComputeCheckDigit(number).ToString(CultureInfo.InvariantCulture));
    }

    public static async Task<LicensedCustomer> CreateAsync(
        HttpClient superadmin, string edition = LicenseEditions.MultiTerminal, bool trial = true, int? maxInstallations = null)
    {
        var (number, dv) = NewNit();
        var account = await PortalApi.PostAsync<Guid>(superadmin, "/admin/accounts", new AccountInput(
            $"Cliente {number}", "DIRECT", null, null, "Contacto", "contacto@cliente.co", "3000000000", null, null));
        var organization = await PortalApi.PostAsync<Guid>(superadmin, "/admin/organizations", new
        {
            accountId = account,
            legalName = $"Supermercado {number} SAS",
            nit = number,
            nitCheckDigit = dv,
            city = "Bogotá",
        });
        var subscription = await PortalApi.PostAsync<Guid>(superadmin, "/admin/subscriptions", new
        {
            organizationId = organization,
            edition,
            billingPeriod = "MONTHLY",
            trial,
            paymentReference = trial ? null : "TRANSFERENCIA-001",
            periods = 1,
        });
        var license = await PortalApi.PostAsync<GeneratedLicenseDto>(superadmin, "/admin/licenses", new { organizationId = organization, maxInstallations });
        return new LicensedCustomer(account, organization, subscription, license, $"{number}-{dv}");
    }
}
