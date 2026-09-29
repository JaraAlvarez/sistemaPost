using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Organization.Application.Devices;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Security;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Organization.Infrastructure;

internal sealed class PairingCodeRecord : Pos.SharedKernel.Domain.ICompanyOwned
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid NodeId { get; set; }

    public string CodeHash { get; set; } = string.Empty;

    public string DeviceKind { get; set; } = "TERMINAL";

    public Guid? PosTerminalId { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public Guid? UsedByDevice { get; set; }
}

internal sealed class DeviceStore(PosDbContext context) : IDeviceStore
{
    public void Add(Device device) => context.Add(device);

    public void AddPairingCode(Guid id, Guid companyId, Guid nodeId, string codeHash, DeviceKind kind, Guid? posTerminalId, Guid createdBy,
        DateTimeOffset createdAt, DateTimeOffset expiresAt) =>
        context.Add(new PairingCodeRecord
        {
            Id = id,
            CompanyId = companyId,
            NodeId = nodeId,
            CodeHash = codeHash,
            DeviceKind = kind == DeviceKind.Terminal ? "TERMINAL" : "ADMIN_WORKSTATION",
            PosTerminalId = posTerminalId,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        });

    public async Task<PendingPairingCode?> LockPairingCodeAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await context.Database.GetDbConnection().QuerySingleOrDefaultAsync<(Guid Id, Guid CompanyId, string Kind, Guid? Terminal, Guid CreatedBy)?>(
            new CommandDefinition(
                """
                SELECT id, company_id, device_kind, pos_terminal_id, created_by FROM org.device_pairing_codes
                WHERE code_hash = @codeHash AND used_at IS NULL AND expires_at > @now
                FOR UPDATE SKIP LOCKED
                """,
                new { codeHash, now },
                context.Database.CurrentTransaction?.GetDbTransaction(),
                cancellationToken: cancellationToken));
        return row is { } r
            ? new PendingPairingCode(r.Id, r.CompanyId, r.Kind == "TERMINAL" ? DeviceKind.Terminal : DeviceKind.AdminWorkstation, r.Terminal, r.CreatedBy)
            : null;
    }

    public Task MarkPairingCodeUsedAsync(Guid codeId, Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.Set<PairingCodeRecord>().Where(c => c.Id == codeId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.UsedAt, now).SetProperty(c => c.UsedByDevice, deviceId), cancellationToken);

    public Task<Device?> GetAsync(Guid deviceId, CancellationToken cancellationToken) =>
        context.Set<Device>().SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken cancellationToken)
    {
        var devices = await context.Set<Device>().AsNoTracking().OrderByDescending(d => d.PairedAt).ToListAsync(cancellationToken);
        var terminals = await context.Set<PosTerminal>().AsNoTracking().Where(t => t.DeviceId != null)
            .ToDictionaryAsync(t => t.DeviceId!.Value, t => t.Id, cancellationToken);
        return devices
            .Select(d => new DeviceDto(d.Id, d.Kind.ToString(), d.Hostname, terminals.TryGetValue(d.Id, out var t) ? t : null, d.Status.ToString(),
                d.PairedAt, d.LastSeenAt, d.AppVersion))
            .ToList();
    }
}

/// <summary>
/// Valida la credencial de un equipo en cada petición (hash en tiempo constante) y actualiza su última conexión como
/// máximo cada 5 minutos.
/// </summary>
internal sealed class DeviceAuthenticator(NpgsqlDataSource dataSource, IClock clock) : IDeviceAuthenticator
{
    private static readonly TimeSpan SeenResolution = TimeSpan.FromMinutes(5);

