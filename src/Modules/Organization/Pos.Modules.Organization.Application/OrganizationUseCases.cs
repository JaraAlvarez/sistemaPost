using FluentValidation;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Modules.Organization.Application.Setup;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Application;

internal static class Mapping
{
    public static CompanyDto ToDto(this Company c) => new(
        c.Id, c.LegalName, c.TradeName, c.PersonType.ToString(), c.IdentificationType, c.IdentificationNumber, c.CheckDigit,
        c.TaxRegime, [.. c.FiscalResponsibilities.Select(r => r.Code).Order(StringComparer.Ordinal)], c.CountryCode,
        c.MunicipalityCode, c.Address, c.Phone, c.Email, c.CurrencyCode, c.Timezone, c.Status.ToString());

    public static BranchDto ToDto(this Branch b, Guid? homeBranchId) => new(
        b.Id, b.Code, b.Name, b.MunicipalityCode, b.Address, b.Phone, b.DefaultWarehouseId, b.Status.ToString(), b.Id == homeBranchId);

    public static Result<Guid> RequireCompany(this IInstallationContext installation) =>
        installation.CompanyId is { } id ? id : OrganizationErrors.SetupRequired;
}

// ─────────────────────────────── Empresa ───────────────────────────────

public sealed record GetCompanyQuery : IQuery<CompanyDto>;

internal sealed class GetCompanyHandler(IInstallationContext installation, IOrganizationStore store) : IQueryHandler<GetCompanyQuery, CompanyDto>
{
    public async Task<Result<CompanyDto>> Handle(GetCompanyQuery request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return companyId.Error;
        }

        var company = await store.GetCompanyAsync(companyId.Value, cancellationToken);
        return company is null ? OrganizationErrors.CompanyNotFound : company.ToDto();
    }
}

/// <summary>Actualiza los datos de la empresa. La identificación (tipo, número y DV) es inmutable.</summary>
public sealed record UpdateCompanyCommand(CompanyInput Company) : ICommand<CompanyDto>;

internal sealed class UpdateCompanyValidator : AbstractValidator<UpdateCompanyCommand>
{
    public UpdateCompanyValidator() => RuleFor(x => x.Company).NotNull().SetValidator(new CompanyInputValidator());
}

internal sealed class UpdateCompanyHandler(IInstallationContext installation, IOrganizationStore store)
    : ICommandHandler<UpdateCompanyCommand, CompanyDto>
{
    public async Task<Result<CompanyDto>> Handle(UpdateCompanyCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return companyId.Error;
        }

        var company = await store.GetCompanyAsync(companyId.Value, cancellationToken);
        if (company is null)
        {
            return OrganizationErrors.CompanyNotFound;
        }

        var input = request.Company;
        var result = company.Update(new CompanyData(
            input.LegalName, input.TradeName, input.PersonType, input.IdentificationType, input.IdentificationNumber,
            input.CheckDigit, input.TaxRegime, input.FiscalResponsibilities, company.CountryCode, input.MunicipalityCode,
            input.Address, input.Phone, input.Email, company.CurrencyCode, company.Timezone));
        return result.IsSuccess ? company.ToDto() : result.Error;
    }
}

// ─────────────────────────────── Sucursales ───────────────────────────────

public sealed record ListBranchesQuery : IQuery<IReadOnlyList<BranchDto>>;

internal sealed class ListBranchesHandler(IInstallationContext installation, IOrganizationStore store)
    : IQueryHandler<ListBranchesQuery, IReadOnlyList<BranchDto>>
{
    public async Task<Result<IReadOnlyList<BranchDto>>> Handle(ListBranchesQuery request, CancellationToken cancellationToken) =>
        installation.CompanyId is null
            ? OrganizationErrors.SetupRequired
            : Result.Success(await store.ListBranchesAsync(installation.BranchId, cancellationToken));
}

public sealed record GetBranchQuery(Guid BranchId) : IQuery<BranchDetailDto>;

