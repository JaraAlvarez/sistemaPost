using System.Net;
using Dapper;
using Npgsql;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;

namespace Pos.Infrastructure.Persistence;

/// <summary>Autor del caso de uso: usuario autenticado → usuario <c>system</c> de la empresa (Fase 2: siempre system).</summary>
internal sealed class ActorContext(ICurrentUser currentUser, IInstallationContext installation) : IActorContext
{
    private (Guid ActorId, string DisplayName, Guid CompanyId, Guid? BranchId)? _override;

    public Guid? ActorId => _override?.ActorId
        ?? (currentUser.IsAuthenticated ? currentUser.UserId : null)
        ?? installation.SystemUserId;

    public string? ActorDisplayName => _override?.DisplayName
        ?? (currentUser.IsAuthenticated ? currentUser.DisplayName : null)
        ?? (installation.SystemUserId is null ? null : SystemUserDisplayName);

    public Guid? CompanyId => _override?.CompanyId ?? currentUser.CompanyId ?? installation.CompanyId;

    public Guid? BranchId => _override is { } o ? o.BranchId : currentUser.BranchId ?? installation.BranchId;

    public const string SystemUserDisplayName = "Sistema";

    public void Use(Guid actorId, string displayName, Guid companyId, Guid? branchId) =>
        _override = (actorId, displayName, companyId, branchId);
}

/// <summary>Usuario anónimo: la autenticación llega en la Fase 3 (riesgo aceptado: el servidor solo escucha en localhost).</summary>
internal sealed class AnonymousCurrentUser : ICurrentUser
{
    public bool IsAuthenticated => false;

    public Guid? UserId => null;

    public string? DisplayName => null;

    public Guid? CompanyId => null;

    public Guid? BranchId => null;

    public Guid? PosTerminalId => null;

    public Guid? SessionId => null;
}

/// <summary>Contexto técnico vacío (procesos en segundo plano). El host lo reemplaza por uno basado en la petición HTTP.</summary>
internal sealed class NoRequestContext : IRequestContext
{
    public string? CorrelationId => null;

    public IPAddress? IpAddress => null;

    public Guid? DeviceId => null;
}

/// <summary>
/// Lee <c>system.installation</c> y el usuario <c>system</c> de la empresa. Singleton con caché; se refresca tras el
/// asistente inicial. Si la fila no existe (BD recién creada), la crea con un installation_id nuevo.
/// </summary>
internal sealed class InstallationContext(NpgsqlDataSource dataSource, NodeRole defaultRole) : IInstallationContext, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Snapshot? _snapshot;

    public Guid NodeId => Current.NodeId;

    public NodeRole NodeRole => Current.Role;

    public bool IsSetupCompleted => Current.SetupCompleted;

    public Guid? CompanyId => Current.CompanyId;

    public Guid? BranchId => Current.BranchId;

    public Guid? SystemUserId => Current.SystemUserId;

    private Snapshot Current => _snapshot
        ?? throw new InvalidOperationException("El contexto de instalación no se ha cargado (llame a RefreshAsync al arrancar).");

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO system.installation (installation_id, node_role, created_at)
                VALUES (@id, @role, now())
                ON CONFLICT (id) DO NOTHING
                """,
                new { id = Guid.CreateVersion7(), role = ToDatabase(defaultRole) },
                cancellationToken: cancellationToken));

            var row = await connection.QuerySingleAsync<(Guid InstallationId, string NodeRole, Guid? CompanyId, Guid? BranchId, DateTimeOffset? SetupCompletedAt)>(
                new CommandDefinition(
                    """
                    SELECT installation_id, node_role, home_company_id, home_branch_id, setup_completed_at
                    FROM system.installation
                    """,
                    cancellationToken: cancellationToken));

            Guid? systemUserId = row.CompanyId is null
                ? null
                : await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM identity.users WHERE company_id = @company AND kind = 'SYSTEM'",
                    new { company = row.CompanyId },
                    cancellationToken: cancellationToken));

            _snapshot = new Snapshot(
                row.InstallationId,
                FromDatabase(row.NodeRole),
                row.SetupCompletedAt is not null,
                row.CompanyId,
                row.BranchId,
                systemUserId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    public static string ToDatabase(NodeRole role) => role == NodeRole.StoreServer ? "STORE_SERVER" : "ALL_IN_ONE";

    public static NodeRole FromDatabase(string role) => role == "STORE_SERVER" ? NodeRole.StoreServer : NodeRole.AllInOne;

    private sealed record Snapshot(Guid NodeId, NodeRole Role, bool SetupCompleted, Guid? CompanyId, Guid? BranchId, Guid? SystemUserId);
}
