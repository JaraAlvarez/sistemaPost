using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Parties.Application;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Parties.Domain;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Text;

namespace Pos.Modules.Parties.Infrastructure;

internal sealed class PartiesModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Party>(b =>
        {
            b.ToTable("parties", "parties");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PersonType).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.Ignore(x => x.DisplayName);
            b.Ignore(x => x.FullIdentification);
            b.HasMany(x => x.Contacts).WithOne().HasForeignKey("PartyId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Contacts).HasField("_contacts");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PartyContact>(b =>
        {
            b.ToTable("party_contacts", "parties");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Role).HasColumnName("contact_role").HasUpperSnakeConversion();
        });
    }
}

internal sealed class PartyStore(PosDbContext context) : IPartyStore
{
    public void Add(Party party) => context.Add(party);

    public Task<Party?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Party>().Include(p => p.Contacts).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Party?> FindByIdentificationAsync(string type, string number, CancellationToken cancellationToken) =>
        context.Set<Party>().Include(p => p.Contacts)
            .FirstOrDefaultAsync(p => p.IdentificationType == type && p.IdentificationNumber == number && p.MergedIntoId == null, cancellationToken);

    public async Task<IReadOnlyList<Party>> FindByNumberAsync(string number, CancellationToken cancellationToken) =>
        await context.Set<Party>().Where(p => p.IdentificationNumber == number && p.MergedIntoId == null && !p.IsSystem).ToListAsync(cancellationToken);

    public async Task LockIdentificationAsync(string type, string number, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        await context.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext('parties:' || @type || ':' || @number))", new { type, number },
            context.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: cancellationToken));
    }
}

/// <summary>Catálogos de la DIAN del esquema ref (se leen en la conexión de la petición).</summary>
internal sealed class PartyReferenceData(PosDbContext context) : IPartyReferenceData
{
    public async Task<IdentificationTypeInfo?> GetIdentificationTypeAsync(string code, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<IdentificationTypeInfo>(new CommandDefinition(
            """
            SELECT code AS Code, requires_check_digit AS RequiresCheckDigit, allows_natural AS AllowsNatural, allows_legal AS AllowsLegal
            FROM ref.identification_types WHERE code = @code
            """,
            new { code }, context.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlySet<string>> GetFiscalResponsibilitiesAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT code FROM ref.fiscal_responsibilities", transaction: context.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken: cancellationToken))).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return context.Database.GetDbConnection();
    }
}

internal sealed class PartyQueries(PosDbContext context) : IPartyQueries
{
    public async Task<IReadOnlyList<PartyMatch>> LookupAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var raw = (text ?? string.Empty).Trim().ToUpperInvariant();
        if (raw.Length < 2)
        {
            return [];
        }

        var compact = new string([.. raw.Where(c => c is not ('.' or ' ' or ','))]);
        var dash = compact.IndexOf('-', StringComparison.Ordinal);
        var number = dash > 0 && compact.All(c => char.IsAsciiDigit(c) || c == '-') ? compact[..dash] : compact;
        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);
        var isNumeric = number.Length > 0 && number.All(char.IsAsciiDigit);
        var name = TextNormalization.ForSearch(text);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var rows = await context.Database.GetDbConnection().QueryAsync<MatchRow>(new CommandDefinition(
            """
            SELECT id AS Id, CASE WHEN person_type = 'LEGAL' THEN legal_name ELSE trim(first_names || ' ' || last_names) END AS DisplayName,
                   identification_type AS IdentificationType, identification_number AS IdentificationNumber, check_digit AS CheckDigit, phone AS Phone,
                   email AS Email,
                   CASE WHEN identification_number = @number OR identification_number LIKE @numberPrefix THEN 'IDENTIFICATION'
                        WHEN @phone::text IS NOT NULL AND phone_digits LIKE @phone THEN 'PHONE' ELSE 'NAME' END AS MatchedBy,
                   (identification_number = @number OR phone_digits = @digits) AS Exact,
                   CASE WHEN @name = '' THEN 0 ELSE similarity(search_text, @name)::float8 END AS Score
            FROM parties.parties
            WHERE company_id = @companyId AND merged_into_id IS NULL AND NOT is_system AND status = 'ACTIVE' AND deleted_at IS NULL
              AND (identification_number = @number
                   OR (@isNumeric AND length(@number) >= 4 AND identification_number LIKE @numberPrefix)
                   OR (@phone::text IS NOT NULL AND phone_digits LIKE @phone)
                   OR (NOT @isNumeric AND search_text LIKE ALL (@tokens)))
            ORDER BY Exact DESC, Score DESC, DisplayName
            LIMIT @limit
            """,
            new
            {
                companyId = context.TenantCompanyId, number, numberPrefix = number + "%", isNumeric, digits,
                phone = digits.Length >= 7 && isNumeric ? "%" + digits : null, name,
                tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => "%" + t + "%").DefaultIfEmpty("%").ToArray(), limit,
            },
            context.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: cancellationToken));
        return [.. rows.Select(r => new PartyMatch(r.Id, r.DisplayName, r.IdentificationType, r.IdentificationNumber, r.CheckDigit, r.Phone, r.Email, r.MatchedBy,
            r.Exact))];
    }

    private sealed class MatchRow
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public string IdentificationType { get; set; } = string.Empty;

        public string IdentificationNumber { get; set; } = string.Empty;

        public string? CheckDigit { get; set; }

        public string? Phone { get; set; }

        public string? Email { get; set; }

        public string MatchedBy { get; set; } = string.Empty;

        public bool Exact { get; set; }

        public double Score { get; set; }
    }

    public async Task<PartyPageDto> SearchAsync(string? text, bool includeInactive, int page, int pageSize, CancellationToken cancellationToken)
    {
        var parties = context.Set<Party>().AsNoTracking().Where(p => p.MergedIntoId == null);
        if (!includeInactive)
        {
            parties = parties.Where(p => p.Status == PartyStatus.Active);
        }

        var raw = text?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(raw))
        {
            var number = raw.Replace(".", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
            var dash = number.IndexOf('-', StringComparison.Ordinal);
            var withoutDv = dash > 0 && number.All(c => char.IsAsciiDigit(c) || c == '-') ? number[..dash] : number;
            var tokens = TextNormalization.ForSearch(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var byName = parties;
            foreach (var token in tokens)
            {
                var pattern = "%" + token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                    .Replace("_", "\\_", StringComparison.Ordinal) + "%";
                byName = byName.Where(p => EF.Functions.Like(p.SearchText, pattern));
            }

            parties = byName.Union(parties.Where(p => p.IdentificationNumber == withoutDv || p.IdentificationNumber == number));
        }

        var total = await parties.CountAsync(cancellationToken);
        var items = await parties
            .OrderByDescending(p => p.IsSystem).ThenBy(p => p.SearchText)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PartyPageDto(
            [.. items.Select(p => new PartySummaryDto(p.Id, p.DisplayName, p.IdentificationType, p.IdentificationNumber, p.CheckDigit,
                p.Status.ToString().ToUpperInvariant(), p.IsSystem))],
            page, pageSize, total);
    }
}

