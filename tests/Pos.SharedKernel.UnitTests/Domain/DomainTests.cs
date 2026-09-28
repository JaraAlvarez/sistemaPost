using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.SharedKernel.UnitTests.Domain;

public class EntityTests
{
    [Fact]
    public void Igualdad_por_tipo_e_id()
    {
        var id = Guid.CreateVersion7();

        var a = new Product(id);
        var b = new Product(id);

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
        (a != new Product(Guid.CreateVersion7())).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.Equals((object)b).ShouldBeTrue();
        a.Equals(null).ShouldBeFalse();
    }

    [Fact]
    public void Tipos_distintos_con_el_mismo_id_no_son_iguales()
    {
        var id = Guid.CreateVersion7();

        new Product(id).Equals(new Category(id)).ShouldBeFalse();
    }

    [Fact]
    public void Agregado_acumula_y_limpia_eventos()
    {
        var sale = new Sale(Guid.CreateVersion7());

        sale.Complete();

        sale.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<SaleCompleted>();
        sale.ClearDomainEvents();
        sale.DomainEvents.ShouldBeEmpty();
    }

    private sealed class Product(Guid id) : Entity<Guid>(id);

    private sealed class Category(Guid id) : Entity<Guid>(id);

    private sealed record SaleCompleted(Guid EventId, DateTimeOffset OccurredAt) : DomainEvent(EventId, OccurredAt);

    private sealed class Sale(Guid id) : AggregateRoot<Guid>(id)
    {
        public void Complete() => Raise(new SaleCompleted(Guid.CreateVersion7(), DateTimeOffset.UtcNow));
    }
}

public class DomainExceptionTests
{
    [Fact]
    public void Conserva_el_error_de_negocio()
    {
        var error = Error.BusinessRule("SALES.CLOSED", "La venta está cerrada.");

        var ex = new DomainException(error);

        ex.Error.ShouldBe(error);
        ex.Message.ShouldBe("La venta está cerrada.");
    }

    [Fact]
    public void Constructores_estandar()
    {
        new DomainException().Error.Code.ShouldBe("DOMAIN.INVARIANT_VIOLATED");
        new DomainException("mensaje").Error.Message.ShouldBe("mensaje");

        var inner = new InvalidOperationException();
        var ex = new DomainException("mensaje", inner);
        ex.InnerException.ShouldBe(inner);
        ex.Error.Type.ShouldBe(ErrorType.BusinessRule);
    }
}
