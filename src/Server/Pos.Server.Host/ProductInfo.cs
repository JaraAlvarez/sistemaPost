using System.Reflection;

namespace Pos.Server.Host;

/// <summary>Identidad del producto, tomada de los metadatos del ensamblado (Directory.Build.props).</summary>
public static class ProductInfo
{
    private static readonly Assembly HostAssembly = typeof(ProductInfo).Assembly;

    public static string Name { get; } =
        HostAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PosProductName")?.Value ?? "PosSupermercado";

    /// <summary>Versión completa (SemVer + metadatos de compilación).</summary>
    public static string Version { get; } =
        HostAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string ServerServiceName => $"{Name}-Server";
}
