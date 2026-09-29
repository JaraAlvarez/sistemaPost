namespace Pos.Application.Abstractions.Installation;

/// <summary>Modo del asistente inicial (revisión arquitectónica §2.2, P1).</summary>
public enum SetupMode
{
    /// <summary>Primera instalación de la empresa: crea la empresa.</summary>
    NewCompany,

    /// <summary>Tienda adicional de una empresa existente (llega con la sincronización).</summary>
    JoinCompany,
}

/// <summary>Cierra el asistente inicial en <c>system.installation</c> (dentro de la transacción del asistente).</summary>
public interface IInstallationSetup
{
    /// <summary>
    /// Bloquea la fila de la instalación hasta el fin de la transacción y devuelve <c>false</c> si el asistente ya se
    /// completó. Así dos asistentes simultáneos nunca crean dos empresas.
    /// </summary>
    Task<bool> TryBeginAsync(CancellationToken cancellationToken = default);

    /// <summary>Marca la instalación como configurada y, tras el commit, recarga el <see cref="IInstallationContext"/>.</summary>
    Task CompleteAsync(Guid companyId, Guid branchId, SetupMode mode, CancellationToken cancellationToken = default);
}
