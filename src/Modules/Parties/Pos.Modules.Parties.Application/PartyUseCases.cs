using FluentValidation;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Parties.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Parties.Application;

/// <summary>Terceros de la empresa (EF Core).</summary>
public interface IPartyStore
{
    void Add(Party party);

    Task<Party?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Party?> FindByIdentificationAsync(string type, string number, CancellationToken cancellationToken);

    /// <summary>Terceros con ese número en cualquier tipo de identificación (posibles duplicados CC/NIT).</summary>
    Task<IReadOnlyList<Party>> FindByNumberAsync(string number, CancellationToken cancellationToken);

    /// <summary>Bloqueo de la transacción sobre una identificación: serializa dos altas simultáneas de la misma.</summary>
    Task LockIdentificationAsync(string type, string number, CancellationToken cancellationToken);
}

/// <summary>Datos de referencia de la DIAN (esquema ref, solo lectura).</summary>
public interface IPartyReferenceData
{
    Task<IdentificationTypeInfo?> GetIdentificationTypeAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> GetFiscalResponsibilitiesAsync(CancellationToken cancellationToken);
}

/// <summary>Búsqueda de terceros por nombre (sin tildes) o por número de identificación.</summary>
public interface IPartyQueries
{
    Task<PartyPageDto> SearchAsync(string? text, bool includeInactive, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Búsqueda de la caja por identificación, teléfono o nombre (D8-03).</summary>
    Task<IReadOnlyList<PartyMatch>> LookupAsync(string text, int limit, CancellationToken cancellationToken);
}

public sealed class PartiesPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => PartiesPermissions.All;
}

/// <summary>Siembra el "Consumidor final" de cada empresa (propuesta §4.1). Idempotente.</summary>
public sealed class PartiesInitializer(IPartyStore store, IIdGenerator ids) : ICompanyInitializer
{
    public int Order => 50;

    public async Task InitializeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        if (await store.FindByIdentificationAsync(Party.FinalConsumerType, Party.FinalConsumerNumber, cancellationToken) is null)
        {
            store.Add(Party.FinalConsumer(ids.NewId(), companyId));
        }
    }
}

/// <summary>Datos de un tercero tal como llegan de la API.</summary>
public sealed record PartyInput(
    PersonType PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? LegalName,
    string? FirstNames,
    string? LastNames,
    string? TradeName,
    string? TaxRegime,
    IReadOnlyList<string>? FiscalResponsibilities,
    string? Email,
    string? Phone,
    string? Address,
    string? MunicipalityCode,
    string? Notes,
    IReadOnlyList<ContactData>? Contacts);

internal static class PartyMapping
{
    public static PartyDto ToDto(this Party p) => new(
        p.Id, p.PersonType.ToString().ToUpperInvariant(), p.IdentificationType, p.IdentificationNumber, p.CheckDigit, p.DisplayName, p.LegalName,
        p.FirstNames, p.LastNames, p.TradeName, p.TaxRegime, p.FiscalResponsibilities.Split(';', StringSplitOptions.RemoveEmptyEntries), p.Email, p.Phone,
        p.Address, p.MunicipalityCode, p.Notes, p.IsSystem, p.Status.ToString().ToUpperInvariant(), p.MergedIntoId,
        [.. p.Contacts.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => new PartyContactDto(c.Id, c.Name, c.Position, c.Phone, c.Email, c.IsPrimary, c.Role.ToString().ToUpperInvariant()))]);

    public static PartyData ToData(this PartyInput input) => new(
        input.PersonType, (input.IdentificationType ?? string.Empty).Trim().ToUpperInvariant(), input.IdentificationNumber, input.CheckDigit, input.LegalName,
        input.FirstNames, input.LastNames, input.TradeName, string.IsNullOrWhiteSpace(input.TaxRegime) ? Party.NotVatResponsible : input.TaxRegime,
        input.FiscalResponsibilities ?? [], input.Email, input.Phone, input.Address, input.MunicipalityCode, input.Notes);
}

/// <summary>Validación común: tipo de identificación existente y responsabilidades fiscales del catálogo de la DIAN.</summary>
public sealed class PartyReferenceGuard(IPartyReferenceData reference)
{
    public async Task<Result<IdentificationTypeInfo>> ValidateAsync(PartyData data, CancellationToken cancellationToken)
    {
        var type = await reference.GetIdentificationTypeAsync(data.IdentificationType, cancellationToken);
        if (type is null)
        {
            return PartyErrors.IdentificationTypeNotFound;
        }

        var known = await reference.GetFiscalResponsibilitiesAsync(cancellationToken);
        return data.FiscalResponsibilities.All(r => known.Contains(r.Trim().ToUpperInvariant())) ? type : PartyErrors.InvalidFiscalData;
    }
}

public sealed record CreatePartyCommand(PartyInput Party) : ICommand<PartyDto>;

