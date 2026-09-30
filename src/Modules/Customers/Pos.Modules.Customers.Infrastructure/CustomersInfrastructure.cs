using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Customers.Application;
using Pos.Modules.Customers.Domain;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Text;

namespace Pos.Modules.Customers.Infrastructure;

internal sealed class CustomersModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerGroup>(b =>
        {
            b.ToTable("customer_groups", "customers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers", "customers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("party_id").ValueGeneratedNever();
            b.Ignore(x => x.PartyId);
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.Origin).HasUpperSnakeConversion();
            b.Property(x => x.CreditStatus).HasUpperSnakeConversion();
            b.Property(x => x.LoyaltyStatus).HasUpperSnakeConversion();
            b.HasOne<CustomerGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PrivacyPolicy>(b =>
        {
            b.ToTable("privacy_policies", "customers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<CustomerConsent>(b =>
        {
            b.ToTable("customer_consents", "customers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Purpose).HasUpperSnakeConversion();
            b.Property(x => x.Channel).HasUpperSnakeConversion();
        });

        modelBuilder.Entity<DataRequest>(b =>
        {
            b.ToTable("data_requests", "customers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasUpperSnakeConversion();
            b.Property(x => x.Channel).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasControlColumns();
            b.HasXminConcurrency();
        });
    }
}

internal sealed class CustomerStore(PosDbContext context) : ICustomerStore
{
    public void Add(CustomerGroup group) => context.Add(group);

    public void Add(Customer customer) => context.Add(customer);

    public void Add(PrivacyPolicy policy) => context.Add(policy);

    public void Add(CustomerConsent consent) => context.Add(consent);

    public void Add(DataRequest request) => context.Add(request);

    public async Task<Customer?> GetCustomerAsync(Guid partyId, CancellationToken cancellationToken) =>
        context.ChangeTracker.Entries<Customer>().Select(e => e.Entity).FirstOrDefault(c => c.Id == partyId)
        ?? await context.Set<Customer>().SingleOrDefaultAsync(c => c.Id == partyId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Customer>> GetCustomersAsync(IReadOnlyCollection<Guid> partyIds, CancellationToken cancellationToken) =>
        partyIds.Count == 0
            ? new Dictionary<Guid, Customer>()
            : await context.Set<Customer>().Where(c => partyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);

    public async Task<IReadOnlyList<CustomerGroup>> GetGroupsAsync(CancellationToken cancellationToken) =>
        [.. context.ChangeTracker.Entries<CustomerGroup>().Where(e => e.State == EntityState.Added).Select(e => e.Entity),
            .. await context.Set<CustomerGroup>().ToListAsync(cancellationToken)];

    public async Task<IReadOnlyList<PrivacyPolicy>> GetPoliciesAsync(CancellationToken cancellationToken) =>
        [.. context.ChangeTracker.Entries<PrivacyPolicy>().Where(e => e.State == EntityState.Added).Select(e => e.Entity),
            .. await context.Set<PrivacyPolicy>().ToListAsync(cancellationToken)];

    public async Task<IReadOnlyList<CustomerConsent>> GetConsentsAsync(Guid partyId, CancellationToken cancellationToken) =>
        await context.Set<CustomerConsent>().AsNoTracking().Where(c => c.PartyId == partyId).OrderBy(c => c.OccurredAt).ToListAsync(cancellationToken);

    public Task<DataRequest?> GetRequestAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<DataRequest>().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DataRequest>> ListRequestsAsync(DataRequestStatus? status, CancellationToken cancellationToken) =>
        await context.Set<DataRequest>().Where(r => status == null || r.Status == status).OrderBy(r => r.DueOn).Take(1_000).ToListAsync(cancellationToken);
}

/// <summary>Listados con SQL directo (cruzan con terceros e identidad para mostrar nombres).</summary>
internal sealed class CustomerQueries(PosDbContext context) : ICustomerQueries
{
    public async Task<(IReadOnlyList<CustomerRow> Items, int Total)> ListAsync(
        string? search, Guid? groupId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var tokens = TextNormalization.ForSearch(search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => "%" + t + "%").ToArray();
        var args = new
        {
            companyId = context.TenantCompanyId, groupId, status, tokens = tokens.Length == 0 ? ["%"] : tokens, number = (search ?? string.Empty).Trim(),
            limit = pageSize, offset = (page - 1) * pageSize,
        };
        const string Where =
            """
            FROM customers.customers c
            JOIN parties.parties p ON p.id = c.party_id
            JOIN customers.customer_groups g ON g.id = c.group_id
            WHERE c.company_id = @companyId AND (@groupId::uuid IS NULL OR c.group_id = @groupId) AND (@status::text IS NULL OR c.status = @status)
              AND (p.search_text LIKE ALL (@tokens) OR p.identification_number = @number)
            """;
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*)::int {Where}", args, transaction, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<CustomerRow>(new CommandDefinition(
            $"""
            SELECT c.party_id AS PartyId, CASE WHEN p.person_type = 'LEGAL' THEN p.legal_name ELSE trim(p.first_names || ' ' || p.last_names) END AS DisplayName,
                   p.identification_type AS IdentificationType, p.identification_number AS IdentificationNumber, p.check_digit AS CheckDigit, p.phone AS Phone,
                   p.email AS Email, g.code AS GroupCode, c.status AS Status, c.service_consent AS ServiceConsent
            {Where}
            ORDER BY p.search_text
            LIMIT @limit OFFSET @offset
            """,
            args, transaction, cancellationToken: cancellationToken));
        return ([.. rows], total);
    }

    public async Task<int> CountWithoutConsentAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM customers.customers WHERE company_id = @companyId AND NOT service_consent AND anonymized_at IS NULL",
            new { companyId = context.TenantCompanyId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(Guid Id, string Name)>(new CommandDefinition(
            "SELECT id, display_name FROM identity.users WHERE id = ANY(@ids)", new { ids = userIds.ToArray() }, transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }

    private async Task<(System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction? Transaction)> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }
}

internal sealed class CustomersConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_customer_groups__code"] = Error.Conflict("CUSTOMERS.GROUP_CODE_DUPLICATED", "Ya existe un grupo con ese código."),
        ["pk_customers"] = Error.Conflict("CUSTOMERS.ALREADY_EXISTS", "El tercero ya tiene el rol de cliente."),
        ["ux_privacy_policies__version"] = Error.Conflict("CUSTOMERS.POLICY_VERSION_DUPLICATED", "Otra versión de la política se creó al mismo tiempo."),
        ["fk_customer_groups__price_list"] = Error.NotFound("CATALOG.PRICE_LIST_NOT_FOUND", "La lista de precios no existe."),
        ["fk_customers__price_list"] = Error.NotFound("CATALOG.PRICE_LIST_NOT_FOUND", "La lista de precios no existe."),
    };
}

public static class CustomersInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CustomersModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, CustomersConstraintErrors>();
        services.AddScoped<ICustomerStore, CustomerStore>();
        services.AddScoped<ICustomerQueries, CustomerQueries>();
    }
}
