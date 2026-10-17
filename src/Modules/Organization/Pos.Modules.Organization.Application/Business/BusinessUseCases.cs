using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Text;

namespace Pos.Modules.Organization.Application.Business;

/// <summary>
/// Tipo de negocio y funciones activables (Fase 7-C parte A, docs/fases/fase-07c-propuesta.md §1). Apagar una función OCULTA pantallas y
/// campos: nunca borra datos. Se administra con el permiso de los datos de la empresa (<c>organization.company.manage</c>).
/// </summary>
internal static class BusinessMapping
{
    public static BusinessFeaturesDto ToFeaturesDto(this Company company) => new(
        company.BusinessProfile.UpperSnake(),
        [.. company.Features.Select(f => f.UpperSnake()).Order(StringComparer.Ordinal)]);

    public static string Names(IEnumerable<BusinessFeature> features)
    {
        var names = features.Select(f => BusinessCatalog.Feature(f.UpperSnake())?.Name ?? f.UpperSnake()).Order(StringComparer.CurrentCulture).ToList();
        return names.Count == 0 ? "ninguna" : string.Join(", ", names);
    }

    public static string ProfileName(BusinessProfile profile) => BusinessCatalog.Profile(profile.UpperSnake())?.Name ?? profile.UpperSnake();
}

/// <summary>Lectura para los demás módulos (<see cref="IBusinessFeatures"/>).</summary>
public sealed class BusinessFeaturesReader(IInstallationContext installation, IOrganizationStore store) : IBusinessFeatures
{
    public async Task<BusinessFeaturesDto?> GetAsync(CancellationToken cancellationToken) =>
        installation.CompanyId is { } companyId && await store.GetCompanyAsync(companyId, cancellationToken) is { } company
            ? company.ToFeaturesDto()
            : null;
}

public sealed record GetBusinessFeaturesQuery : IQuery<BusinessFeaturesDto>;

internal sealed class GetBusinessFeaturesHandler(IInstallationContext installation, IOrganizationStore store)
    : IQueryHandler<GetBusinessFeaturesQuery, BusinessFeaturesDto>
{
    public async Task<Result<BusinessFeaturesDto>> Handle(GetBusinessFeaturesQuery request, CancellationToken cancellationToken)
    {
        var company = await BusinessCompany.LoadAsync(installation, store, cancellationToken);
        return company.IsSuccess ? company.Value.ToFeaturesDto() : company.Error;
    }
}

/// <summary>Cambia el tipo de negocio y deja los interruptores en los valores de ese tipo (la interfaz lo confirma antes).</summary>
public sealed record ChangeBusinessProfileCommand(string Profile) : ICommand<BusinessFeaturesDto>;

internal sealed class ChangeBusinessProfileValidator : AbstractValidator<ChangeBusinessProfileCommand>
{
    public ChangeBusinessProfileValidator() => RuleFor(x => x.Profile).NotEmpty().MaximumLength(30);
}

internal sealed class ChangeBusinessProfileHandler(IInstallationContext installation, IOrganizationStore store, IAuditWriter audit)
    : ICommandHandler<ChangeBusinessProfileCommand, BusinessFeaturesDto>
{
    public async Task<Result<BusinessFeaturesDto>> Handle(ChangeBusinessProfileCommand request, CancellationToken cancellationToken)
    {
        if (!BusinessProfiles.TryParseProfile(request.Profile, out var profile))
        {
            return OrganizationErrors.InvalidBusinessProfile;
        }

        var company = await BusinessCompany.LoadAsync(installation, store, cancellationToken);
        if (company.IsFailure)
        {
            return company.Error;
        }

        var before = company.Value.ToFeaturesDto();
        var previous = company.Value.BusinessProfile;
        company.Value.ApplyProfile(profile);
        await BusinessCompany.AuditAsync(
            audit, company.Value, before,
            $"Tipo de negocio: {BusinessMapping.ProfileName(previous)} → {BusinessMapping.ProfileName(profile)}. Funciones encendidas: " +
            $"{BusinessMapping.Names(company.Value.Features)}.",
            cancellationToken);
        return company.Value.ToFeaturesDto();
    }
}

/// <summary>Enciende exactamente estas funciones; el tipo de negocio no cambia.</summary>
public sealed record SetBusinessFeaturesCommand(IReadOnlyList<string> EnabledFeatures) : ICommand<BusinessFeaturesDto>;

internal sealed class SetBusinessFeaturesValidator : AbstractValidator<SetBusinessFeaturesCommand>
{
    public SetBusinessFeaturesValidator() => RuleFor(x => x.EnabledFeatures).NotNull();
}

internal sealed class SetBusinessFeaturesHandler(IInstallationContext installation, IOrganizationStore store, IAuditWriter audit)
    : ICommandHandler<SetBusinessFeaturesCommand, BusinessFeaturesDto>
{
    public async Task<Result<BusinessFeaturesDto>> Handle(SetBusinessFeaturesCommand request, CancellationToken cancellationToken)
    {
        var features = new HashSet<BusinessFeature>();
        foreach (var code in request.EnabledFeatures)
        {
            if (!BusinessProfiles.TryParseFeature(code, out var feature))
            {
                return OrganizationErrors.InvalidBusinessFeature;
            }

            features.Add(feature);
        }

        var company = await BusinessCompany.LoadAsync(installation, store, cancellationToken);
        if (company.IsFailure)
        {
            return company.Error;
        }

        var previous = company.Value.Features;
        if (previous.SetEquals(features))
        {
            return company.Value.ToFeaturesDto();
        }

        var before = company.Value.ToFeaturesDto();
        company.Value.SetFeatures(features);
        var on = features.Except(previous).ToList();
        var off = previous.Except(features).ToList();
        var summary = string.Join(" ", new[]
        {
            on.Count > 0 ? $"Encendidas: {BusinessMapping.Names(on)}." : null,
            off.Count > 0 ? $"Apagadas (se ocultan, los datos se conservan): {BusinessMapping.Names(off)}." : null,
        }.Where(s => s is not null));
        await BusinessCompany.AuditAsync(audit, company.Value, before, $"Funciones del negocio. {summary}", cancellationToken);
        return company.Value.ToFeaturesDto();
    }
}

internal static class BusinessCompany
{
    public const string AuditAction = "BUSINESS_FEATURES_CHANGED";

    public static async Task<Result<Company>> LoadAsync(IInstallationContext installation, IOrganizationStore store, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return companyId.Error;
        }

        var company = await store.GetCompanyAsync(companyId.Value, cancellationToken);
        return company is null ? OrganizationErrors.CompanyNotFound : company;
    }

    public static Task AuditAsync(IAuditWriter audit, Company company, BusinessFeaturesDto before, string summary, CancellationToken cancellationToken)
    {
        var after = company.ToFeaturesDto();
        return audit.WriteAsync(
            new AuditEntry(
                Module: "organization",
                Action: AuditAction,
                EntityType: nameof(Company),
                EntityId: company.Id,
                EntityLabel: company.AuditLabel,
                Summary: summary,
                OldValues: new Dictionary<string, object?> { ["profile"] = before.Profile, ["features"] = string.Join(',', before.EnabledFeatures) },
                NewValues: new Dictionary<string, object?> { ["profile"] = after.Profile, ["features"] = string.Join(',', after.EnabledFeatures) },
                Severity: AuditSeverity.Warning),
            cancellationToken);
    }
}
