using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Reglas de arquitectura de la nube (ADR 0037, "nube separada en el mismo repositorio"):
/// <list type="bullet">
/// <item>La nube NO referencia los módulos del POS ni su host/terminal: solo los bloques compartidos permitidos (SharedKernel,
/// abstracciones, infraestructura transversal y el migrador SQL-first) y el contrato <c>Pos.Licensing.Contracts</c>.</item>
/// <item>El POS no referencia la nube.</item>
/// <item>El contrato compartido solo depende de .NET y NSec (lo usará el POS en 12-B).</item>
/// <item>Capas de los módulos de la nube como en el POS. DESVIACIÓN DOCUMENTADA: <c>Pos.Cloud.Licensing.Domain</c> depende del
/// contrato compartido (huella del equipo y formato de la clave): es un contrato puro, sin infraestructura, y evita duplicar
/// reglas que deben ser idénticas en el POS y en la nube.</item>
/// </list>
/// </summary>
public class CloudArchitectureTests
{
    private const string Contracts = "Pos.Licensing.Contracts";

    /// <summary>Bloques del POS que la nube puede reutilizar.</summary>
    private static readonly HashSet<string> AllowedPosBlocks =
    [
        "Pos.SharedKernel", "Pos.Application.Abstractions", "Pos.Api.Abstractions", "Pos.Infrastructure", "Pos.Server.Migrations",
    ];

    private static readonly Lazy<IReadOnlyList<Assembly>> Assemblies = new(() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Pos.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith("Tests", StringComparison.Ordinal))
            .Select(name => Assembly.Load(name!))
            .ToList());

    private static IEnumerable<(string Name, string[] References)> Graph =>
        Assemblies.Value.Select(a => (a.GetName().Name!, a.GetReferencedAssemblies().Select(r => r.Name!).Where(r => r.StartsWith("Pos.", StringComparison.Ordinal)).ToArray()));

    private static bool IsCloud(string name) => name.StartsWith("Pos.Cloud.", StringComparison.Ordinal);

    [Fact]
    public void Se_cargan_los_ensamblados_de_la_nube()
    {
        Graph.Count(a => IsCloud(a.Name)).ShouldBeGreaterThanOrEqualTo(12);
        Graph.ShouldContain(a => a.Name == Contracts);
    }

    [Fact]
    public void La_nube_solo_referencia_los_bloques_compartidos_permitidos_del_POS()
    {
        var violations = Graph
            .Where(a => IsCloud(a.Name))
            .SelectMany(a => a.References
                .Where(r => !IsCloud(r) && r != Contracts && !AllowedPosBlocks.Contains(r))
                .Select(r => $"{a.Name} no puede depender de {r} (la nube no usa los módulos, el host ni la terminal del POS)."))
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Ni_el_POS_ni_el_contrato_compartido_referencian_la_nube()
    {
        var violations = Graph
            .Where(a => !IsCloud(a.Name) && a.Name != "Pos.License.Simulator")
            .SelectMany(a => a.References.Where(IsCloud).Select(r => $"{a.Name} no puede depender de {r}."))
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void El_contrato_compartido_solo_depende_de_NET_y_NSec_y_el_simulador_solo_del_contrato()
    {
        var contracts = Assemblies.Value.Single(a => a.GetName().Name == Contracts);
        contracts.GetReferencedAssemblies().Select(r => r.Name!)
            .Where(r => r is not ("netstandard" or "mscorlib" or "System") && !r.StartsWith("System.", StringComparison.Ordinal) && !r.StartsWith("Microsoft.", StringComparison.Ordinal))
            .ShouldBe(["NSec.Cryptography"]);

        Graph.Single(a => a.Name == "Pos.License.Simulator").References.ShouldBe([Contracts]);
    }

    [Fact]
    public void Capas_de_los_modulos_de_la_nube()
    {
        var violations = new List<string>();
        foreach (var (name, references) in Graph.Where(a => IsCloud(a.Name)))
        {
            var layer = name.Split('.')[^1];
            var module = name.Split('.') is [_, _, var m, _] ? m : null;
            if (module is null)
            {
                continue;
            }

            // Domain: solo el SharedKernel (+ el contrato, desviación documentada solo para Licensing).
            if (layer == "Domain")
            {
                violations.AddRange(references
                    .Where(r => r != "Pos.SharedKernel" && !(module == "Licensing" && r == Contracts))
                    .Select(r => $"{name} (Domain) no puede depender de {r}."));
            }

            // Application: no depende de Infrastructure ni de Api.
            if (layer == "Application")
            {
                violations.AddRange(references
                    .Where(r => r.EndsWith(".Infrastructure", StringComparison.Ordinal) || r.EndsWith(".Api", StringComparison.Ordinal) || r == "Pos.Api.Abstractions")
                    .Select(r => $"{name} (Application) no puede depender de {r}."));
            }

            // Entre módulos de la nube: nada directo (se comparten solo las abstracciones de la nube).
            violations.AddRange(references
                .Where(r => r.Split('.') is [_, "Cloud", var other, _] && other != module && r is not ("Pos.Cloud.Abstractions" or "Pos.Cloud.Infrastructure" or "Pos.Cloud.Migrations"))
                .Select(r => $"{name} no puede depender de otro módulo de la nube ({r})."));
        }

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Todo_endpoint_de_la_API_interna_exige_sesion_salvo_el_ingreso()
    {
        using var factory = new CloudServerFactory();
        _ = factory.Server;
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var admin = endpoints.Where(e => e.RoutePattern.RawText?.StartsWith("/admin", StringComparison.Ordinal) == true).ToList();
        admin.Count.ShouldBeGreaterThan(20);
        var anonymousByDesign = new HashSet<string>(StringComparer.Ordinal) { "/admin/auth/login", "/admin/auth/totp/enrollment", "/admin/auth/totp" };
        admin.Where(e => !anonymousByDesign.Contains(e.RoutePattern.RawText!))
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null || e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText)
            .ShouldBeEmpty();

        // La API del POS es anónima por diseño (la autentican la clave o el token) y siempre con límite de peticiones.
        var pos = endpoints.Where(e => e.RoutePattern.RawText?.StartsWith("/v1", StringComparison.Ordinal) == true).ToList();
        pos.Count.ShouldBe(4);
        pos.ShouldAllBe(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>() != null);
    }
}
