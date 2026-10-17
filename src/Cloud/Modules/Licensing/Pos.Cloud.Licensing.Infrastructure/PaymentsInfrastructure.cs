using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.Licensing.Domain;
using Pos.Infrastructure.Persistence;

namespace Pos.Cloud.Licensing.Infrastructure;

/// <summary>Mapeo EF del pago en línea (tablas de V2026.10.009): precios, enlaces, órdenes y eventos de la pasarela.</summary>
internal sealed class PaymentsModelContributor : ICloudModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SubscriptionPrice>(b =>
        {
            b.ToTable("prices", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Edition).HasConversion(LicensingModelContributor.EditionConverter);
            b.Property(x => x.BillingPeriod).HasUpperSnakeConversion();
            b.Property(x => x.AmountCop).HasColumnType("numeric(14,2)");
            b.Ignore(x => x.AuditLabel);
            b.HasControlColumns(withUpdates: false);
        });

        modelBuilder.Entity<PaymentLink>(b =>
        {
            b.ToTable("payment_links", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.IsActive);
        });

        modelBuilder.Entity<PaymentOrder>(b =>
        {
            b.ToTable("payment_orders", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Edition).HasConversion(LicensingModelContributor.EditionConverter);
            b.Property(x => x.BillingPeriod).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.Periods).HasConversion<short>();
            b.Property(x => x.AmountCop).HasColumnType("numeric(14,2)");
            b.Property(x => x.Currency).HasColumnType("char(3)");
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.IsApplied);
            b.Ignore(x => x.NeedsReview);
            b.Ignore(x => x.AuditLabel);
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PaymentEvent>(b =>
        {
            b.ToTable("payment_events", "licensing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Payload).HasColumnType("jsonb");
            b.Property(x => x.SourceIp).HasColumnType("inet");
            b.HasOne<PaymentOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class PaymentStore(CloudDbContext context) : IPaymentStore
{
    public void Add(SubscriptionPrice price) => context.Add(price);

    public void Add(PaymentLink link) => context.Add(link);

    public void Add(PaymentOrder order) => context.Add(order);

    public void Add(PaymentEvent paymentEvent) => context.Add(paymentEvent);

    public async Task<IReadOnlyList<SubscriptionPrice>> GetPricesAsync(CancellationToken cancellationToken) =>
        await context.Set<SubscriptionPrice>().AsNoTracking().ToListAsync(cancellationToken);

    public Task<PaymentLink?> GetActiveLinkAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        context.Set<PaymentLink>().SingleOrDefaultAsync(l => l.SubscriptionId == subscriptionId && l.RevokedAt == null, cancellationToken);

    public Task<PaymentLink?> GetLinkByCodeAsync(string code, CancellationToken cancellationToken) =>
        context.Set<PaymentLink>().AsNoTracking().SingleOrDefaultAsync(l => l.Code == code, cancellationToken);

    public async Task<PaymentLink> EnsureActiveLinkAsync(PaymentLink candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO licensing.payment_links (id, subscription_id, code, created_at)
            VALUES ({candidate.Id}, {candidate.SubscriptionId}, {candidate.Code}, {candidate.CreatedAt})
            ON CONFLICT (subscription_id) WHERE revoked_at IS NULL DO NOTHING
            """,
            cancellationToken);
        return await context.Set<PaymentLink>().AsNoTracking()
            .SingleAsync(l => l.SubscriptionId == candidate.SubscriptionId && l.RevokedAt == null, cancellationToken);
    }

    public Task<PaymentOrder?> GetOrderAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<PaymentOrder>().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<PaymentOrder?> LockOrderAsync(string reference, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM licensing.payment_orders WHERE reference = {reference} FOR UPDATE", cancellationToken);

        // Si ya estaba en memoria se vuelve a leer: otro proceso pudo cambiarla antes de obtener el bloqueo.
        if (context.ChangeTracker.Entries<PaymentOrder>().FirstOrDefault(e => e.Entity.Reference == reference) is { } tracked)
        {
            await tracked.ReloadAsync(cancellationToken);
            return tracked.Entity;
        }

        return await context.Set<PaymentOrder>().SingleOrDefaultAsync(o => o.Reference == reference, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetOrderIdsToReconcileAsync(int limit, DateTimeOffset recheckSince, CancellationToken cancellationToken) =>
        await context.Set<PaymentOrder>().AsNoTracking()
            .Where(o => o.Status == PaymentOrderStatus.Pending
                        || ((o.Status == PaymentOrderStatus.Declined || o.Status == PaymentOrderStatus.Expired) && o.CreatedAt >= recheckSince))
            .OrderBy(o => o.Status == PaymentOrderStatus.Pending ? 0 : 1)
            .ThenBy(o => o.CreatedAt)
            .Select(o => o.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<int> CountRecentRejectedEventsAsync(System.Net.IPAddress? ip, DateTimeOffset since, CancellationToken cancellationToken) =>
        ip is null
            ? context.Set<PaymentEvent>().CountAsync(e => e.SignatureValid == false && e.SourceIp == null && e.ReceivedAt >= since, cancellationToken)
            : context.Set<PaymentEvent>().CountAsync(e => e.SignatureValid == false && e.SourceIp == ip && e.ReceivedAt >= since, cancellationToken);

    public Task<int> CountRecentPendingOrdersAsync(Guid subscriptionId, DateTimeOffset since, CancellationToken cancellationToken) =>
        context.Set<PaymentOrder>().CountAsync(
            o => o.SubscriptionId == subscriptionId && o.Status == PaymentOrderStatus.Pending && o.CreatedAt >= since, cancellationToken);

    public async Task<long> NextOrderNumberAsync(CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<long>($"SELECT nextval('licensing.payment_order_number_seq') AS \"Value\"").ToListAsync(cancellationToken))[0];

    public async Task<Guid?> GetCustomerAccountAsync(Guid userId, CancellationToken cancellationToken) =>
        (await context.Database
            .SqlQuery<Guid?>($"SELECT reseller_account_id AS \"Value\" FROM portal.portal_users WHERE id = {userId} AND role = 'CUSTOMER'")
            .ToListAsync(cancellationToken)).FirstOrDefault();
}

/// <summary>Lecturas del pago en línea (sin seguimiento de cambios).</summary>
internal sealed class PaymentReadModel(CloudDbContext context) : IPaymentReadModel
{
    public async Task<IReadOnlyList<PriceDto>> ListPricesAsync(CancellationToken cancellationToken)
    {
        var users = await UserNamesAsync(cancellationToken);
        var prices = await context.Set<SubscriptionPrice>().AsNoTracking()
            .Select(p => new { Price = p, CreatedAt = EF.Property<DateTimeOffset>(p, ModelConventions.CreatedAt), CreatedBy = EF.Property<Guid>(p, ModelConventions.CreatedBy) })
            .ToListAsync(cancellationToken);
        return [.. prices
            .OrderByDescending(p => p.Price.ValidFrom).ThenByDescending(p => p.Price.Id)
            .Select(p => new PriceDto(p.Price.Id, p.Price.Edition.ToCode(), Code(p.Price.BillingPeriod), p.Price.AmountCop, p.Price.ValidFrom, p.CreatedAt,
                users.GetValueOrDefault(p.CreatedBy) ?? p.CreatedBy.ToString()))];
    }

    public async Task<IReadOnlyList<PaymentOrderRowDto>> ListOrdersAsync(PaymentOrderFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = from order in context.Set<PaymentOrder>().AsNoTracking()
                    join organization in context.Set<Organization>().AsNoTracking() on order.OrganizationId equals organization.Id
                    select new { order, organization };
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<PaymentOrderStatus>(filter.Status.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var status))
        {
            query = query.Where(r => r.order.Status == status);
        }

        if (filter.OrganizationId is { } organizationId)
        {
            query = query.Where(r => r.order.OrganizationId == organizationId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(r => r.order.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => r.order.CreatedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = $"%{filter.Search.Trim()}%";
            query = query.Where(r => EF.Functions.ILike(r.organization.LegalName, pattern) || EF.Functions.ILike(r.organization.Nit, pattern)
                                     || EF.Functions.ILike(r.order.Reference, pattern)
                                     || (r.order.GatewayTransactionId != null && EF.Functions.ILike(r.order.GatewayTransactionId, pattern)));
        }

        var rows = await query.OrderByDescending(r => r.order.CreatedAt).Take(500).ToListAsync(cancellationToken);
        var users = rows.Any(r => r.order.ReviewedBy is not null) ? await UserNamesAsync(cancellationToken) : [];
        return [.. rows.Select(r => ToRow(r.order, r.organization, users))];
    }

    public async Task<PaymentOrderDetailDto?> GetOrderAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = (await ListOrdersByAsync(o => o.Id == id, cancellationToken)).FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        var events = await context.Set<PaymentEvent>().AsNoTracking()
            .Where(e => e.OrderId == id || (e.OrderId == null && e.Reference == row.Reference))
            .OrderByDescending(e => e.ReceivedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        return new PaymentOrderDetailDto(row, [.. events.Select(e => new PaymentEventDto(
            e.Id, e.ReceivedAt, e.Source, e.EventType, e.SignatureValid, e.TransactionId, e.TransactionStatus, e.Reference, e.AmountInCents, e.Environment,
            e.RejectionReason, e.Payload))]);
    }

    public async Task<PaymentOrderRowDto?> FindOrderByTransactionAsync(string transactionId, CancellationToken cancellationToken) =>
        (await ListOrdersByAsync(o => o.GatewayTransactionId == transactionId, cancellationToken)).FirstOrDefault();

    public async Task<PaymentOrderRowDto?> FindOrderByReferenceAsync(string reference, CancellationToken cancellationToken) =>
        (await ListOrdersByAsync(o => o.Reference == reference, cancellationToken)).FirstOrDefault();

    public async Task<PaymentOrderRowDto?> FindOrderForResultAsync(string reference, string token, CancellationToken cancellationToken) =>
        (await ListOrdersByAsync(o => o.Reference == reference && o.ResultToken != null && o.ResultToken == token, cancellationToken)).FirstOrDefault();

    public async Task<(int ReviewRequired, int Pending)> CountAlertsAsync(CancellationToken cancellationToken)
    {
        var orders = context.Set<PaymentOrder>().AsNoTracking();
        return (await orders.CountAsync(o => o.Status == PaymentOrderStatus.ReviewRequired && o.ReviewedAt == null, cancellationToken),
            await orders.CountAsync(o => o.Status == PaymentOrderStatus.Pending, cancellationToken));
    }

    public async Task<IReadOnlyList<CustomerSubscriptionDto>> CustomerSubscriptionsAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var organizations = await context.Set<Organization>().AsNoTracking().Where(o => o.AccountId == accountId).OrderBy(o => o.LegalName).ToListAsync(cancellationToken);
        var ids = organizations.Select(o => o.Id).ToList();
        var subscriptions = await context.Set<Subscription>().AsNoTracking()
            .Where(s => ids.Contains(s.OrganizationId) && s.Status != SubscriptionStatus.Cancelled)
            .ToDictionaryAsync(s => s.OrganizationId, cancellationToken);
        var orders = await context.Set<PaymentOrder>().AsNoTracking()
            .Where(o => ids.Contains(o.OrganizationId))
            .OrderByDescending(o => o.CreatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);
        return [.. organizations.Select(o =>
        {
            var subscription = subscriptions.GetValueOrDefault(o.Id);
            return new CustomerSubscriptionDto(
                o.Id, o.LegalName, subscription is null ? null : LicensingReadModel.Summary(subscription, now),
                subscription is not null && PaymentRules.IsPayable(subscription.StatusAt(now)),
                [.. orders.Where(p => p.OrganizationId == o.Id).Take(50).Select(p => ToRow(p, o, []))]);
        })];
    }

    private async Task<List<PaymentOrderRowDto>> ListOrdersByAsync(System.Linq.Expressions.Expression<Func<PaymentOrder, bool>> predicate, CancellationToken cancellationToken)
    {
        var rows = await (from order in context.Set<PaymentOrder>().AsNoTracking().Where(predicate)
                          join organization in context.Set<Organization>().AsNoTracking() on order.OrganizationId equals organization.Id
                          select new { order, organization })
            .OrderByDescending(r => r.order.CreatedAt)
            .Take(5)
            .ToListAsync(cancellationToken);
        var users = rows.Any(r => r.order.ReviewedBy is not null) ? await UserNamesAsync(cancellationToken) : [];
        return [.. rows.Select(r => ToRow(r.order, r.organization, users))];
    }

    private static PaymentOrderRowDto ToRow(PaymentOrder o, Organization organization, Dictionary<Guid, string> users) => new(
        o.Id, o.Reference, o.SubscriptionId, o.OrganizationId, organization.LegalName, organization.NitNumber.ToString(), o.Edition.ToCode(), Code(o.BillingPeriod),
        o.Periods, o.AmountCop, Code(o.Status), o.GatewayTransactionId, o.PaymentMethodType, o.CreatedAt, o.UpdatedAt, o.AppliedAt, o.ReviewReason, o.ReviewedAt,
        o.ReviewedBy is { } by ? users.GetValueOrDefault(by) ?? by.ToString() : null, o.ReviewNote);

    private async Task<Dictionary<Guid, string>> UserNamesAsync(CancellationToken cancellationToken) =>
        await context.Database.SqlQuery<UserName>($"SELECT id AS \"Id\", display_name AS \"Name\" FROM portal.portal_users")
            .ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);

    private static string Code<TEnum>(TEnum value)
        where TEnum : struct, Enum => (string)new UpperSnakeEnumConverter<TEnum>().ConvertToProvider(value)!;

    private sealed record UserName(Guid Id, string Name);
}

/// <summary>
/// Adaptador de Wompi (D12C-01): Web Checkout con firma de integridad, verificación de eventos con el secreto de eventos y
/// consulta de transacciones con la llave privada. Las llaves nunca se registran en los logs.
/// </summary>
internal sealed partial class WompiGateway(HttpClient http, PaymentsOptions options, ILogger<WompiGateway> logger) : IPaymentGateway
{
    private WompiOptions Wompi => options.Wompi;

    public string Name => PaymentRules.Gateway;

    public bool IsConfigured => Wompi.IsConfigured;

    public string Environment => Wompi.IsProduction ? "Production" : "Sandbox";

    public string? CheckoutOrigin =>
        Uri.TryCreate(Wompi.CheckoutUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;

    public string BuildCheckoutUrl(CheckoutRequest request) =>
        IsConfigured
            ? WompiProtocol.CheckoutUrl(Wompi.CheckoutUrl, Wompi.PublicKey!, Wompi.IntegritySecret!, request)
            : throw new InvalidOperationException(PaymentErrors.NotConfiguredMessage);

    public GatewayEvent ParseEvent(string body, string? checksumHeader) =>
        IsConfigured
            ? WompiProtocol.ParseEvent(body, checksumHeader, Wompi.EventsSecret!)
            : new GatewayEvent(false, null, null, null, PaymentErrors.NotConfiguredMessage);

    public async Task<GatewayTransaction?> GetTransactionAsync(string transactionId, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"transactions/{Uri.EscapeDataString(transactionId)}", cancellationToken);
        return document is not null && document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? WompiProtocol.ReadTransaction(data)
            : null;
    }

    public async Task<IReadOnlyList<GatewayTransaction>?> FindByReferenceAsync(string reference, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"transactions?reference={Uri.EscapeDataString(reference)}", cancellationToken);
        if (document is null || !document.RootElement.TryGetProperty("data", out var data))
        {
            return null;
        }

        return data.ValueKind switch
        {
            JsonValueKind.Array => [.. data.EnumerateArray().Select(WompiProtocol.ReadTransaction).OfType<GatewayTransaction>().Where(t => t.Reference == reference)],
            JsonValueKind.Object => WompiProtocol.ReadTransaction(data) is { } single && single.Reference == reference ? [single] : [],
            _ => [],
        };
    }

    private async Task<JsonDocument?> GetAsync(string relative, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Wompi.EffectiveApiBaseUrl}/{relative}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Wompi.PrivateKey);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return JsonDocument.Parse("{}");
            }

            if (!response.IsSuccessStatusCode)
            {
                LogFailed(logger, relative.Split('?')[0], (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            LogUnavailable(logger, ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wompi respondió {Status} a la consulta {Path}")]
    private static partial void LogFailed(ILogger logger, string path, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo consultar Wompi ({Error})")]
    private static partial void LogUnavailable(ILogger logger, string error);
}
