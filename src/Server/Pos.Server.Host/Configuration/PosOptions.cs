namespace Pos.Server.Host.Configuration;

/// <summary>Sección <c>Pos</c> de la configuración.</summary>
public sealed class PosOptions
{
    public const string SectionName = "Pos";

    /// <summary>Carpeta raíz de datos (config, logs, backups). Vacío = <c>%ProgramData%\{Producto}</c>. Relativa = junto al ejecutable.</summary>
    public string? DataRoot { get; set; }

    /// <summary>Zona horaria del negocio (IANA).</summary>
    public string BusinessTimeZone { get; set; } = "America/Bogota";

    public ServerOptions Server { get; set; } = new();
}

public sealed class ServerOptions
{
    /// <summary>Puerto HTTP de la API: solo en localhost.</summary>
    public int Port { get; set; } = 5480;

    /// <summary>Puerto HTTPS en la LAN (solo edición Multicaja, solo equipos emparejados).</summary>
    public int LanHttpsPort { get; set; } = 5443;
}
