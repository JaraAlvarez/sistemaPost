using System.Reflection;
using System.Text.RegularExpressions;

namespace Pos.ArchitectureTests;

/// <summary>Ensamblado y los nombres de los ensamblados que referencia.</summary>
public sealed record AssemblyNode(string Name, IReadOnlyCollection<string> References);

/// <summary>
/// Reglas de arquitectura (doc 03, "Reglas de dependencia"). Son funciones puras sobre el grafo de ensamblados
/// para poder probarlas también con casos negativos sintéticos.
/// Cada regla devuelve la lista de violaciones (vacía = cumple).
/// </summary>
public static partial class ArchitectureRules
{
    public const string SharedKernel = "Pos.SharedKernel";
    public const string ApplicationAbstractions = "Pos.Application.Abstractions";
    public const string ApiAbstractions = "Pos.Api.Abstractions";
    public const string Infrastructure = "Pos.Infrastructure";
    public const string ServerHost = "Pos.Server.Host";

    /// <summary>R1: el SharedKernel solo usa la biblioteca base de .NET.</summary>
    public static IEnumerable<string> SharedKernelIsPure(IEnumerable<AssemblyNode> assemblies) =>
        assemblies
            .Where(a => a.Name == SharedKernel)
            .SelectMany(a => a.References.Where(r => !IsBaseLibrary(r))
                .Select(r => $"{a.Name} no puede depender de {r} (solo biblioteca base de .NET)."));

    /// <summary>R2: la capa Domain de un módulo solo depende del SharedKernel y de la biblioteca base.</summary>
    public static IEnumerable<string> DomainDependsOnlyOnSharedKernel(IEnumerable<AssemblyNode> assemblies) =>
        assemblies
            .Where(a => ParseModule(a.Name) is { Layer: "Domain" })
            .SelectMany(a => a.References.Where(r => !IsBaseLibrary(r) && r != SharedKernel)
                .Select(r => $"{a.Name} (Domain) no puede depender de {r}."));

    /// <summary>R3: un módulo solo puede referenciar la capa Contracts de otro módulo.</summary>
    public static IEnumerable<string> ModulesOnlyUseOtherModulesContracts(IEnumerable<AssemblyNode> assemblies) =>
        assemblies
            .Select(a => (Node: a, Module: ParseModule(a.Name)))
            .Where(x => x.Module is not null)
            .SelectMany(x => x.Node.References
                .Select(r => (Reference: r, Target: ParseModule(r)))
                .Where(r => r.Target is not null
                            && r.Target.Value.Module != x.Module!.Value.Module
                            && r.Target.Value.Layer != "Contracts")
                .Select(r => $"{x.Node.Name} no puede depender de {r.Reference}: entre módulos solo se usa *.Contracts."));

    /// <summary>R4: dentro de un módulo, Application no depende de Infrastructure ni de Api (inversión de dependencias).</summary>
    public static IEnumerable<string> ApplicationDoesNotDependOnOuterLayers(IEnumerable<AssemblyNode> assemblies) =>
        assemblies
            .Where(a => ParseModule(a.Name) is { Layer: "Application" })
            .SelectMany(a => a.References
                .Where(r => ParseModule(r) is { Layer: "Infrastructure" or "Api" })
                .Select(r => $"{a.Name} (Application) no puede depender de {r}."));

    /// <summary>R5: capas de los building blocks y nadie depende del host.</summary>
    public static IEnumerable<string> BuildingBlocksLayering(IEnumerable<AssemblyNode> assemblies)
    {
        var forbidden = new Dictionary<string, string[]>
        {
            [ApplicationAbstractions] = [Infrastructure, ApiAbstractions, ServerHost],
            [ApiAbstractions] = [Infrastructure, ServerHost],
            [Infrastructure] = [ApiAbstractions, ServerHost],
        };

        foreach (var assembly in assemblies)
        {
            if (forbidden.TryGetValue(assembly.Name, out var targets))
            {
                foreach (var reference in assembly.References.Intersect(targets))
                {
                    yield return $"{assembly.Name} no puede depender de {reference}.";
                }
            }

            if (assembly.Name != ServerHost && assembly.References.Contains(ServerHost))
            {
                yield return $"{assembly.Name} no puede depender del host ({ServerHost}).";
            }
        }
    }

    /// <summary>R6: sin double/float en la superficie pública de Domain, Contracts y SharedKernel (dinero exacto).</summary>
    public static IEnumerable<string> NoFloatingPointInPublicSurface(IEnumerable<Type> types) =>
        types
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .SelectMany(t => PublicMemberTypes(t)
                .Where(m => IsFloatingPoint(m.Type))
                .Select(m => $"{t.FullName}.{m.Member} usa {m.Type.Name}: use decimal (Money/Quantity/Percentage)."));

    public static bool AppliesFloatingPointRule(string assemblyName) =>
        assemblyName == SharedKernel || ParseModule(assemblyName) is { Layer: "Domain" or "Contracts" };

    public static (string Module, string Layer)? ParseModule(string assemblyName)
    {
        var match = ModulePattern().Match(assemblyName);
        return match.Success ? (match.Groups["module"].Value, match.Groups["layer"].Value) : null;
    }

    private static bool IsBaseLibrary(string name) =>
        name is "netstandard" or "mscorlib" or "System" || name.StartsWith("System.", StringComparison.Ordinal);

    private static bool IsFloatingPoint(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(double) || underlying == typeof(float);
    }

    private static IEnumerable<(string Member, Type Type)> PublicMemberTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(flags))
        {
            yield return (property.Name, property.PropertyType);
        }

        foreach (var field in type.GetFields(flags))
        {
            yield return (field.Name, field.FieldType);
        }

        foreach (var method in type.GetMethods(flags).Where(m => !m.IsSpecialName))
        {
            yield return (method.Name, method.ReturnType);
            foreach (var parameter in method.GetParameters())
            {
                yield return ($"{method.Name}({parameter.Name})", parameter.ParameterType);
            }
        }

        foreach (var constructor in type.GetConstructors(flags))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return ($".ctor({parameter.Name})", parameter.ParameterType);
            }
        }
    }

    [GeneratedRegex(@"^Pos\.Modules\.(?<module>[A-Za-z0-9]+)\.(?<layer>[A-Za-z0-9]+)(\..+)?$")]
    private static partial Regex ModulePattern();
}