internal sealed class GetBranchHandler(IInstallationContext installation, IOrganizationStore store) : IQueryHandler<GetBranchQuery, BranchDetailDto>
{
    public async Task<Result<BranchDetailDto>> Handle(GetBranchQuery request, CancellationToken cancellationToken)
    {
        var branch = (await store.ListBranchesAsync(installation.BranchId, cancellationToken)).SingleOrDefault(b => b.Id == request.BranchId);
        if (branch is null)
        {
            return OrganizationErrors.BranchNotFound;
        }

        return new BranchDetailDto(
            branch,
            await store.ListWarehousesAsync(branch.Id, cancellationToken),
            await store.ListTerminalsAsync(branch.Id, cancellationToken));
    }
}

/// <summary>Crea una sucursal con sus 3 bodegas y sus series. El código lo asigna la autoridad de la empresa.</summary>
public sealed record CreateBranchCommand(BranchInput Branch) : ICommand<BranchDetailDto>;

internal sealed class CreateBranchValidator : AbstractValidator<CreateBranchCommand>
{
    public CreateBranchValidator() => RuleFor(x => x.Branch).NotNull().SetValidator(new BranchInputValidator());
}

internal sealed class CreateBranchHandler(
    IInstallationContext installation,
    IOrganizationStore store,
    IDocumentSeriesProvisioner series,
    Pos.Application.Abstractions.Data.IUnitOfWork unitOfWork,
    IIdGenerator ids,
    IOrganizationQueries queries) : ICommandHandler<CreateBranchCommand, BranchDetailDto>
{
    public async Task<Result<BranchDetailDto>> Handle(CreateBranchCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return companyId.Error;
        }

        if (await store.BranchCodeExistsAsync(companyId.Value, request.Branch.Code, cancellationToken))
        {
            return OrganizationErrors.BranchCodeDuplicated;
        }

        var input = request.Branch;
        var created = await BranchFactory.CreateWithWarehousesAsync(
            store, ids, companyId.Value, ids.NewId(), input.Code, input.Name, input.MunicipalityCode, input.Address, input.Phone, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await series.CreateBranchSeriesAsync(companyId.Value, created.Value.Branch.Id, input.Code, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.GetBranchAsync(created.Value.Branch.Id, cancellationToken);
    }
}

public sealed record UpdateBranchCommand(Guid BranchId, string Name, string MunicipalityCode, string Address, string? Phone) : ICommand<BranchDto>;

internal sealed class UpdateBranchValidator : AbstractValidator<UpdateBranchCommand>
{
    public UpdateBranchValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.MunicipalityCode).NotEmpty().Matches("^[0-9]{5}$");
        RuleFor(x => x.Address).NotEmpty().MaximumLength(250);
        RuleFor(x => x.Phone).MaximumLength(30);
    }
}

internal sealed class UpdateBranchHandler(IInstallationContext installation, IOrganizationStore store) : ICommandHandler<UpdateBranchCommand, BranchDto>
{
    public async Task<Result<BranchDto>> Handle(UpdateBranchCommand request, CancellationToken cancellationToken)
    {
        var branch = await store.GetBranchAsync(request.BranchId, cancellationToken);
        if (branch is null)
        {
            return OrganizationErrors.BranchNotFound;
        }

        if (!await store.MunicipalityExistsAsync(request.MunicipalityCode, cancellationToken))
        {
            return Error.Validation("ORGANIZATION.UNKNOWN_MUNICIPALITY", $"El municipio {request.MunicipalityCode} no existe en DIVIPOLA.");
        }

        branch.Update(request.Name, request.MunicipalityCode, request.Address, request.Phone);
        return branch.ToDto(installation.BranchId);
    }
}

/// <summary>Inactivar: sin cajas activas, no la última sucursal activa y no la sucursal de esta instalación.</summary>
public sealed record DeactivateBranchCommand(Guid BranchId) : ICommand;

