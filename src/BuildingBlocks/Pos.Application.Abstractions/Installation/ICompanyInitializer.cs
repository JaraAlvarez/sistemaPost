namespace Pos.Application.Abstractions.Installation;

/// <summary>
/// Datos iniciales que un módulo necesita en cada empresa (p. ej. la lista de precios por defecto o los motivos de ajuste).
/// Se ejecuta al terminar el asistente inicial (en su transacción) y en cada arranque para las instalaciones que ya
/// existían antes de la versión que agregó el módulo. DEBE ser idempotente: solo crea lo que falta.
/// </summary>
public interface ICompanyInitializer
{
    /// <summary>Orden de ejecución (menor primero): un módulo puede depender de los datos de otro.</summary>
    int Order { get; }

    Task InitializeAsync(Guid companyId, CancellationToken cancellationToken);
}
