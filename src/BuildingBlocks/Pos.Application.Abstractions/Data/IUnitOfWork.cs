namespace Pos.Application.Abstractions.Data;

/// <summary>Confirma los cambios de un caso de uso en una sola transacción. Se implementa en la Fase 2.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