internal sealed class DeactivateBranchHandler(IInstallationContext installation, IOrganizationStore store) : ICommandHandler<DeactivateBranchCommand>
{
    public async Task<Result> Handle(DeactivateBranchCommand request, CancellationToken cancellationToken)
    {
        var branch = await store.GetBranchAsync(request.BranchId, cancellationToken);
        if (branch is null)
        {
            return OrganizationErrors.BranchNotFound;
        }

        if (branch.Status == RecordStatus.Inactive)
        {
            return Result.Success();
        }

        if (branch.Id == installation.BranchId)
        {
            return OrganizationErrors.HomeBranchCannotBeDeactivated;
        }

        if (await store.CountActiveTerminalsAsync(branch.Id, cancellationToken) > 0)
        {
            return OrganizationErrors.BranchHasActiveTerminals;
        }

        if (await store.CountActiveBranchesAsync(branch.CompanyId, cancellationToken) <= 1)
        {
            return OrganizationErrors.LastActiveBranch;
        }

        branch.Deactivate();
        return Result.Success();
    }
}

public sealed record ActivateBranchCommand(Guid BranchId) : ICommand;

internal sealed class ActivateBranchHandler(IOrganizationStore store) : ICommandHandler<ActivateBranchCommand>
{
    public async Task<Result> Handle(ActivateBranchCommand request, CancellationToken cancellationToken)
    {
        var branch = await store.GetBranchAsync(request.BranchId, cancellationToken);
        if (branch is null)
        {
            return OrganizationErrors.BranchNotFound;
        }

        branch.Activate();
        return Result.Success();
    }
}

// ─────────────────────────────── Bodegas ───────────────────────────────

public sealed record CreateWarehouseCommand(Guid BranchId, string Code, string Name, WarehouseKind Kind, bool AllowsSales) : ICommand<WarehouseDto>;

internal sealed class CreateWarehouseValidator : AbstractValidator<CreateWarehouseCommand>
{
    public CreateWarehouseValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[A-Z0-9-]{2,10}$");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Kind).IsInEnum();
    }
}

internal sealed class CreateWarehouseHandler(IOrganizationStore store, IIdGenerator ids) : ICommandHandler<CreateWarehouseCommand, WarehouseDto>
{
    public async Task<Result<WarehouseDto>> Handle(CreateWarehouseCommand request, CancellationToken cancellationToken)
    {
        var branch = await store.GetBranchAsync(request.BranchId, cancellationToken);
        if (branch is null)
        {
            return OrganizationErrors.BranchNotFound;
        }

        if (await store.WarehouseCodeExistsAsync(branch.Id, request.Code, cancellationToken))
        {
            return OrganizationErrors.WarehouseCodeDuplicated;
        }

        if (request.Kind == WarehouseKind.InTransit && await store.BranchHasInTransitWarehouseAsync(branch.Id, cancellationToken))
        {
            return OrganizationErrors.InTransitWarehouseDuplicated;
        }

        var warehouse = Warehouse.Create(ids.NewId(), branch.CompanyId, branch.Id, request.Code, request.Name, request.Kind, request.AllowsSales);
        if (warehouse.IsFailure)
        {
            return warehouse.Error;
        }

        store.Add(warehouse.Value);
        return ToDto(warehouse.Value);
    }

    internal static WarehouseDto ToDto(Warehouse w) =>
        new(w.Id, w.BranchId, w.Code, w.Name, w.Kind.ToString(), w.AllowsSales, w.Status.ToString());
}

public sealed record UpdateWarehouseCommand(Guid WarehouseId, string Name, bool AllowsSales) : ICommand<WarehouseDto>;

internal sealed class UpdateWarehouseValidator : AbstractValidator<UpdateWarehouseCommand>
{
    public UpdateWarehouseValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
}

internal sealed class UpdateWarehouseHandler(IOrganizationStore store) : ICommandHandler<UpdateWarehouseCommand, WarehouseDto>
{
    public async Task<Result<WarehouseDto>> Handle(UpdateWarehouseCommand request, CancellationToken cancellationToken)
    {
        var warehouse = await store.GetWarehouseAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null)
        {
            return OrganizationErrors.WarehouseNotFound;
        }

