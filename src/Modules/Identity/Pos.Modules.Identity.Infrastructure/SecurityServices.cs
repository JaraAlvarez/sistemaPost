using System.Collections.Concurrent;
using Dapper;
using Npgsql;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Identity.Application;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;
using Pos.SharedKernel.Security;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Identity.Infrastructure;

/// <summary>
/// Permisos efectivos leídos con Dapper y cacheados por (usuario, sucursal, versión de seguridad): un cambio de rol,
/// excepción o estado sube la versión y la siguiente petición recalcula (D3-06).
/// </summary>
internal sealed class PermissionEvaluator(NpgsqlDataSource dataSource) : IPermissionEvaluator
{
    private const int MaxCachedEntries = 2_000;

    private readonly ConcurrentDictionary<(Guid UserId, Guid? BranchId, long Version), IReadOnlySet<string>> _cache = new();

    public async Task<IReadOnlySet<string>> GetEffectiveAsync(Guid userId, Guid? branchId, long? securityVersion, CancellationToken cancellationToken)
    {
        if (securityVersion is { } version && _cache.TryGetValue((userId, branchId, version), out var cached))
        {
            return cached;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var grants = await connection.QueryAsync<(Guid? BranchId, string Permission)>(new CommandDefinition(
            """
            SELECT ur.branch_id, rp.permission_code
            FROM identity.user_roles ur
            JOIN identity.roles r ON r.id = ur.role_id AND r.deleted_at IS NULL
            JOIN identity.role_permissions rp ON rp.role_id = r.id
            WHERE ur.user_id = @userId
            """,
            new { userId },
            cancellationToken: cancellationToken));
        var overrides = await connection.QueryAsync<(string Permission, string Effect, Guid? BranchId)>(new CommandDefinition(
            "SELECT permission_code, effect, branch_id FROM identity.user_permission_overrides WHERE user_id = @userId",
            new { userId },
            cancellationToken: cancellationToken));
        var catalog = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT code FROM identity.permissions WHERE NOT is_deprecated", cancellationToken: cancellationToken))).ToHashSet(StringComparer.Ordinal);

        var effective = EffectivePermissions.Compute(
            grants.GroupBy(g => g.BranchId).Select(g => new AssignedRole(g.Key, g.Select(x => x.Permission).ToList())),
            overrides.Select(o => new PermissionOverrideRule(o.Permission, o.Effect == "DENY" ? OverrideEffect.Deny : OverrideEffect.Grant, o.BranchId)),
            branchId,
            catalog);

        if (securityVersion is { } v)
        {
            if (_cache.Count > MaxCachedEntries)
            {
                _cache.Clear();
            }

            _cache[(userId, branchId, v)] = effective;
        }

        return effective;
    }
}

/// <summary>Permiso del usuario actual en la sucursal de la sesión (o la indicada).</summary>
internal sealed class PermissionChecker(ICurrentUser current, ICurrentSecurityVersion version, IPermissionEvaluator evaluator) : IPermissionChecker
{
    public async Task<bool> HasPermissionAsync(string permissionCode, Guid? branchId = null, CancellationToken cancellationToken = default)
    {
        if (!current.IsAuthenticated || current.UserId is not { } userId)
        {
            return false;
        }

        var effective = await evaluator.GetEffectiveAsync(userId, branchId ?? current.BranchId, version.SecurityVersion, cancellationToken);
        return effective.Contains(permissionCode);
    }
}

/// <summary>
/// Valida el token opaco de cada petición: sesión no revocada, no vencida, sin exceder la inactividad, del mismo
/// equipo que la abrió y de un usuario activo. Actualiza la última actividad como máximo una vez por minuto.
/// </summary>
internal sealed class SessionAuthenticator(NpgsqlDataSource dataSource, IClock clock) : ISessionAuthenticator
{
    private static readonly TimeSpan ActivityResolution = TimeSpan.FromMinutes(1);

    public async Task<AuthenticatedSession?> AuthenticateAsync(string token, IClientContext client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            """
            SELECT s.id AS SessionId, s.user_id AS UserId, s.kind AS Kind, s.device_id AS DeviceId, s.pos_terminal_id AS PosTerminalId,
                   s.branch_id AS BranchId, s.last_activity_at AS LastActivityAt, s.idle_timeout_seconds AS IdleTimeoutSeconds,
                   s.expires_at AS ExpiresAt, u.display_name AS DisplayName, u.status AS Status, u.must_change_password AS MustChangePassword,
                   u.security_version AS SecurityVersion, u.company_id AS CompanyId
            FROM identity.user_sessions s
            JOIN identity.users u ON u.id = s.user_id AND u.deleted_at IS NULL
            WHERE s.token_hash = @hash AND s.revoked_at IS NULL
            """,
            new { hash = SecureTokens.Hash(token) },
            cancellationToken: cancellationToken));