internal sealed class PartyDirectory(PosDbContext context) : IPartyDirectory
{
    public async Task<IReadOnlyDictionary<Guid, PartyInfo>> GetAsync(IReadOnlyCollection<Guid> partyIds, CancellationToken cancellationToken = default)
    {
        if (partyIds.Count == 0)
        {
            return new Dictionary<Guid, PartyInfo>();
        }

        var parties = await context.Set<Party>().AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToListAsync(cancellationToken);
        return parties.ToDictionary(p => p.Id, ToInfo);
    }

    public async Task<PartyInfo?> GetFinalConsumerAsync(CancellationToken cancellationToken = default) =>
        await context.Set<Party>().AsNoTracking()
            .Where(p => p.IdentificationType == Party.FinalConsumerType && p.IdentificationNumber == Party.FinalConsumerNumber)
            .FirstOrDefaultAsync(cancellationToken) is { } party
            ? ToInfo(party)
            : null;

    private static PartyInfo ToInfo(Party p) => new(
        p.Id, p.DisplayName, p.PersonType.ToString().ToUpperInvariant(), p.IdentificationType, p.IdentificationNumber, p.CheckDigit, p.Email,
        p.Status.ToString().ToUpperInvariant(), p.IsSystem, p.MergedIntoId);
}

internal sealed class PartiesConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_parties__identification"] = PartyErrors.Duplicated,
        ["fk_parties__identification_type"] = PartyErrors.IdentificationTypeNotFound,
        ["fk_parties__tax_regime"] = PartyErrors.InvalidFiscalData,
        ["fk_parties__municipality"] = Error.Validation("PARTIES.MUNICIPALITY_NOT_FOUND", "El municipio (código DIVIPOLA) no existe."),
        ["ux_party_contacts__primary"] = PartyErrors.InvalidContact,
    };
}

public static class PartiesInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, PartiesModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, PartiesConstraintErrors>();
        services.AddScoped<IPartyStore, PartyStore>();
        services.AddScoped<IPartyReferenceData, PartyReferenceData>();
        services.AddScoped<IPartyQueries, PartyQueries>();
        services.AddScoped<IPartyDirectory, PartyDirectory>();
        services.AddScoped<IPartyRegistry, PartyRegistry>();
    }
}