        if (!request.AllowsSales && warehouse.AllowsSales && await store.WarehouseAssignedToActiveTerminalAsync(warehouse.Id, cancellationToken))
        {
            return OrganizationErrors.WarehouseInUse;
        }

        var result = warehouse.Update(request.Name, request.AllowsSales);
        return result.IsSuccess ? CreateWarehouseHandler.ToDto(warehouse) : result.Error;
    }
}

/// <summary>Inactivar: no la bodega por defecto, no asignada a una caja activa y no una bodega de sistema.</summary>
public sealed record DeactivateWarehouseCommand(Guid WarehouseId) : ICommand;

internal sealed class DeactivateWarehouseHandler(IOrganizationStore store) : ICommandHandler<DeactivateWarehouseCommand>
{
    public async Task<Result> Handle(DeactivateWarehouseCommand request, CancellationToken cancellationToken)
    {
        var warehouse = await store.GetWarehouseAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null)
        {
            return OrganizationErrors.WarehouseNotFound;
        }

        if (warehouse.IsSystemWarehouse)
        {
            return OrganizationErrors.SystemWarehouseCannotBeRemoved;
        }

        var branch = await store.GetBranchAsync(warehouse.BranchId, cancellationToken);
        if (branch?.DefaultWarehouseId == warehouse.Id
            || await store.WarehouseAssignedToActiveTerminalAsync(warehouse.Id, cancellationToken))
        {
            return OrganizationErrors.WarehouseInUse;
        }

        // El stock distinto de cero se agrega como restricción en la Fase 4 (inventario).
        warehouse.Deactivate();
        return Result.Success();
    }
}

// ─────────────────────────────── Cajas ───────────────────────────────

public sealed record ListTerminalsQuery(Guid? BranchId = null) : IQuery<IReadOnlyList<TerminalDto>>;

internal sealed class ListTerminalsHandler(IOrganizationStore store) : IQueryHandler<ListTerminalsQuery, IReadOnlyList<TerminalDto>>
{
    public async Task<Result<IReadOnlyList<TerminalDto>>> Handle(ListTerminalsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await store.ListTerminalsAsync(request.BranchId, cancellationToken));
}

/// <summary>Crea una caja con sus series. En la edición Caja Única solo se admite una.</summary>
public sealed record CreateTerminalCommand(Guid BranchId, string Code, string Name, Guid? WarehouseId = null) : ICommand<TerminalDto>;

internal sealed class CreateTerminalValidator : AbstractValidator<CreateTerminalCommand>
{
    public CreateTerminalValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[A-Z0-9]{2,6}$");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
    }
}

