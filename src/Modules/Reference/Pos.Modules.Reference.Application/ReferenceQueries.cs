using Pos.Application.Abstractions.Messaging;
using Pos.Modules.Reference.Contracts;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Reference.Application;

/// <summary>Lectura de los catálogos oficiales (solo lectura: se actualizan únicamente con migraciones).</summary>
public interface IReferenceReadModel
{
    Task<IReadOnlyList<CountryDto>> CountriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DepartmentDto>> DepartmentsAsync(string countryCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<MunicipalityDto>> MunicipalitiesAsync(string? departmentCode, string? search, CancellationToken cancellationToken);

    Task<IReadOnlyList<IdentificationTypeDto>> IdentificationTypesAsync(string countryCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<CodeNameDto>> FiscalResponsibilitiesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CodeNameDto>> TaxRegimesAsync(CancellationToken cancellationToken);
}

public sealed record ListCountriesQuery : IQuery<IReadOnlyList<CountryDto>>;

public sealed record ListDepartmentsQuery(string CountryCode = "CO") : IQuery<IReadOnlyList<DepartmentDto>>;

public sealed record ListMunicipalitiesQuery(string? DepartmentCode, string? Search) : IQuery<IReadOnlyList<MunicipalityDto>>;

public sealed record ListIdentificationTypesQuery(string CountryCode = "CO") : IQuery<IReadOnlyList<IdentificationTypeDto>>;

public sealed record ListFiscalResponsibilitiesQuery : IQuery<IReadOnlyList<CodeNameDto>>;

public sealed record ListTaxRegimesQuery : IQuery<IReadOnlyList<CodeNameDto>>;

internal sealed class ReferenceQueryHandlers(IReferenceReadModel read) :
    IQueryHandler<ListCountriesQuery, IReadOnlyList<CountryDto>>,
    IQueryHandler<ListDepartmentsQuery, IReadOnlyList<DepartmentDto>>,
    IQueryHandler<ListMunicipalitiesQuery, IReadOnlyList<MunicipalityDto>>,
    IQueryHandler<ListIdentificationTypesQuery, IReadOnlyList<IdentificationTypeDto>>,
    IQueryHandler<ListFiscalResponsibilitiesQuery, IReadOnlyList<CodeNameDto>>,
    IQueryHandler<ListTaxRegimesQuery, IReadOnlyList<CodeNameDto>>
{
    public async Task<Result<IReadOnlyList<CountryDto>>> Handle(ListCountriesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.CountriesAsync(cancellationToken));

    public async Task<Result<IReadOnlyList<DepartmentDto>>> Handle(ListDepartmentsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.DepartmentsAsync(request.CountryCode, cancellationToken));

    public async Task<Result<IReadOnlyList<MunicipalityDto>>> Handle(ListMunicipalitiesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.MunicipalitiesAsync(request.DepartmentCode, request.Search, cancellationToken));

    public async Task<Result<IReadOnlyList<IdentificationTypeDto>>> Handle(ListIdentificationTypesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.IdentificationTypesAsync(request.CountryCode, cancellationToken));

    public async Task<Result<IReadOnlyList<CodeNameDto>>> Handle(ListFiscalResponsibilitiesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.FiscalResponsibilitiesAsync(cancellationToken));

    public async Task<Result<IReadOnlyList<CodeNameDto>>> Handle(ListTaxRegimesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.TaxRegimesAsync(cancellationToken));
}
