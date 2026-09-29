using Pos.Server.Migrations;

namespace Pos.Cloud.Migrations;

/// <summary>
/// Scripts de la BD de la nube. La BD de la nube es independiente de la del POS (L-01): su propio catálogo, su propia
/// versión de esquema y sus propios esquemas (<c>system</c>, <c>audit</c>, <c>portal</c>, <c>licensing</c>).
/// </summary>
public static class CloudScripts
{
    private static readonly Lazy<ScriptCatalog> Embedded = new(() => ScriptCatalog.FromAssembly(typeof(CloudScripts).Assembly));

    public static ScriptCatalog Catalog => Embedded.Value;
}