internal sealed class CreatePartyValidator : AbstractValidator<CreatePartyCommand>
{
    public CreatePartyValidator()
    {
        RuleFor(x => x.Party).NotNull();
        RuleFor(x => x.Party.IdentificationType).NotEmpty().MaximumLength(10);
        RuleFor(x => x.Party.IdentificationNumber).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Party.Notes).MaximumLength(500);
        RuleFor(x => x.Party.Address).MaximumLength(250);
        RuleFor(x => x.Party.Phone).MaximumLength(30);
    }
}

internal sealed class CreatePartyHandler(IInstallationContext installation, IPartyStore store, PartyReferenceGuard guard, IIdGenerator ids)
    : ICommandHandler<CreatePartyCommand, PartyDto>
{
    public async Task<Result<PartyDto>> Handle(CreatePartyCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
        }

        var data = request.Party.ToData();
        var type = await guard.ValidateAsync(data, cancellationToken);
        if (type.IsFailure)
        {
            return type.Error;
        }

        var party = Party.Create(ids.NewId(), companyId, data, type.Value);
        if (party.IsFailure)
        {
            return party.Error;
        }

        if (await store.FindByIdentificationAsync(party.Value.IdentificationType, party.Value.IdentificationNumber, cancellationToken) is not null)
        {
            return PartyErrors.Duplicated;
        }

        var contacts = party.Value.SetContacts(request.Party.Contacts ?? [], ids.NewId);
        if (contacts.IsFailure)
        {
            return contacts.Error;
        }

        store.Add(party.Value);
        return party.Value.ToDto();
    }
}

public sealed record UpdatePartyCommand(Guid PartyId, PartyInput Party) : ICommand<PartyDto>;

internal sealed class UpdatePartyHandler(IPartyStore store, PartyReferenceGuard guard, IIdGenerator ids) : ICommandHandler<UpdatePartyCommand, PartyDto>
{
    public async Task<Result<PartyDto>> Handle(UpdatePartyCommand request, CancellationToken cancellationToken)
    {
        var party = await store.GetAsync(request.PartyId, cancellationToken);
        if (party is null)
        {
            return PartyErrors.NotFound;
        }

        var data = request.Party.ToData();
        var type = await guard.ValidateAsync(data, cancellationToken);
        if (type.IsFailure)
        {
            return type.Error;
        }

        var updated = party.Update(data, type.Value);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        if (request.Party.Contacts is { } contacts && party.SetContacts(contacts, ids.NewId) is { IsFailure: true } invalid)
        {
            return invalid.Error;
        }

        return party.ToDto();
    }
}

public sealed record SetPartyActiveCommand(Guid PartyId, bool IsActive) : ICommand<PartyDto>;

internal sealed class SetPartyActiveHandler(IPartyStore store) : ICommandHandler<SetPartyActiveCommand, PartyDto>
{
    public async Task<Result<PartyDto>> Handle(SetPartyActiveCommand request, CancellationToken cancellationToken)
    {
        var party = await store.GetAsync(request.PartyId, cancellationToken);
        if (party is null)
        {
            return PartyErrors.NotFound;
        }

        var result = party.SetActive(request.IsActive);
        return result.IsSuccess ? party.ToDto() : result.Error;
    }
}

public sealed record GetPartyQuery(Guid PartyId) : IQuery<PartyDto>;

internal sealed class GetPartyHandler(IPartyStore store) : IQueryHandler<GetPartyQuery, PartyDto>
{
    public async Task<Result<PartyDto>> Handle(GetPartyQuery request, CancellationToken cancellationToken) =>
        await store.GetAsync(request.PartyId, cancellationToken) is { } party ? party.ToDto() : PartyErrors.NotFound;
}

/// <summary>Busca por identificación exacta (tipo + número); lo usan la caja y compras para no crear duplicados.</summary>
public sealed record FindPartyByIdentificationQuery(string Type, string Number) : IQuery<PartyDto>;

internal sealed class FindPartyByIdentificationHandler(IPartyStore store) : IQueryHandler<FindPartyByIdentificationQuery, PartyDto>
{
    public async Task<Result<PartyDto>> Handle(FindPartyByIdentificationQuery request, CancellationToken cancellationToken) =>
        await store.FindByIdentificationAsync(
            (request.Type ?? string.Empty).Trim().ToUpperInvariant(), (request.Number ?? string.Empty).Trim().ToUpperInvariant(), cancellationToken) is { } party
            ? party.ToDto()
            : PartyErrors.NotFound;
}

public sealed record SearchPartiesQuery(string? Text, bool IncludeInactive, int Page = 1, int PageSize = 50) : IQuery<PartyPageDto>;

internal sealed class SearchPartiesHandler(IPartyQueries queries) : IQueryHandler<SearchPartiesQuery, PartyPageDto>
{
    public async Task<Result<PartyPageDto>> Handle(SearchPartiesQuery request, CancellationToken cancellationToken) =>
        await queries.SearchAsync(request.Text, request.IncludeInactive, Math.Max(1, request.Page), Math.Clamp(request.PageSize, 1, 200), cancellationToken);
}
