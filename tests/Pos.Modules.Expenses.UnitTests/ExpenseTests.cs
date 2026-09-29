using Pos.Modules.Expenses.Application;
using Pos.Modules.Expenses.Domain;

namespace Pos.Modules.Expenses.UnitTests;

public class ExpenseTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    private static ExpenseData Data(decimal amount = 85_000m, decimal tax = 13_571.43m, string description = " Factura de energía septiembre ", Guid? session = null) =>
        new(new DateOnly(2026, 9, 28), Guid.NewGuid(), null, description, amount, tax, Guid.NewGuid(), session, " FAC-9981 ");

    [Fact]
    public void Categorias_de_dos_niveles()
    {
        var services = ExpenseCategory.Create(Guid.NewGuid(), Company, " Servicios públicos ", null, 10).Value;
        services.Name.ShouldBe("Servicios públicos");
        var energy = ExpenseCategory.Create(Guid.NewGuid(), Company, "Energía", services).Value;
        energy.ParentId.ShouldBe(services.Id);
        ExpenseCategory.Create(Guid.NewGuid(), Company, "Nivel 3", energy).Error.ShouldBe(ExpensesErrors.CategoryTooDeep);
        ExpenseCategory.Create(Guid.NewGuid(), Company, " ", null).Error.ShouldBe(ExpensesErrors.InvalidCategory);
        energy.Update("Energía eléctrica", 5, isActive: false).IsSuccess.ShouldBeTrue();
        energy.Status.ShouldBe(MasterStatus.Inactive);
        energy.AuditLabel.ShouldContain("Energía eléctrica");
    }

    [Fact]
    public void Gasto_valido_y_su_anulacion()
    {
        var session = Guid.NewGuid();
        var expense = Expense.Create(Guid.NewGuid(), Company, Guid.NewGuid(), "S01-000001", Data(session: session)).Value;
        expense.Description.ShouldBe("Factura de energía septiembre");
        expense.Reference.ShouldBe("FAC-9981");
        expense.CashSessionId.ShouldBe(session);
        expense.Status.ShouldBe(ExpenseStatus.Posted);
        expense.AuditLabel.ShouldBe("Gasto S01-000001");

        expense.Void("no", Guid.NewGuid(), Now).Error.ShouldBe(ExpensesErrors.ReasonRequired);
        expense.Void("Se registró dos veces", Guid.NewGuid(), Now).IsSuccess.ShouldBeTrue();
        expense.Status.ShouldBe(ExpenseStatus.Voided);
        expense.VoidedAt.ShouldBe(Now);
        expense.Void("Se registró dos veces", Guid.NewGuid(), Now).Error.ShouldBe(ExpensesErrors.InvalidStatus);
    }

    [Theory]
    [InlineData(0, 0, "Aseo")]
    [InlineData(100, 101, "Aseo")]
    [InlineData(100, -1, "Aseo")]
    [InlineData(100.001, 0, "Aseo")]
    [InlineData(100, 0, "")]
    public void Gastos_invalidos(double amount, double tax, string description) =>
        Expense.Create(Guid.NewGuid(), Company, Guid.NewGuid(), "N", Data((decimal)amount, (decimal)tax, description)).Error.ShouldBe(ExpensesErrors.InvalidExpense);

    [Fact]
    public void Permisos_publicados() => new ExpensesPermissionCatalog().GetPermissions().Count().ShouldBe(2);
}
