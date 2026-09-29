using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Expenses.Contracts;

/// <summary>Permisos del módulo Expenses (docs/fases/fase-06-propuesta.md §7). Los gastos menores desde la caja usan cash.session.operate.</summary>
public static class ExpensesPermissions
{
    public const string ExpenseManage = "expenses.expense.manage";
    public const string ExpenseView = "expenses.expense.view";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(ExpenseManage, "Registrar y anular gastos y sus categorías", isSensitive: true),
        new(ExpenseView, "Consultar gastos", isSensitive: false),
    ];
}

public sealed record ExpenseCategoryDto(Guid Id, Guid? ParentId, string Name, int SortOrder, string Status);

public sealed record ExpenseDto(
    Guid Id,
    string Number,
    DateOnly BusinessDate,
    Guid CategoryId,
    string CategoryName,
    Guid? PartyId,
    string Description,
    decimal Amount,
    decimal TaxAmount,
    Guid PaymentMethodId,
    Guid? CashSessionId,
    string? Reference,
    string Status,
    string? VoidReason,
    DateTimeOffset CreatedAt);
