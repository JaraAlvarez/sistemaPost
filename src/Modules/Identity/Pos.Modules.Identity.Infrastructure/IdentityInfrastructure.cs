using System.Net;
using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Identity.Application;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Identity.Infrastructure;

/// <summary>Fila de solo lectura del catálogo <c>identity.permissions</c> (se escribe únicamente por migraciones).</summary>
internal sealed class PermissionRecord
{
    public string Code { get; set; } = string.Empty;

    public string Module { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsSensitive { get; set; }

    public bool IsDeprecated { get; set; }
}

internal sealed class UserSessionRecord : Pos.SharedKernel.Domain.ICompanyOwned
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid NodeId { get; set; }

    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public string Kind { get; set; } = "BACKOFFICE";

    public Guid? DeviceId { get; set; }

    public Guid? PosTerminalId { get; set; }

    public Guid BranchId { get; set; }

    public IPAddress? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public long SecurityVersion { get; set; }

    public int IdleTimeoutSeconds { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public string? RevokedReason { get; set; }
}

internal sealed class LoginAttemptRecord
{
    public Guid Id { get; set; }

    public Guid? CompanyId { get; set; }

    public Guid NodeId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string IdentifierAttempted { get; set; } = string.Empty;

    public Guid? UserId { get; set; }

    public Guid? DeviceId { get; set; }

    public IPAddress? IpAddress { get; set; }

    public bool Succeeded { get; set; }

    public string? FailureReason { get; set; }
}

internal sealed class PasswordHistoryRecord
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string PasswordHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class AuthorizationGrantRecord : Pos.SharedKernel.Domain.ICompanyOwned
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid NodeId { get; set; }

    public string PermissionCode { get; set; } = string.Empty;

    public Guid RequestedBy { get; set; }

    public Guid AuthorizedBy { get; set; }

    public Guid? PosTerminalId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? TargetType { get; set; }

