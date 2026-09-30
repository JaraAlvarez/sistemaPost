using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Cash.Application;

/// <summary>Maestros y jornadas de la caja (EF Core).</summary>
public interface ICashStore
{
    void Add(PaymentMethod method);

    void Add(Denomination denomination);

    void Add(CashSession session);

    void Add(CashMovement movement);

    Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(CancellationToken cancellationToken);

    Task<PaymentMethod?> GetPaymentMethodAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Denomination>> GetDenominationsAsync(CancellationToken cancellationToken);

    Task<CashSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Jornada sin cerrar de la caja (o del cajero, si no se indica caja).</summary>
    Task<CashSession?> GetUnclosedSessionAsync(Guid? posTerminalId, Guid? cashierId, CancellationToken cancellationToken);
}

public sealed class CashPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => CashPermissions.All;
}

/// <summary>
/// Medios de pago y denominaciones iniciales de cada empresa (aprobados en la Fase 6, pregunta 5). Los códigos de medio de pago de la
/// factura electrónica se validan con Factus en la Fase 11-B. Idempotente: solo crea los que faltan.
/// </summary>
public sealed class CashInitializer(ICashStore store, IIdGenerator ids) : ICompanyInitializer
{
    public int Order => 60;

    public async Task InitializeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var existing = (await store.GetPaymentMethodsAsync(cancellationToken)).Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        (string Code, string Name, PaymentMethodKind Kind, string Dian, bool Reference)[] seeds =
        [
            (PaymentMethod.CashCode, "Efectivo", PaymentMethodKind.Cash, "10", false),
            ("DEBITO", "Tarjeta débito", PaymentMethodKind.DebitCard, "49", true),
            ("CREDITO", "Tarjeta crédito", PaymentMethodKind.CreditCard, "48", true),
            ("TRANSFERENCIA", "Transferencia", PaymentMethodKind.Transfer, "47", true),
            ("NEQUI", "Nequi", PaymentMethodKind.Wallet, "47", true),
            ("DAVIPLATA", "Daviplata", PaymentMethodKind.Wallet, "47", true),
            ("BONO", "Bono", PaymentMethodKind.Voucher, "71", true),
            (PaymentMethod.ExchangeCreditCode, "Crédito por cambio", PaymentMethodKind.ExchangeCredit, "ZZZ", false),
        ];
        var order = 0;
        foreach (var (code, name, kind, dian, reference) in seeds)
        {
            order += 10;
            if (!existing.Contains(code))
            {
                store.Add(PaymentMethod.Create(ids.NewId(), companyId, code, name, kind, dian, reference, order, isSystem: true).Value);
            }
        }

        // Denominaciones del peso colombiano en circulación (Fase 6, D6-10).
        var values = (await store.GetDenominationsAsync(cancellationToken)).Select(d => d.Value).ToHashSet();
        (decimal Value, DenominationKind Kind)[] cop =
        [
            (100_000m, DenominationKind.Bill), (50_000m, DenominationKind.Bill), (20_000m, DenominationKind.Bill), (10_000m, DenominationKind.Bill),
            (5_000m, DenominationKind.Bill), (2_000m, DenominationKind.Bill), (1_000m, DenominationKind.Coin), (500m, DenominationKind.Coin),
            (200m, DenominationKind.Coin), (100m, DenominationKind.Coin), (50m, DenominationKind.Coin),
        ];
        for (var i = 0; i < cop.Length; i++)
        {
            if (!values.Contains(cop[i].Value))
            {
                store.Add(Denomination.Create(ids.NewId(), companyId, DefaultCurrency, cop[i].Value, cop[i].Kind, (i + 1) * 10).Value);
            }
        }
    }

    public const string DefaultCurrency = "COP";
}

public static class PaymentMethodMapping
{
    public static PaymentMethodDto ToDto(this PaymentMethod m) => new(
        m.Id, m.Code, m.Name, Db(m.Kind), m.DianCode, m.RequiresReference, m.AffectsCashDrawer, m.SortOrder, m.IsSystem, Db(m.Status));

    public static string Db<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}

public sealed record ListPaymentMethodsQuery(bool IncludeInactive) : IQuery<IReadOnlyList<PaymentMethodDto>>;

internal sealed class ListPaymentMethodsHandler(ICashStore store) : IQueryHandler<ListPaymentMethodsQuery, IReadOnlyList<PaymentMethodDto>>
{
    public async Task<Result<IReadOnlyList<PaymentMethodDto>>> Handle(ListPaymentMethodsQuery request, CancellationToken cancellationToken) =>
        (await store.GetPaymentMethodsAsync(cancellationToken))
            .Where(m => request.IncludeInactive || m.Status == MasterStatus.Active)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.Code, StringComparer.Ordinal)
            .Select(m => m.ToDto()).ToList();
}

public sealed record CreatePaymentMethodCommand(string Code, string Name, PaymentMethodKind Kind, string? DianCode, bool RequiresReference, int SortOrder)
    : ICommand<PaymentMethodDto>;

internal sealed class CreatePaymentMethodHandler(IInstallationContext installation, ICashStore store, IIdGenerator ids)
    : ICommandHandler<CreatePaymentMethodCommand, PaymentMethodDto>
{
    public Task<Result<PaymentMethodDto>> Handle(CreatePaymentMethodCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return Task.FromResult<Result<PaymentMethodDto>>(Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup)."));
        }

        if (request.Kind == PaymentMethodKind.ExchangeCredit)
        {
            return Task.FromResult<Result<PaymentMethodDto>>(CashErrors.ExchangeCreditReserved);
        }

        if (request.Kind is PaymentMethodKind.CustomerCredit or PaymentMethodKind.LoyaltyPoints)
        {
            return Task.FromResult<Result<PaymentMethodDto>>(CashErrors.PaymentKindNotAvailable);
        }

        var method = PaymentMethod.Create(ids.NewId(), companyId, request.Code, request.Name, request.Kind, request.DianCode, request.RequiresReference, request.SortOrder);
        if (method.IsFailure)
        {
            return Task.FromResult<Result<PaymentMethodDto>>(method.Error);
        }

        store.Add(method.Value);
        return Task.FromResult<Result<PaymentMethodDto>>(method.Value.ToDto());
    }
}

public sealed record UpdatePaymentMethodCommand(Guid PaymentMethodId, string Name, string? DianCode, bool RequiresReference, int SortOrder, bool IsActive)
    : ICommand<PaymentMethodDto>;

internal sealed class UpdatePaymentMethodHandler(ICashStore store) : ICommandHandler<UpdatePaymentMethodCommand, PaymentMethodDto>
{
    public async Task<Result<PaymentMethodDto>> Handle(UpdatePaymentMethodCommand request, CancellationToken cancellationToken)
    {
        var method = await store.GetPaymentMethodAsync(request.PaymentMethodId, cancellationToken);
        if (method is null)
        {
            return CashErrors.PaymentMethodNotFound;
        }

        var updated = method.Update(request.Name, request.DianCode, request.RequiresReference, request.SortOrder, request.IsActive);
        return updated.IsSuccess ? method.ToDto() : updated.Error;
    }
}
