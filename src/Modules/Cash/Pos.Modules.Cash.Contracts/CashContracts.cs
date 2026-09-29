using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Cash.Contracts;

/// <summary>Permisos del módulo Cash (Fase 5: medios de pago; la Fase 6 agrega los de jornadas y cierres).</summary>
public static class CashPermissions
{
    public const string PaymentMethodManage = "cash.payment_method.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(PaymentMethodManage, "Crear y modificar medios de pago", isSensitive: true),
    ];
}

/// <summary>Medio de pago visto por otros módulos (compras, ventas). <c>Kind</c>: CASH, DEBIT_CARD, CREDIT_CARD, TRANSFER, WALLET, VOUCHER u OTHER.</summary>
public sealed record PaymentMethodInfo(
    Guid Id, string Code, string Name, string Kind, string? DianCode, bool RequiresReference, bool AffectsCashDrawer, bool IsActive);

public interface IPaymentMethodDirectory
{
    Task<PaymentMethodInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record PaymentMethodDto(
    Guid Id, string Code, string Name, string Kind, string? DianCode, bool RequiresReference, bool AffectsCashDrawer, int SortOrder, bool IsSystem, string Status);