    public async Task<AuthenticatedDevice?> AuthenticateAsync(Guid deviceId, string secret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 100)
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition(
            """
            SELECT d.id AS Id, d.company_id AS CompanyId, d.kind AS Kind, d.credential_hash AS CredentialHash, d.status AS Status,
                   d.last_seen_at AS LastSeenAt, t.id AS PosTerminalId, t.branch_id AS BranchId
            FROM org.devices d
            LEFT JOIN org.pos_terminals t ON t.device_id = d.id AND t.deleted_at IS NULL
            WHERE d.id = @deviceId
            """,
            new { deviceId },
            cancellationToken: cancellationToken));

        if (row is not { Status: "ACTIVE", CredentialHash: { } hash } || !SecureTokens.Matches(secret, hash))
        {
            return null;
        }

        var now = clock.UtcNow;
        if (row.LastSeenAt is null || now.UtcDateTime - row.LastSeenAt.Value >= SeenResolution)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE org.devices SET last_seen_at = @now WHERE id = @deviceId", new { now, deviceId }, cancellationToken: cancellationToken));
        }

        var kind = row.Kind == "TERMINAL" ? DeviceKind.Terminal : DeviceKind.AdminWorkstation;
        return new AuthenticatedDevice(row.Id, row.CompanyId, kind, kind == DeviceKind.Terminal ? row.PosTerminalId : null, row.BranchId);
    }

    private sealed class DeviceRow
    {
        public Guid Id { get; set; }

        public Guid CompanyId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string? CredentialHash { get; set; }

        public string Status { get; set; } = string.Empty;

        public DateTime? LastSeenAt { get; set; }

        public Guid? PosTerminalId { get; set; }

        public Guid? BranchId { get; set; }
    }
}

internal sealed class TerminalDirectory(NpgsqlDataSource dataSource) : ITerminalDirectory
{
    private const string Columns =
        "id AS Id, company_id AS CompanyId, branch_id AS BranchId, code AS Code, status = 'ACTIVE' AS IsActive, warehouse_id AS WarehouseId";

    public async Task<TerminalInfo?> GetAsync(Guid posTerminalId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TerminalInfo>(new CommandDefinition(
            $"SELECT {Columns} FROM org.pos_terminals WHERE id = @posTerminalId AND deleted_at IS NULL",
            new { posTerminalId },
            cancellationToken: cancellationToken));
    }

    public async Task<TerminalInfo?> GetSingleActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var terminals = (await connection.QueryAsync<TerminalInfo>(new CommandDefinition(
            $"SELECT {Columns} FROM org.pos_terminals WHERE status = 'ACTIVE' AND deleted_at IS NULL LIMIT 2",
            cancellationToken: cancellationToken))).ToList();
        return terminals.Count == 1 ? terminals[0] : null;
    }
}

internal sealed class WarehouseDirectory(NpgsqlDataSource dataSource, IInstallationContext installation) : IWarehouseDirectory
{
    private const string Columns =
        "id AS Id, company_id AS CompanyId, branch_id AS BranchId, code AS Code, name AS Name, kind AS Kind, allows_sales AS AllowsSales, status = 'ACTIVE' AS IsActive";

    public async Task<WarehouseInfo?> GetAsync(Guid warehouseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<WarehouseInfo>(new CommandDefinition(
            $"SELECT {Columns} FROM org.warehouses WHERE id = @warehouseId AND deleted_at IS NULL",
            new { warehouseId },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<WarehouseInfo>> ListByBranchAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<WarehouseInfo>(new CommandDefinition(
            $"SELECT {Columns} FROM org.warehouses WHERE branch_id = @branchId AND deleted_at IS NULL ORDER BY code",
            new { branchId },
            cancellationToken: cancellationToken))).ToList();
    }

    public async Task<Guid?> FindBranchIdByCodeAsync(string branchCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM org.branches WHERE company_id = @companyId AND code = @code AND deleted_at IS NULL",
            new { companyId = installation.CompanyId, code = (branchCode ?? string.Empty).Trim().ToUpperInvariant() },
            cancellationToken: cancellationToken));
    }

    public async Task<short> GetLocalNodeNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<short?>(new CommandDefinition(
                "SELECT number FROM org.nodes WHERE id = @nodeId", new { nodeId = installation.NodeId }, cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException("El nodo local no está registrado: complete el asistente inicial.");
    }
}
