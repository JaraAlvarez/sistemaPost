using System.Reflection;

namespace Pos.Server.Migrations;

/// <summary>Scripts incrustados en este ensamblado, ordenados para su ejecución.</summary>
public sealed class ScriptCatalog
{
    private static readonly Lazy<ScriptCatalog> Embedded = new(() => FromAssembly(typeof(ScriptCatalog).Assembly));

    public ScriptCatalog(IEnumerable<MigrationScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        var all = scripts.ToList();

        var duplicated = all.Where(s => s.Version is not null)
            .GroupBy(s => s.Version)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new InvalidOperationException($"Versión de migración duplicada: {duplicated.Key}.");
        }

        Versioned = all.Where(s => s.Kind == MigrationKind.Versioned)
            .OrderBy(s => s.Version!, Comparer<string>.Create(MigrationScript.CompareVersions))
            .ToList();
        Repeatable = all.Where(s => s.Kind == MigrationKind.Repeatable).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        Always = all.Where(s => s.Kind == MigrationKind.Always).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Scripts incrustados en la versión instalada de la aplicación.</summary>
    public static ScriptCatalog Default => Embedded.Value;

    public IReadOnlyList<MigrationScript> Versioned { get; }

    public IReadOnlyList<MigrationScript> Repeatable { get; }

    public IReadOnlyList<MigrationScript> Always { get; }

    /// <summary>Versión de esquema que espera esta versión de la aplicación.</summary>
    public string? LatestVersion => Versioned.Count == 0 ? null : Versioned[^1].Version;

    public static ScriptCatalog FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return MigrationScript.Parse(name, reader.ReadToEnd());
            });

        return new ScriptCatalog(scripts);
    }
}
