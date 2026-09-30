using Pos.Application.Abstractions.Installation;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Parties.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Parties.Application;

/// <summary>
/// Terceros para el módulo de clientes (Fase 8): alta rápida sin duplicados (con bloqueo por identificación, dos cajas a la vez
/// reciben el mismo tercero), completar vacíos, corrección, supresión y búsqueda de la caja.
/// </summary>
public sealed class PartyRegistry(IInstallationContext installation, IPartyStore store, IPartyQueries queries, PartyReferenceGuard guard, IIdGenerator ids)
    : IPartyRegistry
{
    public async Task<Result<PartyRegistrationResult>> RegisterAsync(PartyRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (installation.CompanyId is not { } companyId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
        }

        var data = ToData(registration);
        if (data.IsFailure)
        {
            return data.Error;
        }

        var type = await guard.ValidateAsync(data.Value, cancellationToken);
        if (type.IsFailure)
        {
            return type.Error;
        }

        var created = Party.Create(ids.NewId(), companyId, data.Value, type.Value);
        if (created.IsFailure)
        {
            return created.Error;
        }

        // Serializa las altas de la misma identificación: la segunda caja espera y recibe el tercero que creó la primera.
        await store.LockIdentificationAsync(created.Value.IdentificationType, created.Value.IdentificationNumber, cancellationToken);
        var duplicates = (await store.FindByNumberAsync(created.Value.IdentificationNumber, cancellationToken))
            .Where(p => p.IdentificationType != created.Value.IdentificationType).Select(ToProfile).ToList();
        if (await store.FindByIdentificationAsync(created.Value.IdentificationType, created.Value.IdentificationNumber, cancellationToken) is { } existing)
        {
            return new PartyRegistrationResult(ToProfile(existing), false, duplicates);
        }

        store.Add(created.Value);
        return new PartyRegistrationResult(ToProfile(created.Value), true, duplicates);
    }

    public async Task<Result<(PartyProfile Party, IReadOnlyList<string> Filled)>> CompleteAsync(
        Guid partyId, string? email, string? phone, string? address, string? municipalityCode, CancellationToken cancellationToken = default)
    {
        var party = await store.GetAsync(partyId, cancellationToken);
        if (party is null)
        {
            return PartyErrors.NotFound;
        }

        var filled = party.CompleteEmpty(email, phone, address, municipalityCode);
        return filled.IsSuccess ? (ToProfile(party), filled.Value) : filled.Error;
    }

    public async Task<Result<PartyProfile>> CorrectAsync(Guid partyId, PartyRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var party = await store.GetAsync(partyId, cancellationToken);
        if (party is null)
        {
            return PartyErrors.NotFound;
        }

        var data = ToData(registration);
        if (data.IsFailure)
        {
            return data.Error;
        }

        var type = await guard.ValidateAsync(data.Value, cancellationToken);
        if (type.IsFailure)
        {
            return type.Error;
        }

        var updated = party.Update(data.Value, type.Value);
        return updated.IsSuccess ? ToProfile(party) : updated.Error;
    }

    public async Task<Result> AnonymizeAsync(Guid partyId, CancellationToken cancellationToken = default)
    {
        var party = await store.GetAsync(partyId, cancellationToken);
        return party is null ? PartyErrors.NotFound : party.Anonymize();
    }

    public async Task<IReadOnlyDictionary<Guid, PartyProfile>> GetProfilesAsync(IReadOnlyCollection<Guid> partyIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, PartyProfile>();
        foreach (var id in partyIds.Distinct())
        {
            if (await store.GetAsync(id, cancellationToken) is { } party)
            {
                result[id] = ToProfile(party);
            }
        }

        return result;
    }

    public Task<IReadOnlyList<PartyMatch>> LookupAsync(string text, int limit, CancellationToken cancellationToken = default) =>
        queries.LookupAsync(text ?? string.Empty, Math.Clamp(limit, 1, 50), cancellationToken);

    public static PartyProfile ToProfile(Party p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new PartyProfile(
            p.Id, p.DisplayName, p.PersonType.ToString().ToUpperInvariant(), p.IdentificationType, p.IdentificationNumber, p.CheckDigit, p.LegalName, p.FirstNames,
            p.LastNames, p.TaxRegime, p.FiscalResponsibilities.Split(';', StringSplitOptions.RemoveEmptyEntries), p.Email, p.Phone, p.Address, p.MunicipalityCode,
            p.Status.ToString().ToUpperInvariant(), p.IsSystem);
    }

    private static Result<PartyData> ToData(PartyRegistration r)
    {
        if (!Enum.TryParse<PersonType>(r.PersonType, ignoreCase: true, out var personType))
        {
            return PartyErrors.PersonTypeNotAllowed;
        }

        return new PartyData(
            personType, (r.IdentificationType ?? string.Empty).Trim().ToUpperInvariant(), r.IdentificationNumber, r.CheckDigit, r.LegalName, r.FirstNames,
            r.LastNames, r.TradeName, string.IsNullOrWhiteSpace(r.TaxRegime) ? Party.NotVatResponsible : r.TaxRegime,
            r.FiscalResponsibilities is { Count: > 0 } responsibilities ? responsibilities : [Party.DefaultResponsibility], r.Email, r.Phone, r.Address,
            r.MunicipalityCode, null);
    }
}
