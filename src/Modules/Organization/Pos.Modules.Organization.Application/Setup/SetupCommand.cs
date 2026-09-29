using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Organization.Application.Setup;

public sealed record CompanyInput(
    string LegalName,
    string TradeName,
    PersonType PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string TaxRegime,
    IReadOnlyList<string> FiscalResponsibilities,
    string MunicipalityCode,
    string Address,
    string? Phone,
    string? Email);

public sealed record BranchInput(string Code, string Name, string MunicipalityCode, string Address, string? Phone);

public sealed record TerminalInput(string Code, string Name);

/// <summary>
/// Asistente de configuración inicial (docs/fases/fase-02-propuesta.md §8). Disponible solo mientras la instalación no
/// esté configurada. Todo en UNA transacción auditada.
/// </summary>
public sealed record SetupCommand(
    CompanyInput Company,
    BranchInput Branch,
    OwnerInput Owner,
    TerminalInput? Terminal = null,
    SetupMode Mode = SetupMode.NewCompany) : ICommand<SetupResultDto>;

internal sealed class SetupCommandValidator : AbstractValidator<SetupCommand>
{
    public SetupCommandValidator()
    {
        RuleFor(x => x.Company).NotNull().SetValidator(new CompanyInputValidator());
        RuleFor(x => x.Branch).NotNull().SetValidator(new BranchInputValidator());
        RuleFor(x => x.Owner).NotNull();
        RuleFor(x => x.Owner.Username).NotEmpty().MaximumLength(60).When(x => x.Owner is not null);
        RuleFor(x => x.Owner.DisplayName).NotEmpty().MaximumLength(120).When(x => x.Owner is not null);
        RuleFor(x => x.Owner.Password).NotEmpty().When(x => x.Owner is not null);
        RuleFor(x => x.Terminal!.Code).Matches("^[A-Z0-9]{2,6}$").When(x => x.Terminal is not null);
        RuleFor(x => x.Terminal!.Name).NotEmpty().MaximumLength(60).When(x => x.Terminal is not null);
    }
}

internal sealed class CompanyInputValidator : AbstractValidator<CompanyInput>
{
    public CompanyInputValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TradeName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.IdentificationType).NotEmpty().MaximumLength(10);
        RuleFor(x => x.IdentificationNumber).NotEmpty().MaximumLength(30).Matches("^[0-9A-Za-z-]+$");
        RuleFor(x => x.CheckDigit).NotEmpty().Matches("^[0-9]$").When(x => x.IdentificationType == Company.NitType)
            .WithMessage("El NIT requiere su dígito de verificación.");
        RuleFor(x => x.IdentificationNumber).Must(Nit.IsWellFormed).When(x => x.IdentificationType == Company.NitType)
            .WithMessage("El NIT debe tener solo dígitos, sin DV ni separadores.");
        RuleFor(x => x.TaxRegime).NotEmpty().MaximumLength(10);
        RuleFor(x => x.FiscalResponsibilities).NotNull();
        RuleFor(x => x.MunicipalityCode).NotEmpty().Matches("^[0-9]{5}$");
        RuleFor(x => x.Address).NotEmpty().MaximumLength(250);
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(200).When(x => !string.IsNullOrEmpty(x.Email));
    }
}

internal sealed class BranchInputValidator : AbstractValidator<BranchInput>
{
    public BranchInputValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[A-Z0-9]{2,6}$");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.MunicipalityCode).NotEmpty().Matches("^[0-9]{5}$");
        RuleFor(x => x.Address).NotEmpty().MaximumLength(250);
        RuleFor(x => x.Phone).MaximumLength(30);
    }
}

