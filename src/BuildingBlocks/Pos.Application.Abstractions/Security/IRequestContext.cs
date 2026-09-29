using System.Net;

namespace Pos.Application.Abstractions.Security;

/// <summary>Datos técnicos de la petición en curso que se guardan en la auditoría. Fuera de HTTP todo es <c>null</c>.</summary>
public interface IRequestContext
{
    string? CorrelationId { get; }

    IPAddress? IpAddress { get; }

    Guid? DeviceId { get; }
}

/// <summary>
/// Autor de los cambios del caso de uso (columnas <c>created_by</c>/<c>updated_by</c> y auditoría):
/// el usuario autenticado o, si no hay, el usuario técnico <c>system</c> de la empresa.
/// </summary>
public interface IActorContext
{
    /// <summary>Id del autor, o <c>null</c> si todavía no existe ninguno (antes del asistente inicial).</summary>
    Guid? ActorId { get; }

    string? ActorDisplayName { get; }

    /// <summary>
    /// Fija explícitamente el autor y la empresa/sucursal del caso de uso. Lo usa el asistente inicial, que crea el
    /// usuario <c>system</c> y la empresa en la misma transacción en la que se auditan.
    /// </summary>
    void Use(Guid actorId, string displayName, Guid companyId, Guid? branchId);

    Guid? CompanyId { get; }

    Guid? BranchId { get; }
}
