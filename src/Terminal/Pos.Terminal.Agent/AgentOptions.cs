using System.Reflection;

namespace Pos.Terminal.Agent;

/// <summary>Configuración local del agente (sección <c>Agent</c> de appsettings.json o variables de entorno <c>Agent__*</c>).</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Puerto HTTP; el agente escucha solo en localhost (127.0.0.1 y ::1).</summary>
    public int Port { get; set; } = 5490;

    /// <summary>Tamaño máximo del cuerpo de una petición (tiquete + impresora en JSON).</summary>
    public int MaxRequestBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Orígenes web que pueden llamar al agente desde un navegador (la interfaz de caja servida por el servidor local, p. ej.
    /// <c>http://localhost:5480</c>). Vacío: ninguno; las peticiones sin cabecera <c>Origin</c> (programas locales) siempre se atienden.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Carpeta del transporte de archivo cuando la impresora no indica una (por defecto <c>C:\ProgramData\{Producto}\agent\output</c>).</summary>
    public string? FileOutputDirectory { get; set; }

    /// <summary>
    /// Otras carpetas que la configuración de la impresora (FILE) puede indicar. El agente corre como servicio con permisos amplios y no
    /// autentica a quien lo llama: no escribe en carpetas que no estén en esta lista.
    /// </summary>
    public string[] AllowedFileDirectories { get; set; } = [];

    /// <summary>Velocidad del puerto serie si la dirección no la indica (<c>COM3</c> frente a <c>COM3:19200</c>).</summary>
    public int SerialBaudRate { get; set; } = 9600;

    public TimeSpan NetworkConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Tiempo máximo de un trabajo de impresión completo.</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public string ResolveFileOutputDirectory() =>
        string.IsNullOrWhiteSpace(FileOutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AgentInfo.ProductName, "agent", "output")
            : Path.GetFullPath(FileOutputDirectory, AppContext.BaseDirectory);
}

/// <summary>Identidad del agente tomada de los metadatos del ensamblado.</summary>
public static class AgentInfo
{
    private static readonly Assembly AgentAssembly = typeof(AgentInfo).Assembly;

    public static string ProductName { get; } =
        AgentAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "PosProductName")?.Value ?? "PosSupermercado";

    public static string Version { get; } =
        (AgentAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static string ServiceName => $"{ProductName}-TerminalAgent";
}
