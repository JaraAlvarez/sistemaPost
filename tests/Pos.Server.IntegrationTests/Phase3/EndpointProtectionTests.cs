using System.Net;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase3;

/// <summary>Criterio de aceptación: ningún endpoint de negocio responde sin sesión (401) ni sin permiso (403).</summary>
public class EndpointProtectionTests
{
    [Fact]
    public async Task Todo_endpoint_protegido_responde_401_sin_sesion()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var anonymous = factory.CreateClient();

        var unexpected = new List<string>();
        foreach (var (method, url, _) in ProtectedEndpoints(factory))
        {
            var response = await anonymous.SendAsync(Request(method, url), Ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || await ErrorCodeAsync(response) != EndpointSecurity.AuthenticationRequiredCode)
            {
                unexpected.Add($"{method} {url} → {(int)response.StatusCode}");
            }
        }

        unexpected.ShouldBeEmpty();
        scenario.Setup.OwnerUserId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Todo_endpoint_con_permiso_responde_403_a_un_usuario_sin_ese_permiso()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("cajero.sin.permisos", "CASHIER");
        var cashier = await scenario.LocalClientAsync("cajero.sin.permisos");

        var unexpected = new List<string>();
        foreach (var (method, url, permission) in ProtectedEndpoints(factory).Where(e => e.Permission is not null))
        {
            var response = await cashier.SendAsync(Request(method, url), Ct);
            if (response.StatusCode != HttpStatusCode.Forbidden || await ErrorCodeAsync(response) != EndpointSecurity.ForbiddenCode)
            {
                unexpected.Add($"{method} {url} ({permission}) → {(int)response.StatusCode}");
            }
        }

        unexpected.ShouldBeEmpty();
    }

    private static IEnumerable<(string Method, string Url, string? Permission)> ProtectedEndpoints(PosServerFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<PermissionRequirement>() is not null || e.Metadata.GetMetadata<AuthenticatedOnly>() is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m =>
                (m, Url(e.RoutePattern.RawText!), e.Metadata.GetMetadata<PermissionRequirement>()?.PermissionCode)));

    private static string Url(string pattern)
    {
        // Sustituye los parámetros de ruta ({id:guid}, {key}) por valores válidos.
        var builder = new StringBuilder();
        var inside = false;
        var parameter = new StringBuilder();
        foreach (var c in pattern)
        {
            if (c == '{')
            {
                inside = true;
                parameter.Clear();
            }
            else if (c == '}')
            {
                inside = false;
                builder.Append(parameter.ToString().Contains(":guid", StringComparison.Ordinal) ? Guid.CreateVersion7().ToString() : "valor");
            }
            else if (inside)
            {
                parameter.Append(c);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static HttpRequestMessage Request(string method, string url) => new(new HttpMethod(method), url)
    {
        Content = method is "POST" or "PUT" ? new StringContent("{}", Encoding.UTF8, "application/json") : null,
    };
}
