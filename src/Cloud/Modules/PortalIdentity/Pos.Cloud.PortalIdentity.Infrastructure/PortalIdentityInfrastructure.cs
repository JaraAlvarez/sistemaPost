using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.PortalIdentity.Application;
using Pos.Cloud.PortalIdentity.Domain;
using Pos.Infrastructure;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.PortalIdentity.Infrastructure;

internal sealed class PortalIdentityModelContributor : ICloudModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PortalUser>(b =>
        {
            b.ToTable("portal_users", "portal");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Role).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Ignore(x => x.AuditLabel);
            b.HasControlColumns();
        });

        modelBuilder.Entity<PortalSession>(b =>
        {
            b.ToTable("portal_sessions", "portal");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TokenHash).HasColumnType("char(64)");
            b.Property(x => x.Stage).HasUpperSnakeConversion();
            b.Property(x => x.Channel).HasUpperSnakeConversion();
            b.Property(x => x.IpAddress).HasColumnType("inet");
            b.HasOne<PortalUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class PortalIdentityConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_portal_users__email"] = PortalIdentityErrors.EmailDuplicated,
        ["fk_portal_users__reseller_account"] = Error.NotFound("PORTAL.RESELLER_ACCOUNT_NOT_FOUND", "La cuenta del distribuidor no existe."),
    };
}

internal sealed class PortalIdentityStore(CloudDbContext context) : IPortalIdentityStore
{
    public void Add(PortalUser user) => context.Add(user);

    public void Add(PortalSession session) => context.Add(session);

    public Task<PortalUser?> GetUserAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<PortalUser>().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<PortalUser?> FindUserByEmailAsync(string email, CancellationToken cancellationToken) =>
        context.Set<PortalUser>().SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

    public async Task<IReadOnlyList<PortalUser>> ListUsersAsync(CancellationToken cancellationToken) =>
        await context.Set<PortalUser>().ToListAsync(cancellationToken);

    public Task<bool> AnyActiveSuperadminAsync(Guid? except, CancellationToken cancellationToken) =>
        context.Set<PortalUser>().AnyAsync(
            u => u.Kind == PortalUserKind.Human && u.Role == PortalRole.Superadmin && u.Status == PortalUserStatus.Active && u.Id != except,
            cancellationToken);

    public Task<PortalSession?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        context.Set<PortalSession>().SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

    public Task<PortalSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<PortalSession>().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PortalSession>> GetOpenSessionsAsync(Guid userId, CancellationToken cancellationToken) =>
        await context.Set<PortalSession>().Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PortalSession>> ListSessionsAsync(Guid? userId, int limit, CancellationToken cancellationToken) =>
        await context.Set<PortalSession>().AsNoTracking()
            .Where(s => userId == null || s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// Secreto TOTP cifrado con la protección de datos de ASP.NET Core. Las llaves viven en un directorio del servidor (volumen
/// aparte), no en la BD: un respaldo o una copia de la BD no revela los secretos. Si se pierden las llaves, los usuarios
/// deben volver a enrolar su autenticador (el superadministrador lo restablece por consola).
/// </summary>
internal sealed class DataProtectionTotpProtector(IDataProtectionProvider provider) : ITotpSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Pos.Cloud.PortalIdentity.TotpSecret.v1");

    public string Protect(byte[] secret) => _protector.Protect(Convert.ToBase64String(secret));

    public byte[]? Unprotect(string protectedSecret)
    {
        try
        {
            return Convert.FromBase64String(_protector.Unprotect(protectedSecret));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public static class PortalIdentityInfrastructureRegistration
{
    public static void Register(IServiceCollection services, PortalIdentityOptions options)
    {
        services.AddSingleton(options);
        services.AddRequestHandlersFrom(typeof(IPortalIdentityStore).Assembly);
        services.AddSingleton<ICloudModelContributor, PortalIdentityModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, PortalIdentityConstraintErrors>();
        services.AddScoped<IPortalIdentityStore, PortalIdentityStore>();
        services.AddScoped<PortalAuthServices>();
        services.AddScoped<PortalSessionAuthenticator>();
        services.TryAddSingleton<ITotpSecretProtector, DataProtectionTotpProtector>();
        services.Replace(ServiceDescriptor.Scoped<IPermissionChecker, PortalPermissionChecker>());
    }
}
