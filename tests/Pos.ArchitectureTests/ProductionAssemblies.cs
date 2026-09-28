using System.Reflection;

namespace Pos.ArchitectureTests;

/// <summary>Carga todos los ensamblados de producción Pos.* presentes junto a las pruebas.</summary>
public static class ProductionAssemblies
{
    private static readonly Lazy<IReadOnlyList<Assembly>> Loaded = new(Load);

    public static IReadOnlyList<Assembly> All => Loaded.Value;

    public static IReadOnlyList<AssemblyNode> Graph =>
        All.Select(a => new AssemblyNode(
                a.GetName().Name!,
                a.GetReferencedAssemblies().Select(r => r.Name!).ToArray()))
            .ToArray();

    private static Assembly[] Load() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Pos.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith("Tests", StringComparison.Ordinal))
            .Select(name => Assembly.Load(name!))
            .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
            .ToArray();
}
