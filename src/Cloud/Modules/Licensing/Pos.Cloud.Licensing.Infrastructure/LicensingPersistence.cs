using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.Licensing.Domain;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Licensing.Infrastructure;

internal sealed class LicensingModelContributor : ICloudModelContributor
{
    private static readonly ValueConverter<LicenseEdition, string> EditionConverter = new(
        v => v == LicenseEdition.SingleTerminal ? "SINGLE" : "MULTI",
        v => v == "SINGLE" ? LicenseEdition.SingleTerminal : LicenseEdition.MultiTerminal);

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(b =>
        {
            b.ToTable("accounts", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.NitCheckDigit).HasColumnType("char(1)");
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.ParentAccountId).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.AuditLabel);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Organization>(b =>
        {
            b.ToTable("organizations", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.NitCheckDigit).HasColumnType("char(1)");
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.NitNumber);
            b.Ignore(x => x.AuditLabel);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Subscription>(b =>
        {
            b.ToTable("subscriptions", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Edition).HasConversion(EditionConverter);
            b.Property(x => x.BillingPeriod).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.GraceDays).HasConversion<short>();
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Events).HasField("_events");
            b.Ignore(x => x.ValidUntil);
            b.Ignore(x => x.GraceUntil);
            b.Ignore(x => x.IsPaid);
            b.Ignore(x => x.AllowsActivation);
            b.Ignore(x => x.AuditLabel);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<SubscriptionEvent>(b =>
        {
            b.ToTable("subscription_events", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasUpperSnakeConversion();
        });

        modelBuilder.Entity<License>(b =>
        {
            b.ToTable("licenses", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.KeyHash).HasColumnType("char(64)");
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.IsActive);
            b.Ignore(x => x.AuditLabel);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Installation>(b =>
        {
            b.ToTable("installations", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PosInstallationId).HasColumnName("installation_id");
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.LastIp).HasColumnType("inet");
            b.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Devices).WithOne().HasForeignKey(x => x.InstallationId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Devices).HasField("_devices");
            b.HasMany(x => x.Activations).WithOne().HasForeignKey(x => x.InstallationId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Activations).HasField("_activations");
            b.Ignore(x => x.ActiveActivation);
            b.Ignore(x => x.ActiveDevice);
            b.Ignore(x => x.AuditLabel);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Device>(b =>
        {
            b.ToTable("devices", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Role).HasUpperSnakeConversion();
        });

        modelBuilder.Entity<Activation>(b =>
        {
            b.ToTable("activations", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.AuditLabel);
        });

        modelBuilder.Entity<Checkin>(b =>
        {
            b.ToTable("checkins", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Result).HasUpperSnakeConversion();
            b.Property(x => x.SubscriptionStatus).HasConversion(new UpperSnakeEnumConverter<SubscriptionStatus>());
            b.Property(x => x.IpAddress).HasColumnType("inet");
            b.HasOne<Installation>().WithMany().HasForeignKey(x => x.InstallationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Activation>().WithMany().HasForeignKey(x => x.ActivationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SigningKey>(b =>
        {
            b.ToTable("signing_keys", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("kid").ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Ignore(x => x.Kid);
            b.Ignore(x => x.IsTrusted);
        });
    }
}

internal sealed class LicensingConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_organizations__nit"] = LicensingErrors.NitDuplicated,
        ["ux_subscriptions__organization_current"] = LicensingErrors.SubscriptionExists,
        ["ux_licenses__organization_active"] = LicensingErrors.LicenseExists,
        ["ux_activations__installation_active"] = LicenseApiErrors.InstallationActiveOnOtherDevice,
        ["ux_activations__device_active"] = LicenseApiErrors.InstallationActiveOnOtherDevice,
        ["ux_installations__installation_id"] = Error.Conflict("LICENSING.INSTALLATION_DUPLICATED", "La instalación ya está registrada; reintente la activación."),
    };
}

internal sealed class LicensingStore(CloudDbContext context) : ILicensingStore
{
    public void Add(Account account) => context.Add(account);

    public void Add(Organization organization) => context.Add(organization);

    public void Add(Subscription subscription) => context.Add(subscription);

    public void Add(License license) => context.Add(license);

    public void Add(Installation installation) => context.Add(installation);

    public void Add(Checkin checkin) => context.Add(checkin);

    public void Add(SigningKey key) => context.Add(key);

    public Task<Account?> GetAccountAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Account>().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<Organization?> GetOrganizationAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Organization>().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    public Task<bool> NitExistsAsync(string nit, CancellationToken cancellationToken) =>
        context.Set<Organization>().AnyAsync(o => o.Nit == nit, cancellationToken);

    public Task<Subscription?> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Subscription>().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<Subscription?> GetCurrentSubscriptionAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.Set<Subscription>().SingleOrDefaultAsync(s => s.OrganizationId == organizationId && s.Status != SubscriptionStatus.Cancelled, cancellationToken);

    public async Task<IReadOnlyList<Subscription>> GetSubscriptionsToRefreshAsync(CancellationToken cancellationToken) =>
        await context.Set<Subscription>()
            .Where(s => s.Status == SubscriptionStatus.Trial || s.Status == SubscriptionStatus.Active
                        || s.Status == SubscriptionStatus.PastDue || s.Status == SubscriptionStatus.Expired)
            .ToListAsync(cancellationToken);

    public Task<License?> GetLicenseAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<License>().SingleOrDefaultAsync(l => l.Id == id, cancellationToken);

    public Task<License?> GetActiveLicenseAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.Set<License>().SingleOrDefaultAsync(l => l.OrganizationId == organizationId && l.Status == LicenseStatus.Active, cancellationToken);

    public async Task<IReadOnlyList<License>> FindLicensesByPrefixAsync(string keyPrefix, CancellationToken cancellationToken) =>
        await context.Set<License>().Where(l => l.KeyPrefix == keyPrefix).ToListAsync(cancellationToken);

    public async Task LockLicenseAsync(Guid licenseId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM licensing.licenses WHERE id = {licenseId} FOR UPDATE", cancellationToken);

    public Task<Installation?> FindInstallationAsync(Guid posInstallationId, CancellationToken cancellationToken) =>
        Installations().SingleOrDefaultAsync(i => i.PosInstallationId == posInstallationId, cancellationToken);

    public Task<Installation?> FindInstallationByActivationAsync(Guid activationId, CancellationToken cancellationToken) =>
        Installations().SingleOrDefaultAsync(i => i.Activations.Any(a => a.Id == activationId), cancellationToken);

    public async Task<IReadOnlyList<Installation>> GetInstallationsOfLicenseAsync(Guid licenseId, CancellationToken cancellationToken) =>
        await Installations().Where(i => i.LicenseId == licenseId).ToListAsync(cancellationToken);

    public Task<int> CountActiveInstallationsAsync(Guid licenseId, CancellationToken cancellationToken) =>
        context.Set<Installation>().CountAsync(i => i.LicenseId == licenseId && i.Status == InstallationStatus.Active, cancellationToken);

    public Task<bool> HasActiveStoreServersAsync(Guid organizationId, CancellationToken cancellationToken) =>
        (from installation in context.Set<Installation>()
         from activation in installation.Activations
         join device in context.Set<Device>() on activation.DeviceId equals device.Id
         where installation.OrganizationId == organizationId && activation.Status == ActivationStatus.Active && device.Role == DeviceRole.StoreServer
         select activation.Id).AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<SigningKey>> GetSigningKeysAsync(CancellationToken cancellationToken) =>
        await context.Set<SigningKey>().ToListAsync(cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);

    private IQueryable<Installation> Installations() =>
        context.Set<Installation>().Include(i => i.Devices).Include(i => i.Activations).AsSplitQuery();
}