    public Guid? TargetId { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset GrantedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public string? ConsumedByRequest { get; set; }
}

internal sealed class IdentityModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users", "identity");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.LockedReason).HasConversion(new UpperSnakeEnumConverter<LockReason>());
            b.HasControlColumns();
            b.HasXminConcurrency();
            b.OwnsMany(x => x.Roles, r =>
            {
                r.ToTable("user_roles", "identity");
                r.WithOwner().HasForeignKey("UserId");
                r.HasKey(x => x.Id);
                r.Property(x => x.Id).ValueGeneratedNever();
            });
            b.Navigation(x => x.Roles).HasField("_roles");
            b.OwnsMany(x => x.Overrides, o =>
            {
                o.ToTable("user_permission_overrides", "identity");
                o.WithOwner().HasForeignKey("UserId");
                o.HasKey(x => x.Id);
                o.Property(x => x.Id).ValueGeneratedNever();
                o.Property(x => x.Effect).HasConversion(new UpperSnakeEnumConverter<OverrideEffect>());
                o.Property<DateTimeOffset>(ModelConventions.CreatedAt);
                o.Property<Guid>(ModelConventions.CreatedBy);
                o.Property<DateTimeOffset?>(ModelConventions.UpdatedAt);
                o.Property<Guid?>(ModelConventions.UpdatedBy);
            });
            b.Navigation(x => x.Overrides).HasField("_overrides");
        });

        modelBuilder.Entity<Role>(b =>
        {
            b.ToTable("roles", "identity");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Ignore(x => x.PermissionCodes);
            b.HasControlColumns();
            b.HasXminConcurrency();
            b.OwnsMany(x => x.Permissions, p =>
            {
                p.ToTable("role_permissions", "identity");
                p.WithOwner().HasForeignKey("RoleId");
                p.Property(x => x.PermissionCode).HasColumnName("permission_code");
                p.HasKey("RoleId", nameof(RoleGrant.PermissionCode));
            });
            b.Navigation(x => x.Permissions).HasField("_permissions");
        });

        modelBuilder.Entity<Employee>(b =>
        {
            b.ToTable("employees", "identity");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PermissionRecord>(b =>
        {
            b.ToTable("permissions", "identity");
            b.HasKey(x => x.Code);
        });

        modelBuilder.Entity<UserSessionRecord>(b =>
        {
            b.ToTable("user_sessions", "identity");
            b.HasKey(x => x.Id);
            b.Property(x => x.IpAddress).HasColumnType("inet");
            b.Property(x => x.TokenHash).HasColumnType("char(64)");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LoginAttemptRecord>(b =>
        {
            b.ToTable("login_attempts", "identity");
            b.HasKey(x => x.Id);
            b.Property(x => x.IpAddress).HasColumnType("inet");
        });

        // Relaciones declaradas para que EF inserte primero el usuario (mismo SaveChanges).
        modelBuilder.Entity<PasswordHistoryRecord>(b =>
        {
            b.ToTable("password_history", "identity");
            b.HasKey(x => x.Id);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthorizationGrantRecord>(b =>
        {
            b.ToTable("authorization_grants", "identity");
            b.HasKey(x => x.Id);
        });
    }
}

internal sealed class IdentityStore(PosDbContext context) : IIdentityStore
{
    public void Add(User user) => context.Add(user);

    public void Add(Role role) => context.Add(role);

    public void Add(Employee employee) => context.Add(employee);

    public void Remove(Role role) => context.Remove(role);

    public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken) =>
        context.Set<User>().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
        context.Set<User>().SingleOrDefaultAsync(u => u.Username == username && u.Kind == UserKind.Human, cancellationToken);

    public Task<User?> FindByPosCodeAsync(string posCode, CancellationToken cancellationToken) =>
        context.Set<User>().SingleOrDefaultAsync(u => u.PosCode == posCode && u.Kind == UserKind.Human, cancellationToken);

    public Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken) =>
        context.Set<User>().AnyAsync(u => u.Username == username, cancellationToken);

    public Task<bool> PosCodeExistsAsync(string posCode, Guid? exceptUserId, CancellationToken cancellationToken) =>
        context.Set<User>().AnyAsync(u => u.PosCode == posCode && u.Id != exceptUserId, cancellationToken);

    public Task<Role?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
        context.Set<Role>().SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);

    public async Task<Role?> GetRoleByCodeAsync(string code, CancellationToken cancellationToken) =>
        context.ChangeTracker.Entries<Role>().Select(e => e.Entity).FirstOrDefault(r => r.Code == code)
        ?? await context.Set<Role>().SingleOrDefaultAsync(r => r.Code == code, cancellationToken);

    public async Task<IReadOnlyList<Role>> GetRolesAsync(IEnumerable<Guid> roleIds, CancellationToken cancellationToken)
    {
        var ids = roleIds.ToList();
        return await context.Set<Role>().Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Role>> GetSystemRolesAsync(CancellationToken cancellationToken) =>
        await context.Set<Role>().Where(r => r.IsSystem).ToListAsync(cancellationToken);

    public Task<bool> RoleCodeExistsAsync(string code, CancellationToken cancellationToken) =>
        context.Set<Role>().AnyAsync(r => r.Code == code, cancellationToken);

    public Task<Employee?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken) =>
        context.Set<Employee>().SingleOrDefaultAsync(e => e.Id == employeeId, cancellationToken);

    public async Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken cancellationToken) =>
        await context.Set<PermissionRecord>().AsNoTracking()
            .OrderBy(p => p.Code)
            .Select(p => new PermissionDto(p.Code, p.Module, p.Description, p.IsSensitive, p.IsDeprecated))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> ListActivePermissionCodesAsync(CancellationToken cancellationToken) =>
        await context.Set<PermissionRecord>().AsNoTracking()
            .Where(p => !p.IsDeprecated)
            .OrderBy(p => p.Code)
            .Select(p => p.Code)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var roles = await context.Set<Role>().AsNoTracking().OrderBy(r => r.Code).ToListAsync(cancellationToken);
        return roles.Select(RoleRules.ToDto).ToList();
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var users = await context.Set<User>().AsNoTracking().Where(u => u.Kind == UserKind.Human).OrderBy(u => u.Username).ToListAsync(cancellationToken);
        var roles = await context.Set<Role>().AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Code, cancellationToken);
        return users.Select(u => ToDto(u, roles)).ToList();
    }

    public async Task<UserDto?> GetUserDtoAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await context.Set<User>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.Kind == UserKind.Human, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var roles = await context.Set<Role>().AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Code, cancellationToken);
        return ToDto(user, roles);
    }

    public async Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(CancellationToken cancellationToken)
    {
        var employees = await context.Set<Employee>().AsNoTracking().OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync(cancellationToken);
        return employees
            .Select(e => new EmployeeDto(e.Id, e.BranchId, e.IdentificationType, e.IdentificationNumber, e.FirstName, e.LastName, e.Phone, e.Email,
                e.Status.ToString()))
            .ToList();
    }

    public async Task<int> CountActiveAdministratorsAsync(Guid? excludingUserId, CancellationToken cancellationToken)
    {
        var adminRoles = await context.Set<Role>()
            .Where(r => r.IsSystem && PrivilegeGuard.AdministratorRoles.Contains(r.Code))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        return await context.Set<User>()
            .Where(u => u.Kind == UserKind.Human && u.Status != UserStatus.Disabled && u.Id != excludingUserId)
            .CountAsync(u => u.Roles.Any(r => r.BranchId == null && adminRoles.Contains(r.RoleId)), cancellationToken);
    }

    public async Task<bool> HasActiveOwnerAsync(CancellationToken cancellationToken)
    {
        var owner = await context.Set<Role>().Where(r => r.IsSystem && r.Code == SystemRoles.Owner).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken);
        return owner is not null && await context.Set<User>()
            .AnyAsync(u => u.Kind == UserKind.Human && u.Status != UserStatus.Disabled && u.Roles.Any(r => r.RoleId == owner), cancellationToken);
    }

    public async Task BumpSecurityVersionForRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var users = await context.Set<User>().Where(u => u.Roles.Any(r => r.RoleId == roleId)).ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            user.BumpSecurityVersion();
        }
    }

    public void AddPasswordHistory(Guid historyId, Guid userId, string passwordHash, DateTimeOffset now) =>
        context.Add(new PasswordHistoryRecord { Id = historyId, UserId = userId, PasswordHash = passwordHash, CreatedAt = now });

    public async Task<IReadOnlyList<string>> RecentPasswordHashesAsync(Guid userId, int count, CancellationToken cancellationToken) =>
        await context.Set<PasswordHistoryRecord>().AsNoTracking()
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.CreatedAt)
            .Take(count)
            .Select(h => h.PasswordHash)
            .ToListAsync(cancellationToken);

    private static UserDto ToDto(User u, Dictionary<Guid, string> roles) => new(
        u.Id, u.Username, u.DisplayName, u.Email, u.Status.ToString(), u.PosCode, u.PinHash is not null, u.MustChangePassword, u.EmployeeId,
        u.LastLoginAt,
        [.. u.Roles.Select(r => new UserRoleDto(r.RoleId, roles.GetValueOrDefault(r.RoleId, "?"), r.BranchId))],
        [.. u.Overrides.Select(o => new UserOverrideDto(o.PermissionCode, o.Effect.ToString(), o.BranchId, o.Reason))]);
}

