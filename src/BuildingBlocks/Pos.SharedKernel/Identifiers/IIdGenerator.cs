namespace Pos.SharedKernel.Identifiers;

/// <summary>
/// Genera identificadores UUID v7 (ordenados por tiempo). Se generan en la aplicación —no en la BD—
/// para poder crear registros sin conexión y sincronizarlos sin colisiones (ver ADR-0004).
/// </summary>
public interface IIdGenerator
{
    Guid NewId();
}
