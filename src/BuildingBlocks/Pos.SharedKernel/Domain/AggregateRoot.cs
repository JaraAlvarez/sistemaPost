namespace Pos.SharedKernel.Domain;

/// <summary>Contrato no genérico para que la infraestructura recoja los eventos de cualquier agregado.</summary>
public interface IAggregateRoot
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}

/// <summary>
/// Raíz de agregado: frontera de consistencia (p. ej. una venta con sus líneas y pagos).
/// Acumula eventos de dominio que la infraestructura publica al confirmar la transacción.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(Guard.NotNull(domainEvent));
}
