namespace Pos.SharedKernel.Domain;

/// <summary>Hecho de negocio ocurrido dentro de un agregado (p. ej. "venta completada").</summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

/// <summary>Base de eventos de dominio. El Id y la fecha los aporta quien lo crea (IIdGenerator, IClock).</summary>
public abstract record DomainEvent(Guid EventId, DateTimeOffset OccurredAt) : IDomainEvent;
