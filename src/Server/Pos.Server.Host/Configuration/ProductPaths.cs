namespace Pos.Server.Host.Configuration;

/// <summary>Rutas de datos de la instalación (por defecto bajo <c>C:\ProgramData\{Producto}</c>).</summary>
public sealed record ProductPaths(string DataRoot)
{
    public string ConfigDirectory => Path.Combine(DataRoot, "config");

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>Configuración propia de la instalación (la escribe el instalador); tiene prioridad sobre appsettings.json.</summary>
    public string ServerConfigFile => Path.Combine(ConfigDirectory, "server.json");

    public static ProductPaths From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[$"{PosOptions.SectionName}:{nameof(PosOptions.DataRoot)}"];

        var dataRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductInfo.Name)
            : Path.GetFullPath(configured, AppContext.BaseDirectory);

        return new ProductPaths(dataRoot);
    }
}