internal sealed class SessionStore(PosDbContext context, Pos.SharedKernel.Time.IClock clock) : ISessionStore, ILoginAttemptLog, IAuthorizationGrantStore
{
    public void Add(NewSession s) => context.Add(new UserSessionRecord
    {
        Id = s.Id,
        CompanyId = s.CompanyId,
        NodeId = s.NodeId,
        UserId = s.UserId,
        TokenHash = s.TokenHash,
        Kind = s.IsTerminal ? "TERMINAL" : "BACKOFFICE",
        DeviceId = s.DeviceId,
        PosTerminalId = s.PosTerminalId,
        BranchId = s.BranchId,
        IpAddress = s.IpAddress,
        UserAgent = s.UserAgent is { Length: > 200 } ua ? ua[..200] : s.UserAgent,
        SecurityVersion = s.SecurityVersion,
        IdleTimeoutSeconds = s.IdleTimeoutSeconds,
        CreatedAt = s.CreatedAt,
        LastActivityAt = s.CreatedAt,
        ExpiresAt = s.ExpiresAt,
    });

    public void Add(LoginAttempt a) => context.Add(new LoginAttemptRecord
    {
        Id = a.Id,
        CompanyId = a.CompanyId,
        NodeId = a.NodeId,
        OccurredAt = a.OccurredAt,
        Kind = a.Kind,
        IdentifierAttempted = a.Identifier,
        UserId = a.UserId,
        DeviceId = a.DeviceId,
        IpAddress = a.IpAddress,
        Succeeded = a.Succeeded,
        FailureReason = a.FailureReason,
    });

