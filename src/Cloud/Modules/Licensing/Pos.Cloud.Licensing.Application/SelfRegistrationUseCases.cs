using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

/// <summary>Crea la cuenta de cliente de un registro propio desde la tienda (ver <see cref="ICustomerSelfRegistration"/>).</summary>
public sealed class CustomerSelfRegistration(ILicensingStore store, IAuditWriter audit, IIdGenerator ids, IClock clock) : ICustomerSelfRegistration
{
    public async Task<Result<Guid>> RegisterAsync(string name, string email, CancellationToken cancellationToken)
    {
        var account = Account.SelfRegister(ids.NewId(), name, email, clock.UtcNow);
        if (account.IsFailure)
        {
            return account.Error;
        }

        store.Add(account.Value);
        await audit.WriteAsync(
            new AuditEntry("licensing", "ACCOUNT_SELF_REGISTERED", nameof(Account), account.Value.Id, account.Value.AuditLabel,
                $"Registro nuevo: {account.Value.Name} ({email}) entró con Google en el primer ingreso a una tienda; cuenta de cliente creada (prueba)."),
            cancellationToken);
        return account.Value.Id;
    }
}

/// <summary>Registro nuevo (posible cliente) para el portal: nombre, correo, fecha y si ya dejó una tienda con Propietario.</summary>
public sealed record SelfRegisteredAccountDto(Guid AccountId, string Name, string? Email, DateTimeOffset RegisteredAt, DateTimeOffset? FirstStoreLinkedAt);

/// <summary>"Registros nuevos": cuentas creadas solas desde la tienda (Continuar con Google), las más recientes primero.</summary>
[RequiresPermission(CloudPermissions.LicensingView)]
public sealed record ListSelfRegisteredAccountsQuery : IQuery<IReadOnlyList<SelfRegisteredAccountDto>>;

internal sealed class ListSelfRegisteredAccountsHandler(ILicensingReadModel read)
    : IQueryHandler<ListSelfRegisteredAccountsQuery, IReadOnlyList<SelfRegisteredAccountDto>>
{
    public async Task<Result<IReadOnlyList<SelfRegisteredAccountDto>>> Handle(ListSelfRegisteredAccountsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.ListSelfRegisteredAccountsAsync(cancellationToken));
}
