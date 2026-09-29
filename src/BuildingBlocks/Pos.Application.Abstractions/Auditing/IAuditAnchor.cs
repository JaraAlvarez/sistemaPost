namespace Pos.Application.Abstractions.Auditing;

/// <summary>Sello de la auditoría del nodo: número y código corto para imprimir (<c>7F3A-91C2-0B44-E1D8</c>).</summary>
public sealed record AuditSealInfo(long SealNo, string ShortCode, DateTimeOffset SealedAt);

/// <summary>
/// Ancla externa de la auditoría (D6-08): sella lo que ya se puede sellar con seguridad (lo emitido antes del horizonte
/// seguro) y devuelve el último sello del nodo para imprimirlo en un documento (reporte Z). Una reescritura posterior de
/// la bitácora hasta ese sello deja de coincidir con el código impreso.
/// </summary>
public interface IAuditAnchor
{
    Task<AuditSealInfo?> SealNowAsync(CancellationToken cancellationToken = default);
}
