using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Modules.Reference.Application;
using Pos.Modules.Reference.Contracts;

namespace Pos.Modules.Reference.Infrastructure;

/// <summary>Lecturas con Dapper. Los nombres se ordenan con la colación española explícita (COLLATE public.es_co).</summary>
internal sealed class ReferenceReadModel(NpgsqlDataSource dataSource) : IReferenceReadModel
{
    public Task<IReadOnlyList<CountryDto>> CountriesAsync(CancellationToken cancellationToken) =>
        QueryAsync<CountryDto>(
            "SELECT code, iso3, name, phone_prefix AS PhonePrefix FROM ref.countries ORDER BY name COLLATE public.es_co", null, cancellationToken);

    public Task<IReadOnlyList<DepartmentDto>> DepartmentsAsync(string countryCode, CancellationToken cancellationToken) =>
        QueryAsync<DepartmentDto>(
            "SELECT code, name FROM ref.departments WHERE country_code = @countryCode ORDER BY name COLLATE public.es_co",
            new { countryCode },
            cancellationToken);

    public Task<IReadOnlyList<MunicipalityDto>> MunicipalitiesAsync(string? departmentCode, string? search, CancellationToken cancellationToken) =>
        QueryAsync<MunicipalityDto>(
            """
            SELECT code, department_code AS DepartmentCode, name, kind FROM ref.municipalities
            WHERE (@departmentCode::text IS NULL OR department_code = @departmentCode)
              AND (@search::text IS NULL OR unaccent(name) ILIKE '%' || unaccent(@search) || '%')
            ORDER BY name COLLATE public.es_co
            LIMIT 200
            """,
            new { departmentCode, search },
            cancellationToken);

    public Task<IReadOnlyList<IdentificationTypeDto>> IdentificationTypesAsync(string countryCode, CancellationToken cancellationToken) =>
        QueryAsync<IdentificationTypeDto>(
            """
            SELECT code, name, fiscal_code AS FiscalCode, requires_check_digit AS RequiresCheckDigit,
                   allows_natural AS AllowsNatural, allows_legal AS AllowsLegal
            FROM ref.identification_types WHERE country_code = @countryCode ORDER BY fiscal_code
            """,
            new { countryCode },
            cancellationToken);

    public Task<IReadOnlyList<CodeNameDto>> FiscalResponsibilitiesAsync(CancellationToken cancellationToken) =>
        QueryAsync<CodeNameDto>("SELECT code, name FROM ref.fiscal_responsibilities ORDER BY code", null, cancellationToken);

    public Task<IReadOnlyList<CodeNameDto>> TaxRegimesAsync(CancellationToken cancellationToken) =>
        QueryAsync<CodeNameDto>("SELECT code, name FROM ref.tax_regimes ORDER BY code", null, cancellationToken);

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();
    }
}

public static class ReferenceInfrastructureRegistration
{
    public static void Register(IServiceCollection services) => services.AddSingleton<IReferenceReadModel, ReferenceReadModel>();
}
