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
        return parties.ToDictionary(p => p.Id, p => new PartyInfo(
            p.Id, p.DisplayName, p.PersonType.ToString().ToUpperInvariant(), p.IdentificationType, p.IdentificationNumber, p.CheckDigit, p.Email,
            p.Status.ToString().ToUpperInvariant(), p.IsSystem, p.MergedIntoId));
    }
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
    }
}
