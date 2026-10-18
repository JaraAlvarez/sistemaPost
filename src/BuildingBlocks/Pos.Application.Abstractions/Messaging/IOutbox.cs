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

/// <summary>
/// Supresión del titular (Ley 1581) en el outbox SYNC: los mensajes de cambio ya guardados de un maestro (p. ej. el tercero) pierden sus
/// valores, que pueden contener nombre, identificación, texto de búsqueda y datos de contacto (revisión, ronda 2 §6).
/// </summary>
public interface ISyncOutboxRedaction
{
    /// <summary>
    /// Reemplaza por <c>(suprimido)</c> los valores con datos del titular (nombre, identificación, texto de búsqueda, contacto, notas) de los
    /// mensajes SYNC de la entidad (<c>esquema.tabla</c>) y el id dados; los demás campos se conservan.
    /// </summary>
    Task<int> RedactAsync(string entity, Guid id, CancellationToken cancellationToken = default);
}
