namespace Pos.Application.Abstractions.Messaging;

/// <summary>Destino de un mensaje del outbox.</summary>
public enum OutboxDestination
{
    /// <summary>Efecto interno del propio nodo, procesado en segundo plano (p. ej. enviar un documento fiscal).</summary>
    Local,

    /// <summary>Evento de integración para otros nodos (nube, paquete <c>.possync</c>). Se conserva hasta que lo confirman.</summary>
    Sync,
}

/// <summary>
/// Outbox transaccional: el mensaje se guarda en la MISMA transacción del caso de uso, así nunca se pierde
/// ni se publica algo que no ocurrió.
/// </summary>
public interface IOutbox
{
    void Enqueue(string type, object payload, OutboxDestination destination);
}