    public void Add(NewAuthorizationGrant g) => context.Add(new AuthorizationGrantRecord
    {
        Id = g.Id,
        CompanyId = g.CompanyId,
        NodeId = g.NodeId,
        PermissionCode = g.PermissionCode,
        RequestedBy = g.RequestedBy,
        AuthorizedBy = g.AuthorizedBy,
        PosTerminalId = g.PosTerminalId,
        Action = g.Action,
        TargetType = g.TargetType,
        TargetId = g.TargetId,
        Reason = g.Reason,
        GrantedAt = g.GrantedAt,
        ExpiresAt = g.ExpiresAt,
    });

    public async Task<bool> RevokeAsync(Guid sessionId, Guid? revokedBy, string reason, CancellationToken cancellationToken) =>
        await context.Set<UserSessionRecord>()
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.RevokedAt, clock.UtcNow).SetProperty(s => s.RevokedBy, revokedBy).SetProperty(s => s.RevokedReason, reason),
                cancellationToken) > 0;

    public Task<int> RevokeAllForUserAsync(Guid userId, Guid? revokedBy, string reason, Guid? exceptSessionId, CancellationToken cancellationToken) =>
        context.Set<UserSessionRecord>()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != exceptSessionId)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.RevokedAt, clock.UtcNow).SetProperty(s => s.RevokedBy, revokedBy).SetProperty(s => s.RevokedReason, reason),
                cancellationToken);

    public async Task<IReadOnlyList<SessionDto>> ListActiveAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await context.Set<UserSessionRecord>().AsNoTracking()
            .Where(s => s.RevokedAt == null && s.ExpiresAt > now)
            .Join(context.Set<User>().AsNoTracking(), s => s.UserId, u => u.Id, (s, u) => new { s, u.Username })
            .OrderByDescending(x => x.s.LastActivityAt)
            .ToListAsync(cancellationToken);
        return rows
            .Where(x => x.s.LastActivityAt.AddSeconds(x.s.IdleTimeoutSeconds) > now)
            .Select(x => new SessionDto(x.s.Id, x.s.UserId, x.Username, x.s.Kind, x.s.DeviceId, x.s.PosTerminalId, x.s.IpAddress?.ToString(),
                x.s.CreatedAt, x.s.LastActivityAt, x.s.ExpiresAt))
            .ToList();
    }
}

internal sealed class IdentityConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_users__company_username"] = IdentityErrors.UsernameDuplicated,
        ["ux_users__company_pos_code"] = IdentityErrors.PosCodeDuplicated,
        ["ux_users__employee"] = Error.Conflict("IDENTITY.EMPLOYEE_ALREADY_LINKED", "El empleado ya está vinculado a otro usuario."),
        ["fk_users__employee"] = IdentityErrors.EmployeeNotFound,
        ["ux_roles__company_code"] = Error.Conflict("IDENTITY.ROLE_CODE_DUPLICATED", "Ya existe un rol con ese código."),
        ["fk_role_permissions__permission"] = Error.Validation("IDENTITY.UNKNOWN_PERMISSION", "Uno de los permisos no existe en el catálogo."),
        ["fk_user_permission_overrides__permission"] = Error.Validation("IDENTITY.UNKNOWN_PERMISSION", "Uno de los permisos no existe en el catálogo."),
        ["fk_user_roles__branch"] = Error.NotFound("ORGANIZATION.BRANCH_NOT_FOUND", "La sucursal no existe."),
        ["ux_employees__company_identification"] =
            Error.Conflict("IDENTITY.EMPLOYEE_DUPLICATED", "Ya existe un empleado con esa identificación."),
        ["fk_employees__identification_type"] =
            Error.Validation("IDENTITY.UNKNOWN_IDENTIFICATION_TYPE", "El tipo de identificación no existe."),
    };
}
