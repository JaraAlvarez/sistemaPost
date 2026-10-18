using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Application.Business;

/// <summary>
/// La empresa que crea el instalador premium sin preguntar sus datos (NIT provisional) toma los de la empresa del portal en el primer
/// ingreso del dueño (ver <see cref="IProvisionalCompany"/>). Lo demás (dirección, municipio…) lo completa el dueño en Configuración.
/// </summary>
public sealed class ProvisionalCompany(IOrganizationStore store) : IProvisionalCompany
{
    public async Task<CompanyIdentityInfo?> GetAsync(Guid companyId, CancellationToken cancellationToken) =>
        await store.GetCompanyAsync(companyId, cancellationToken) is { } company
            ? new CompanyIdentityInfo(
                company.CheckDigit is null ? company.IdentificationNumber : $"{company.IdentificationNumber}-{company.CheckDigit}",
                company.HasProvisionalIdentification)
            : null;

    public async Task<Result> AdoptAsync(
        Guid companyId, string nitWithCheckDigit, string? legalName, string? businessProfile, CancellationToken cancellationToken)
    {
        if (await store.GetCompanyAsync(companyId, cancellationToken) is not { } company)
        {
            return OrganizationErrors.CompanyNotFound;
        }

        if (!company.HasProvisionalIdentification)
        {
            return Result.Success();
        }

        var parts = (nitWithCheckDigit ?? string.Empty).Split('-', StringSplitOptions.TrimEntries);
        if (parts is not [var number, var checkDigit])
        {
            return Error.Validation("ORGANIZATION.PORTAL_NIT_INVALID", "El NIT de la empresa del portal no es válido.");
        }

        var name = string.IsNullOrWhiteSpace(legalName) ? company.LegalName : legalName.Trim();
        var updated = company.Update(new CompanyData(
            name, string.IsNullOrWhiteSpace(legalName) ? company.TradeName : name, company.PersonType == PersonType.Natural && !string.IsNullOrWhiteSpace(legalName) ? PersonType.Legal : company.PersonType,
            Company.NitType, number, checkDigit, company.TaxRegime, [.. company.FiscalResponsibilities.Select(r => r.Code)],
            company.CountryCode, company.MunicipalityCode, company.Address, company.Phone, company.Email, company.CurrencyCode, company.Timezone));
        if (updated.IsFailure)
        {
            return updated;
        }

        if (BusinessProfiles.TryParseProfile(businessProfile, out var profile) && profile != company.BusinessProfile)
        {
            company.ApplyProfile(profile);
        }

        return Result.Success();
    }
}
