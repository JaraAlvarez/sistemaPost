namespace Pos.Application.Abstractions.Installation;

/// <summary>Edición instalada. La edición es la única diferencia comercial (decisión del propietario, revisión §10.5).</summary>
public enum NodeRole
{
    /// <summary>Caja Única: un equipo con servidor, base de datos y una sola caja.</summary>
    AllInOne,

    /// <summary>Multicaja: servidor de tienda con cajas y equipos administrativos ilimitados en la LAN.</summary>
    StoreServer,
}

/// <summary>Identidad de este nodo (instalación) y de su empresa/sucursal local. Se lee de <c>system.installation</c>.</summary>
public interface IInstallationContext
{
    /// <summary>Id del nodo (= installation_id). Identifica la cadena de auditoría y el origen de los datos.</summary>
    Guid NodeId { get; }

    NodeRole NodeRole { get; }

    bool IsSetupCompleted { get; }

    /// <summary>Empresa local; <c>null</c> hasta completar el asistente inicial.</summary>
    Guid? CompanyId { get; }

    /// <summary>Sucursal local; <c>null</c> hasta completar el asistente inicial.</summary>
    Guid? BranchId { get; }

    /// <summary>Usuario técnico <c>system</c> de la empresa: autor de procesos automáticos.</summary>
    Guid? SystemUserId { get; }

    /// <summary>Vuelve a leer la instalación (tras el asistente o una restauración).</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