internal sealed class CreateTerminalHandler(
    IInstallationContext installation,
    IOrganizationStore store,
    IDocumentSeriesProvisioner series,
    Pos.Application.Abstractions.Data.IUnitOfWork unitOfWork,
    IIdGenerator ids) : ICommandHandler<CreateTerminalCommand, TerminalDto>
{
    public async Task<Result<TerminalDto>> Handle(CreateTerminalCommand request, CancellationToken cancellationToken)
    {
        var branch = await store.GetBranchAsync(request.BranchId, cancellationToken);
        if (branch is null)
        {
            return OrganizationErrors.BranchNotFound;
        }

        // Edición = única diferencia comercial (revisión §10.2): Caja Única admite exactamente una caja.
        if (installation.NodeRole == NodeRole.AllInOne && await store.CountTerminalsAsync(cancellationToken) >= 1)
        {
            return OrganizationErrors.SingleTerminalEdition;
        }

        if (await store.TerminalCodeExistsAsync(branch.Id, request.Code, cancellationToken))
        {
            return OrganizationErrors.TerminalCodeDuplicated;
        }

        var warehouseId = request.WarehouseId ?? branch.DefaultWarehouseId;
        var warehouse = warehouseId is null ? null : await store.GetWarehouseAsync(warehouseId.Value, cancellationToken);
        if (warehouse is null)
        {
            return OrganizationErrors.TerminalWarehouseMustSell;
        }

        var terminal = PosTerminal.Create(ids.NewId(), branch.CompanyId, branch.Id, request.Code, request.Name, warehouse);
        if (terminal.IsFailure)
        {
            return terminal.Error;
        }

        store.Add(terminal.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await series.CreateTerminalSeriesAsync(branch.CompanyId, branch.Id, branch.Code, terminal.Value.Id, request.Code, cancellationToken);
        return ToDto(terminal.Value);
    }

    internal static TerminalDto ToDto(PosTerminal t) =>
        new(t.Id, t.BranchId, t.Code, t.Name, t.WarehouseId, t.DeviceId, t.Status.ToString());
}

public sealed record UpdateTerminalCommand(Guid TerminalId, string Name, Guid WarehouseId) : ICommand<TerminalDto>;

internal sealed class UpdateTerminalValidator : AbstractValidator<UpdateTerminalCommand>
{
    public UpdateTerminalValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
}

internal sealed class UpdateTerminalHandler(IOrganizationStore store) : ICommandHandler<UpdateTerminalCommand, TerminalDto>
{
    public async Task<Result<TerminalDto>> Handle(UpdateTerminalCommand request, CancellationToken cancellationToken)
    {
        var terminal = await store.GetTerminalAsync(request.TerminalId, cancellationToken);
        if (terminal is null)
        {
            return OrganizationErrors.TerminalNotFound;
        }

        var warehouse = await store.GetWarehouseAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null)
        {
            return OrganizationErrors.TerminalWarehouseMustSell;
        }

        var assigned = terminal.AssignWarehouse(warehouse);
        if (assigned.IsFailure)
        {
            return assigned.Error;
        }

        terminal.Rename(request.Name);
        return CreateTerminalHandler.ToDto(terminal);
    }
}

public sealed record ChangeTerminalStatusCommand(Guid TerminalId, TerminalStatus Status) : ICommand;

internal sealed class ChangeTerminalStatusHandler(IInstallationContext installation, IOrganizationStore store)
    : ICommandHandler<ChangeTerminalStatusCommand>
{
    public async Task<Result> Handle(ChangeTerminalStatusCommand request, CancellationToken cancellationToken)
    {
        var terminal = await store.GetTerminalAsync(request.TerminalId, cancellationToken);
        if (terminal is null)
        {
            return OrganizationErrors.TerminalNotFound;
        }

        switch (request.Status)
        {
            case TerminalStatus.Active:
                if (installation.NodeRole == NodeRole.AllInOne && terminal.Status != TerminalStatus.Active
                    && await store.CountActiveTerminalsAsync(null, cancellationToken) >= 1)
                {
                    return OrganizationErrors.SingleTerminalEdition;
                }

                terminal.Activate();
                break;
            case TerminalStatus.Inactive:
                // Las jornadas abiertas se agregan como restricción en la Fase 6 (caja).
                terminal.Deactivate();
                break;
            default:
                terminal.Block();
                break;
        }

        return Result.Success();
    }
}

/// <summary>Consultas reutilizadas por varios comandos para devolver el estado final.</summary>
public interface IOrganizationQueries
{
    Task<Result<BranchDetailDto>> GetBranchAsync(Guid branchId, CancellationToken cancellationToken);

    Task<Result<BranchDto>> GetBranchSummaryAsync(Guid branchId, CancellationToken cancellationToken);
}

public sealed class OrganizationQueries(IInstallationContext installation, IOrganizationStore store) : IOrganizationQueries
{
    public Task<Result<BranchDetailDto>> GetBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        new GetBranchHandler(installation, store).Handle(new GetBranchQuery(branchId), cancellationToken);

    public async Task<Result<BranchDto>> GetBranchSummaryAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var detail = await GetBranchAsync(branchId, cancellationToken);
        return detail.IsSuccess ? detail.Value.Branch : detail.Error;
    }
}
