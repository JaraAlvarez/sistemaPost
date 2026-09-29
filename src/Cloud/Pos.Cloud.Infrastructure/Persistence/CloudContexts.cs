using System.Net;
using System.Security.Claims;
using Npgsql;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Abstractions;

namespace Pos.Cloud.Infrastructure.Persistence;

/// <summary>
/// Identidad del nodo de la nube para la auditoría (una cadena de sellos propia). Implementa <see cref="IInstallationContext"/>
/// para reutilizar tal cual el sellador y el verificador del POS; los datos de empresa y sucursal no aplican en la nube.
/// </summary>
public sealed class CloudNodeContext(NpgsqlDataSource dataSource) : IInstallationContext
{
    private Guid? _nodeId;

    public Guid NodeId => _nodeId ?? throw new InvalidOperationException("El nodo de la nube aún no se ha leído de la BD (system.cloud_node).");

    public NodeRole NodeRole => NodeRole.StoreServer;

    public bool IsSetupCompleted => true;

    public Guid? CompanyId => null;

    public Guid? BranchId => null;

    public Guid? SystemUserId => SystemActor.Id;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT node_id FROM system.cloud_node");
        _nodeId = (Guid?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("La BD de la nube no tiene su fila system.cloud_node.");
    }
}

/// <summary>Claims con los que el host describe al usuario del portal (cookie o Bearer).</summary>
public static class PortalClaims
{
    public const string UserId = "portal:user_id";
    public const string SessionId = "portal:session_id";
    public const string Role = "portal:role";
    public const string Stage = "portal:stage";
    public const string Permission = "portal:permission";
    public const string MustChangePassword = "portal:must_change_password";
    public const string ActiveStage = "ACTIVE";
}

/// <summary>
/// Usuario del portal en curso. El host lo completa desde la identidad autenticada: en cada petición HTTP (middleware) y, en
/// las pantallas interactivas, al abrir el circuito de Blazor. Solo cuenta como autenticado con el segundo factor completo.
/// </summary>
public sealed class PortalUserContext : IPortalUserContext
{
    public bool IsAuthenticated { get; private set; }

    public Guid? UserId { get; private set; }

    public string? DisplayName { get; private set; }

    public string? Role { get; private set; }

    public Guid? SessionId { get; private set; }

    public void Set(ClaimsPrincipal? principal)
    {
        var active = principal?.Identity?.IsAuthenticated == true && principal.FindFirst(PortalClaims.Stage)?.Value == PortalClaims.ActiveStage;
        IsAuthenticated = active;
        UserId = active && Guid.TryParse(principal!.FindFirst(PortalClaims.UserId)?.Value, out var userId) ? userId : null;
        SessionId = active && Guid.TryParse(principal!.FindFirst(PortalClaims.SessionId)?.Value, out var sessionId) ? sessionId : null;
        DisplayName = active ? principal!.FindFirst(ClaimTypes.Name)?.Value : null;
        Role = active ? principal!.FindFirst(PortalClaims.Role)?.Value : null;
    }

    /// <summary>Copia el usuario a otro ámbito (cada acción de una pantalla interactiva usa su propio ámbito y su propio contexto EF).</summary>
    public void CopyFrom(IPortalUserContext other)
    {
        ArgumentNullException.ThrowIfNull(other);
        IsAuthenticated = other.IsAuthenticated;
        UserId = other.UserId;
        DisplayName = other.DisplayName;
        Role = other.Role;
        SessionId = other.SessionId;
    }
}

/// <summary>Fuera de HTTP (consola, procesos de fondo) no hay datos de la petición.</summary>
internal sealed class NoCloudRequestContext : IRequestContext
{
    public string? CorrelationId => null;

    public IPAddress? IpAddress => null;

    public Guid? DeviceId => null;
}