        var now = clock.UtcNow.UtcDateTime;
        if (row is null
            || row.ExpiresAt <= now
            || row.LastActivityAt.AddSeconds(row.IdleTimeoutSeconds) <= now
            || row.Status == "DISABLED"
            || row.DeviceId != client.DeviceId)
        {
            return null;
        }

        if (now - row.LastActivityAt >= ActivityResolution)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE identity.user_sessions SET last_activity_at = @now WHERE id = @id",
                new { now = clock.UtcNow, id = row.SessionId },
                cancellationToken: cancellationToken));
        }

        return new AuthenticatedSession(
            row.SessionId, row.UserId, row.DisplayName, row.CompanyId, row.BranchId, row.PosTerminalId, row.Kind == "TERMINAL",
            row.MustChangePassword, row.SecurityVersion);
    }

    private sealed class SessionRow
    {
        public Guid SessionId { get; set; }

        public Guid UserId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public Guid? DeviceId { get; set; }

        public Guid? PosTerminalId { get; set; }

        public Guid BranchId { get; set; }

        public DateTime LastActivityAt { get; set; }

        public int IdleTimeoutSeconds { get; set; }

        public DateTime ExpiresAt { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public bool MustChangePassword { get; set; }

        public long SecurityVersion { get; set; }

        public Guid CompanyId { get; set; }
    }
}

internal sealed class SessionRevoker(NpgsqlDataSource dataSource, IClock clock) : ISessionRevoker
{
    public async Task<int> RevokeByDeviceAsync(Guid deviceId, string reason, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE identity.user_sessions SET revoked_at = @now, revoked_reason = @reason WHERE device_id = @deviceId AND revoked_at IS NULL",
            new { now = clock.UtcNow, reason, deviceId },
            cancellationToken: cancellationToken));
    }
}

/// <summary>¿Hay un Propietario activo? Cacheado; se invalida al crearlo.</summary>
internal sealed class IdentityState(NpgsqlDataSource dataSource) : IIdentityState
{
    private const int Unknown = -1;

    /// <summary>-1 = sin calcular; 0 = no; 1 = sí.</summary>
    private int _ownerPending = Unknown;

    public async Task<bool> IsOwnerPendingAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _ownerPending);
        if (cached != Unknown)
        {
            return cached == 1;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var hasOwner = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1 FROM identity.users u
                JOIN identity.user_roles ur ON ur.user_id = u.id
                JOIN identity.roles r ON r.id = ur.role_id AND r.code = 'OWNER' AND r.is_system
                WHERE u.kind = 'HUMAN' AND u.status <> 'DISABLED' AND u.deleted_at IS NULL)
            """,
            cancellationToken: cancellationToken));
        var companyExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM org.companies)", cancellationToken: cancellationToken));
        var pending = companyExists && !hasOwner;
        Volatile.Write(ref _ownerPending, pending ? 1 : 0);
        return pending;
    }

    public void Invalidate() => Volatile.Write(ref _ownerPending, Unknown);
}

/// <summary>
/// Consume una autorización de supervisor de forma atómica: un único UPDATE … RETURNING que exige coincidencia exacta
/// de permiso, acción, objetivo, cajero y caja, y que no esté vencida ni usada (D3-07).
/// </summary>
internal sealed class SupervisorAuthorization(NpgsqlDataSource dataSource, ICurrentUser current, IRequestContext request, IClock clock)
    : ISupervisorAuthorization
{
    public async Task<ConsumedAuthorization?> TryConsumeAsync(
        Guid grantId, string permissionCode, string action, Guid? targetId, CancellationToken cancellationToken = default)
    {
        if (current.UserId is not { } userId)
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var authorizedBy = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            UPDATE identity.authorization_grants
            SET consumed_at = @now, consumed_by_request = @correlation
            WHERE id = @grantId AND consumed_at IS NULL AND expires_at > @now
              AND permission_code = @permissionCode AND action = @action AND requested_by = @userId
              AND (target_id IS NULL OR target_id = @targetId)
              AND pos_terminal_id IS NOT DISTINCT FROM @terminal
            RETURNING authorized_by
            """,
            new { now = clock.UtcNow, correlation = request.CorrelationId, grantId, permissionCode, action, userId, targetId, terminal = current.PosTerminalId },
            cancellationToken: cancellationToken));

        return authorizedBy is { } by ? new ConsumedAuthorization(grantId, by) : null;
    }
}
