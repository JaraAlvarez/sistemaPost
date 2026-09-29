using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

// ─────────────────────────────── Cuentas ───────────────────────────────

[RequiresPermission(CloudPermissions.LicensingView)]
public sealed record ListAccountsQuery(string? Search = null) : IQuery<IReadOnlyList<AccountDto>>;

internal sealed class ListAccountsHandler(ILicensingReadModel read) : IQueryHandler<ListAccountsQuery, IReadOnlyList<AccountDto>>
{
    public async Task<Result<IReadOnlyList<AccountDto>>> Handle(ListAccountsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.ListAccountsAsync(request.Search, cancellationToken));
}

[RequiresPermission(CloudPermissions.LicensingView)]
public sealed record GetAccountQuery(Guid AccountId) : IQuery<AccountDto>;

internal sealed class GetAccountHandler(ILicensingReadModel read) : IQueryHandler<GetAccountQuery, AccountDto>
{
    public async Task<Result<AccountDto>> Handle(GetAccountQuery request, CancellationToken cancellationToken) =>
        await read.GetAccountAsync(request.AccountId, cancellationToken) is { } account ? account : LicensingErrors.AccountNotFound;
}

[RequiresPermission(CloudPermissions.AccountManage)]
public sealed record CreateAccountCommand(AccountInput Account) : ICommand<Guid>;

internal sealed class CreateAccountHandler(ILicensingStore store, IAuditWriter audit, IIdGenerator ids) : ICommandHandler<CreateAccountCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateAccountCommand request, CancellationToken cancellationToken)
    {
        var input = request.Account;
        if (!Enum.TryParse<AccountKind>(input.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            return LicensingErrors.InvalidAccount;
        }

        Account? parent = null;
        if (input.ParentAccountId is { } parentId && (parent = await store.GetAccountAsync(parentId, cancellationToken)) is null)
        {
            return LicensingErrors.AccountNotFound;
        }

        var account = Account.Create(ids.NewId(), kind, ToData(input), parent);
        if (account.IsFailure)
        {
            return account.Error;
        }

        store.Add(account.Value);
        await audit.WriteAsync(
            new AuditEntry("licensing", "ACCOUNT_REGISTERED", nameof(Account), account.Value.Id, account.Value.AuditLabel,
                $"Cuenta {account.Value.Name} ({kind}) registrada."),
            cancellationToken);
        return account.Value.Id;
    }

    internal static AccountData ToData(AccountInput input) =>
        new(input.Name, input.Nit, input.NitCheckDigit, input.ContactName, input.ContactEmail, input.ContactPhone, input.Notes);
}

[RequiresPermission(CloudPermissions.AccountManage)]
public sealed record UpdateAccountCommand(Guid AccountId, AccountInput Account, bool IsActive) : ICommand;

internal sealed class UpdateAccountHandler(ILicensingStore store) : ICommandHandler<UpdateAccountCommand>
{
    public async Task<Result> Handle(UpdateAccountCommand request, CancellationToken cancellationToken)
    {
        var account = await store.GetAccountAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return LicensingErrors.AccountNotFound;
        }

        Account? parent = null;
        if (request.Account.ParentAccountId is { } parentId && (parent = await store.GetAccountAsync(parentId, cancellationToken)) is null)
        {
            return LicensingErrors.AccountNotFound;
        }

        // El cambio queda en la auditoría automática (campos antes/después).
        return account.Update(CreateAccountHandler.ToData(request.Account), parent, request.IsActive);
    }
}

// ─────────────────────────────── Empresas ───────────────────────────────

[RequiresPermission(CloudPermissions.LicensingView)]
public sealed record ListOrganizationsQuery(Guid? AccountId = null, string? Search = null) : IQuery<IReadOnlyList<OrganizationRowDto>>;

internal sealed class ListOrganizationsHandler(ILicensingReadModel read, IClock clock) : IQueryHandler<ListOrganizationsQuery, IReadOnlyList<OrganizationRowDto>>
{
    public async Task<Result<IReadOnlyList<OrganizationRowDto>>> Handle(ListOrganizationsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.ListOrganizationsAsync(request.AccountId, request.Search, clock.UtcNow, cancellationToken));
}

[RequiresPermission(CloudPermissions.LicensingView)]
public sealed record GetOrganizationQuery(Guid OrganizationId) : IQuery<OrganizationDetailDto>;

internal sealed class GetOrganizationHandler(ILicensingReadModel read, IClock clock) : IQueryHandler<GetOrganizationQuery, OrganizationDetailDto>
{
    public async Task<Result<OrganizationDetailDto>> Handle(GetOrganizationQuery request, CancellationToken cancellationToken) =>
        await read.GetOrganizationAsync(request.OrganizationId, clock.UtcNow, cancellationToken) is { } detail ? detail : LicensingErrors.OrganizationNotFound;
}

[RequiresPermission(CloudPermissions.AccountManage)]
public sealed record CreateOrganizationCommand(Guid AccountId, string LegalName, string Nit, string NitCheckDigit, string? City) : ICommand<Guid>;

internal sealed class CreateOrganizationHandler(ILicensingStore store, IAuditWriter audit, IIdGenerator ids) : ICommandHandler<CreateOrganizationCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateOrganizationCommand request, CancellationToken cancellationToken)
    {
        var account = await store.GetAccountAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            return LicensingErrors.AccountNotFound;
        }

        var organization = Organization.Create(ids.NewId(), account, request.LegalName, request.Nit, request.NitCheckDigit, request.City);
        if (organization.IsFailure)
        {
            return organization.Error;
        }

        if (await store.NitExistsAsync(organization.Value.Nit, cancellationToken))
        {
            return LicensingErrors.NitDuplicated;
        }

        store.Add(organization.Value);
        await audit.WriteAsync(
            new AuditEntry("licensing", "ORGANIZATION_REGISTERED", nameof(Organization), organization.Value.Id, organization.Value.AuditLabel,
                $"Empresa {organization.Value.LegalName} (NIT {organization.Value.NitNumber}) registrada en la cuenta {account.Name}."),
            cancellationToken);
        return organization.Value.Id;
    }
}

[RequiresPermission(CloudPermissions.AccountManage)]
public sealed record UpdateOrganizationCommand(Guid OrganizationId, string LegalName, string? City, bool IsActive) : ICommand;

internal sealed class UpdateOrganizationHandler(ILicensingStore store) : ICommandHandler<UpdateOrganizationCommand>
{
    public async Task<Result> Handle(UpdateOrganizationCommand request, CancellationToken cancellationToken) =>
        await store.GetOrganizationAsync(request.OrganizationId, cancellationToken) is { } organization
            ? organization.Update(request.LegalName, request.City, request.IsActive)
            : LicensingErrors.OrganizationNotFound;
}
