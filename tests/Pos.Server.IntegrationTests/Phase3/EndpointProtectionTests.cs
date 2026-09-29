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
        // Un usuario sin ningún rol: cada endpoint con permiso debe negarle el acceso.
        await scenario.CreateUserAsync("usuario.sin.permisos", roleCode: null);
        var cashier = await scenario.LocalClientAsync("usuario.sin.permisos");

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

    [Theory]
    [InlineData("/api/v1/sales")]
    [InlineData("/api/v1/exchanges")]
    [InlineData("/api/v1/promotions")]
    [InlineData("/api/v1/billing/documents")]
    [InlineData("/api/v1/inventory/quick-adjustments")]
    [InlineData("/api/v1/organization/terminals/{terminalId:guid}/receipt-printer")]
    public async Task Los_endpoints_de_la_fase_7_estan_en_el_recorrido_y_todos_exigen_permiso(string prefix)
    {
        // Las dos pruebas anteriores recorren los endpoints automáticamente; esta asegura que los de la Fase 7 están registrados y
        // que ninguno quedó solo con sesión (sin permiso), salvo la lectura de la impresora de la caja: la interfaz de caja de
        // cualquier cajero la lee para entregársela al agente (decisión de la Fase 7), y exige sesión.
        await using var factory = new PosServerFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(prefix, StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (Method: m, Endpoint: e)))
            .ToList();

        endpoints.ShouldNotBeEmpty();
        endpoints.Where(e => e.Endpoint.Metadata.GetMetadata<PermissionRequirement>() is null)
            .Where(e => !(e.Method == "GET" && e.Endpoint.RoutePattern.RawText == SessionOnlyReceiptPrinter
                && e.Endpoint.Metadata.GetMetadata<AuthenticatedOnly>() is not null))
            .Select(e => $"{e.Method} {e.Endpoint.RoutePattern.RawText}")
            .ShouldBeEmpty();
    }

    private const string SessionOnlyReceiptPrinter = "/api/v1/organization/terminals/{terminalId:guid}/receipt-printer";

    private static IEnumerable<(string Method, string Url, string? Permission)> ProtectedEndpoints(PosServerFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<PermissionRequirement>() is not null || e.Metadata.GetMetadata<AuthenticatedOnly>() is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m =>
                (m, Url(e.RoutePattern.RawText!), e.Metadata.GetMetadata<PermissionRequirement>()?.PermissionCode)));

    private static string Url(string pattern)
    {
        // Sustituye los parámetros de ruta ({id:guid}, {n:long}, {key}) por valores válidos.
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
                var text = parameter.ToString();
                builder.Append(text.Contains(":guid", StringComparison.Ordinal) ? Guid.CreateVersion7().ToString()
                    : text.Contains(":long", StringComparison.Ordinal) || text.Contains(":int", StringComparison.Ordinal) ? "1" : "valor");
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
