using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Expenses.Application;
using Pos.Modules.Expenses.Contracts;
using Pos.Modules.Expenses.Infrastructure;

namespace Pos.Modules.Expenses.Api;

/// <summary>Módulo Expenses: gastos (desde la caja o fuera de ella) y sus categorías.</summary>
public sealed class ExpensesModule : IModule
{
    public string Name => "expenses";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IExpenseStore).Assembly);
        ExpensesInfrastructureRegistration.Register(services);
        services.AddScoped<ICompanyInitializer, ExpensesInitializer>();
        services.AddSingleton<IPermissionCatalogProvider, ExpensesPermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/expenses").WithTags("Gastos");

        group.MapGet("/categories", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListExpenseCategoriesQuery(), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .WithSummary("Categorías de gasto (dos niveles)");
        group.MapPost("/categories", async (CreateExpenseCategoryCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(c => $"/api/v1/expenses/categories/{c.Id}"))
            .RequirePermission(ExpensesPermissions.ExpenseManage);
        group.MapPut("/categories/{categoryId:guid}", async (Guid categoryId, CategoryRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateExpenseCategoryCommand(categoryId, r.Name, r.SortOrder, r.IsActive), ct)).ToHttpResult())
            .RequirePermission(ExpensesPermissions.ExpenseManage);

        group.MapGet("/", async (DateOnly? from, DateOnly? to, Guid? categoryId, Guid? cashSessionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListExpensesQuery(from, to, categoryId, cashSessionId), ct)).ToHttpResult())
            .RequirePermission(ExpensesPermissions.ExpenseView)
            .WithSummary("Gastos de la sucursal por fecha de negocio, categoría o jornada de caja");
        group.MapPost("/", async (ExpenseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateExpenseCommand(r), ct)).ToCreatedResult(e => $"/api/v1/expenses/{e.Id}"))
            .RequirePermission(ExpensesPermissions.ExpenseManage)
            .WithSummary("Registra un gasto (con cashSessionId sale del cajón en la misma transacción)");
        group.MapPost("/from-cash", async (ExpenseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateExpenseCommand(r, FromCash: true), ct)).ToCreatedResult(e => $"/api/v1/expenses/{e.Id}"))
            .RequirePermission(CashPermissions.SessionOperate)
            .WithSummary("Gasto menor pagado desde la jornada de caja del cajero");
        group.MapPost("/{expenseId:guid}/void", async (Guid expenseId, VoidRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new VoidExpenseCommand(expenseId, r.Reason), ct)).ToHttpResult())
            .RequirePermission(ExpensesPermissions.ExpenseManage)
            .WithSummary("Anula el gasto (si salió de la caja, el dinero vuelve con una corrección en esa jornada abierta)");
    }
}

public sealed record CategoryRequest(string Name, int SortOrder, bool IsActive);

public sealed record VoidRequest(string Reason);