internal sealed class SetupCommandHandler(
    IInstallationContext installation,
    IInstallationSetup installationSetup,
    IActorContext actor,
    IOrganizationStore store,
    IIdentityProvisioning identity,
    IDocumentSeriesProvisioner series,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<SetupCommand, SetupResultDto>
{
    public const string DefaultCountry = "CO";
    public const string DefaultCurrency = "COP";
    public const string DefaultTimezone = BusinessTimeZones.ColombiaIanaId;

    public async Task<Result<SetupResultDto>> Handle(SetupCommand request, CancellationToken cancellationToken)
    {
        if (request.Mode == SetupMode.JoinCompany)
        {
            return OrganizationErrors.SetupModeNotAvailable;
        }

        if (!await installationSetup.TryBeginAsync(cancellationToken))
        {
            return OrganizationErrors.SetupAlreadyCompleted;
        }

        var input = request.Company;
        if (await store.CompanyExistsAsync(input.IdentificationType, input.IdentificationNumber, cancellationToken))
        {
            return OrganizationErrors.CompanyAlreadyExists;
        }

        var companyId = ids.NewId();
        var branchId = ids.NewId();
        var systemUserId = ids.NewId();

        // El asistente crea al usuario system en esta misma transacción: es el autor de todo lo que se crea aquí.
        actor.Use(systemUserId, "Sistema", companyId, branchId);

        var company = Company.Create(companyId, new CompanyData(
            input.LegalName, input.TradeName, input.PersonType, input.IdentificationType, input.IdentificationNumber,
            input.CheckDigit, input.TaxRegime, input.FiscalResponsibilities, DefaultCountry, input.MunicipalityCode,
            input.Address, input.Phone, input.Email, DefaultCurrency, DefaultTimezone));
        if (company.IsFailure)
        {
            return company.Error;
        }

        store.Add(company.Value);

        var branch = await BranchFactory.CreateWithWarehousesAsync(
            store, ids, companyId, branchId, request.Branch.Code, request.Branch.Name, request.Branch.MunicipalityCode,
            request.Branch.Address, request.Branch.Phone, cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        var terminalInput = request.Terminal ?? new TerminalInput("C01", "Caja 1");
        var terminal = PosTerminal.Create(
            ids.NewId(), companyId, branchId, terminalInput.Code, terminalInput.Name, branch.Value.SalesFloor);
        if (terminal.IsFailure)
        {
            return terminal.Error;
        }

        store.Add(terminal.Value);

        var nodeKind = installation.NodeRole == NodeRole.StoreServer ? NodeKind.StoreServer : NodeKind.AllInOne;
        store.Add(Node.RegisterLocal(installation.NodeId, companyId, branchId, nodeKind, $"{request.Branch.Code} · {request.Branch.Name}", clock.UtcNow));

        // La organización se guarda primero: usuarios, roles y series la referencian con FK.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await identity.ProvisionCompanyAsync(companyId, systemUserId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var owner = await identity.CreateOwnerAsync(companyId, request.Owner, cancellationToken);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        await series.CreateBranchSeriesAsync(companyId, branchId, request.Branch.Code, cancellationToken);
        await series.CreateTerminalSeriesAsync(companyId, branchId, request.Branch.Code, terminal.Value.Id, terminalInput.Code, cancellationToken);
        await installationSetup.CompleteAsync(companyId, branchId, SetupMode.NewCompany, cancellationToken);

        var edition = installation.NodeRole == NodeRole.StoreServer ? "MULTI" : "SINGLE";
        await audit.WriteAsync(
            new AuditEntry(
                Module: "organization",
                Action: "SETUP_COMPLETED",
                EntityType: nameof(Company),
                EntityId: companyId,
                EntityLabel: company.Value.AuditLabel,
                Summary: $"Configuración inicial completada: {company.Value.TradeName}, sucursal {request.Branch.Code}, caja {terminalInput.Code}, edición {edition}.",
                Severity: AuditSeverity.Warning),
            cancellationToken);

        return new SetupResultDto(companyId, branchId, terminal.Value.Id, branch.Value.SalesFloor.Id, installation.NodeId, systemUserId, owner.Value, edition);
    }
}

public sealed record GetSetupStatusQuery : IQuery<SetupStatusDto>;

internal sealed class GetSetupStatusHandler(IInstallationContext installation, IIdentityState identity) : IQueryHandler<GetSetupStatusQuery, SetupStatusDto>
{
    public async Task<Result<SetupStatusDto>> Handle(GetSetupStatusQuery request, CancellationToken cancellationToken) =>
        new SetupStatusDto(
            installation.IsSetupCompleted,
            installation.NodeRole == NodeRole.StoreServer ? "MULTI" : "SINGLE",
            installation.NodeId,
            installation.CompanyId,
            installation.BranchId,
            await identity.IsOwnerPendingAsync(cancellationToken));
}
