using Microsoft.EntityFrameworkCore;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.Licensing.Domain;
using Pos.Infrastructure.Persistence;

namespace Pos.Cloud.Licensing.Infrastructure;

/// <summary>Lecturas del portal: entidades sin seguimiento convertidas a DTO; el estado efectivo se calcula con el dominio.</summary>
internal sealed class LicensingReadModel(CloudDbContext context) : ILicensingReadModel
{
    public async Task<IReadOnlyList<AccountDto>> ListAccountsAsync(string? search, CancellationToken cancellationToken)
    {
        var query = context.Set<Account>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(a => EF.Functions.ILike(a.Name, pattern) || (a.Nit != null && EF.Functions.ILike(a.Nit, pattern))
                                     || (a.ContactEmail != null && EF.Functions.ILike(a.ContactEmail, pattern)));
        }

        var accounts = await query.OrderBy(a => a.Name).Take(500).ToListAsync(cancellationToken);
        var counts = await OrganizationCountsAsync(accounts.Select(a => a.Id).ToList(), cancellationToken);
        return [.. accounts.Select(a => ToDto(a, counts.GetValueOrDefault(a.Id)))];
    }

    public async Task<AccountDto?> GetAccountAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await context.Set<Account>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return account is null ? null : ToDto(account, (await OrganizationCountsAsync([id], cancellationToken)).GetValueOrDefault(id));
    }

    public async Task<IReadOnlyList<OrganizationRowDto>> ListOrganizationsAsync(Guid? accountId, string? search, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var query = context.Set<Organization>().AsNoTracking();
        if (accountId is { } account)
        {
            query = query.Where(o => o.AccountId == account);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(o => EF.Functions.ILike(o.LegalName, pattern) || EF.Functions.ILike(o.Nit, pattern) || (o.City != null && EF.Functions.ILike(o.City, pattern)));
        }

        var organizations = await query.OrderBy(o => o.LegalName).Take(500).ToListAsync(cancellationToken);
        return await RowsAsync(organizations, now, cancellationToken);
    }

    public async Task<OrganizationDetailDto?> GetOrganizationAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var organization = await context.Set<Organization>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (organization is null)
        {
            return null;
        }

        var row = (await RowsAsync([organization], now, cancellationToken))[0];
        var account = (await GetAccountAsync(organization.AccountId, cancellationToken))!;
        var subscriptionIds = await context.Set<Subscription>().AsNoTracking().Where(s => s.OrganizationId == id).Select(s => s.Id).ToListAsync(cancellationToken);
        var users = await UserNamesAsync(cancellationToken);
        var events = await context.Set<SubscriptionEvent>().AsNoTracking()
            .Where(e => subscriptionIds.Contains(e.SubscriptionId))
            .OrderByDescending(e => e.OccurredAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        var licenses = await context.Set<License>().AsNoTracking().Where(l => l.OrganizationId == id).OrderByDescending(l => l.IssuedAt).ToListAsync(cancellationToken);
        var installations = await InstallationsAsync(context.Set<Installation>().Where(i => i.OrganizationId == id), cancellationToken);
        var installationIds = installations.Select(i => i.Id).ToList();
        var checkins = await context.Set<Checkin>().AsNoTracking()
            .Where(c => installationIds.Contains(c.InstallationId))
            .OrderByDescending(c => c.OccurredAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        return new OrganizationDetailDto(
            row,
            account,
            [.. events.Select(e => new SubscriptionEventDto(
                e.Id, Code(e.Type), e.OccurredAt, users.GetValueOrDefault(e.ActorId) ?? e.ActorId.ToString(), e.PaymentReference, e.PeriodStart, e.PeriodEnd,
                e.OldValue, e.NewValue, e.Reason))],
            [.. licenses.Select(l => new LicenseDto(
                l.Id, l.SubscriptionId, l.KeyPrefix, Code(l.Status), l.MaxInstallations, l.IssuedAt, l.RevokedAt, l.RevokedReason, l.ReplacedBy,
                installations.Count(i => i.Status == "ACTIVE" && i.Activations.Any(a => a.Status == "ACTIVE" && a.LicenseId == l.Id))))],
            [.. installations.Select(i => i with { OrganizationName = organization.LegalName })],
            [.. checkins.Select(c => new CheckinDto(
                c.Id, c.InstallationId, c.OccurredAt, c.AppVersion, c.ActiveTerminals, c.ReportedClock, c.IpAddress?.ToString(), Code(c.Result), c.RejectionCode,
                c.SubscriptionStatus?.ToCode(), c.TokenValidUntil, c.AuditSealNo, c.AuditSealCode, c.AuditSealedAt))]);
    }

    public async Task<IReadOnlyList<SubscriptionRowDto>> ListSubscriptionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await (from subscription in context.Set<Subscription>().AsNoTracking()
                          join organization in context.Set<Organization>().AsNoTracking() on subscription.OrganizationId equals organization.Id
                          where subscription.Status != SubscriptionStatus.Cancelled
                          select new { subscription, organization })
            .ToListAsync(cancellationToken);
        return [.. rows
            .Select(r => ToRow(r.subscription, r.organization, now))
            .OrderBy(r => r.ValidUntil)];
    }

    public async Task<IReadOnlyList<InstallationDto>> ListInstallationsAsync(Guid? organizationId, string? search, CancellationToken cancellationToken)
    {
        var query = context.Set<Installation>().AsQueryable();
        if (organizationId is { } id)
        {
            query = query.Where(i => i.OrganizationId == id);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            var matching = context.Set<Organization>().Where(o => EF.Functions.ILike(o.LegalName, pattern) || EF.Functions.ILike(o.Nit, pattern)).Select(o => o.Id);
            query = query.Where(i => (i.BranchName != null && EF.Functions.ILike(i.BranchName, pattern)) || matching.Contains(i.OrganizationId));
        }

        var installations = await InstallationsAsync(query, cancellationToken);
        var names = await context.Set<Organization>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.LegalName, cancellationToken);
        return [.. installations.Select(i => i with { OrganizationName = names.GetValueOrDefault(i.OrganizationId) ?? string.Empty })];
    }

    public async Task<(int Accounts, int Organizations)> CountAsync(CancellationToken cancellationToken) =>
        (await context.Set<Account>().CountAsync(cancellationToken), await context.Set<Organization>().CountAsync(cancellationToken));

    internal static SubscriptionRowDto ToRow(Subscription subscription, Organization organization, DateTimeOffset now)
    {
        var status = subscription.StatusAt(now);
        return new SubscriptionRowDto(
            subscription.Id, organization.Id, organization.LegalName, organization.NitNumber.ToString(), subscription.Edition.ToCode(), Code(subscription.BillingPeriod),
            status.ToCode(), subscription.ValidUntil, subscription.GraceUntil, (int)Math.Floor((subscription.ValidUntil - now).TotalDays), subscription.SuspendedReason);
    }

    private async Task<List<OrganizationRowDto>> RowsAsync(List<Organization> organizations, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = organizations.Select(o => o.Id).ToList();
        var accountIds = organizations.Select(o => o.AccountId).Distinct().ToList();
        var accounts = await context.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);
        var subscriptions = await context.Set<Subscription>().AsNoTracking()
            .Where(s => ids.Contains(s.OrganizationId) && s.Status != SubscriptionStatus.Cancelled)
            .ToDictionaryAsync(s => s.OrganizationId, cancellationToken);
        var licenses = await context.Set<License>().AsNoTracking()
            .Where(l => ids.Contains(l.OrganizationId) && l.Status == LicenseStatus.Active)
            .ToDictionaryAsync(l => l.OrganizationId, l => l.KeyPrefix, cancellationToken);
        var installed = await context.Set<Installation>().AsNoTracking()
            .Where(i => ids.Contains(i.OrganizationId) && i.Status == InstallationStatus.Active)
            .GroupBy(i => i.OrganizationId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

        return [.. organizations.Select(o => new OrganizationRowDto(
            o.Id, o.AccountId, accounts.GetValueOrDefault(o.AccountId) ?? string.Empty, o.LegalName, o.NitNumber.ToString(), o.City, Code(o.Status),
            subscriptions.TryGetValue(o.Id, out var s) ? Summary(s, now) : null,
            licenses.GetValueOrDefault(o.Id),
            installed.GetValueOrDefault(o.Id)))];
    }

    private static SubscriptionSummaryDto Summary(Subscription s, DateTimeOffset now) => new(
        s.Id, s.Edition.ToCode(), Code(s.BillingPeriod), s.Status.ToCode(), s.StatusAt(now).ToCode(), s.TrialEndsAt, s.CurrentPeriodStart, s.CurrentPeriodEnd,
        s.ValidUntil, s.GraceDays, s.GraceUntil, s.SuspendedReason);

    private async Task<List<InstallationDto>> InstallationsAsync(IQueryable<Installation> query, CancellationToken cancellationToken)
    {
        var installations = await query.AsNoTracking().Include(i => i.Devices).Include(i => i.Activations).AsSplitQuery()
            .OrderBy(i => i.BranchName).Take(1000).ToListAsync(cancellationToken);
        var users = await UserNamesAsync(cancellationToken);
        return [.. installations.Select(i => new InstallationDto(
            i.Id, i.PosInstallationId, i.OrganizationId, string.Empty, i.BranchName, i.AppVersion, Code(i.Status), i.FirstActivatedAt, i.LastCheckinAt,
            i.LastIp?.ToString(), i.ActiveTerminals,
            [.. i.Devices.OrderByDescending(d => d.LastSeenAt).Select(d => new DeviceDto(
                d.Id, d.Fingerprint, d.Role.ToCode(), d.DeviceName, d.OperatingSystem, d.FirstSeenAt, d.LastSeenAt))],
            [.. i.Activations.OrderByDescending(a => a.ActivatedAt).Select(a => new ActivationDto(
                a.Id, a.DeviceId, a.LicenseId, Code(a.Status), a.ActivatedAt, a.ReleasedAt,
                a.ReleasedBy is { } by ? users.GetValueOrDefault(by) ?? by.ToString() : null, a.ReleaseReason))]))];
    }

    private async Task<Dictionary<Guid, int>> OrganizationCountsAsync(List<Guid> accountIds, CancellationToken cancellationToken) =>
        await context.Set<Organization>().AsNoTracking()
            .Where(o => accountIds.Contains(o.AccountId))
            .GroupBy(o => o.AccountId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

    /// <summary>Nombres de los usuarios del portal (para mostrar quién hizo cada cambio).</summary>
    private async Task<Dictionary<Guid, string>> UserNamesAsync(CancellationToken cancellationToken) =>
        await context.Database.SqlQuery<UserName>($"SELECT id AS \"Id\", display_name AS \"Name\" FROM portal.portal_users")
            .ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);

    private static AccountDto ToDto(Account a, int organizations) => new(
        a.Id, a.Name, Code(a.Kind), a.Nit, a.NitCheckDigit, a.ContactName, a.ContactEmail, a.ContactPhone, a.ParentAccountId, Code(a.Status), a.Notes, organizations);

    private static string Code<TEnum>(TEnum value)
        where TEnum : struct, Enum => EnumCodes<TEnum>.Of(value);

    private sealed record UserName(Guid Id, string Name);

    /// <summary>Valor en la BD (MAYÚSCULAS_CON_GUIONES) de una enumeración; el convertidor se compila una vez por tipo.</summary>
    private static class EnumCodes<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly Func<object?, object?> Convert = new UpperSnakeEnumConverter<TEnum>().ConvertToProvider;

        public static string Of(TEnum value) => (string)Convert(value)!;
    }
}
