namespace Pos.Terminal.Agent;

/// <summary>Mensajes de registro del agente (generados en compilación). Nunca incluyen el contenido del tiquete.</summary>
internal static partial class AgentLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Trabajo {Kind} enviado a {Printer} ({Bytes} bytes) → {Destination}")]
    public static partial void JobSent(ILogger logger, string kind, string printer, int bytes, string destination);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La impresora {Printer} no recibió el trabajo {Kind}")]
    public static partial void JobFailed(ILogger logger, Exception exception, string printer, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Petición rechazada ({Code}) desde {Remote}")]
    public static partial void RequestRejected(ILogger logger, string code, string remote);
}
