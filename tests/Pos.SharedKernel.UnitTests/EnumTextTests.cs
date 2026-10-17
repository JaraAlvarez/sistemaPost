using Pos.SharedKernel.Text;

namespace Pos.SharedKernel.UnitTests;

/// <summary>Formato único de los estados en la API (Fase 17-E, D17E-02).</summary>
public class EnumTextTests
{
    private enum Tipo
    {
        SalesFloor,
        InTransit,
        Active,
        AdminWorkstation,
    }

    [Theory]
    [InlineData("SalesFloor", "SALES_FLOOR")]
    [InlineData("salesFloor", "SALES_FLOOR")]
    [InlineData("SALES_FLOOR", "SALES_FLOOR")]
    [InlineData("Active", "ACTIVE")]
    [InlineData("ACTIVE", "ACTIVE")]
    [InlineData("AdminWorkstation", "ADMIN_WORKSTATION")]
    [InlineData("PC850", "PC850")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normaliza_los_dos_formatos(string? value, string expected) => EnumText.Normalize(value).ShouldBe(expected);

    [Fact]
    public void Enumeraciones_en_mayusculas_con_guion_y_de_vuelta()
    {
        Tipo.SalesFloor.UpperSnake().ShouldBe("SALES_FLOOR");
        Tipo.InTransit.UpperSnake().ShouldBe("IN_TRANSIT");
        Tipo.AdminWorkstation.UpperSnake().ShouldBe("ADMIN_WORKSTATION");
        EnumText.ToEnumName("SALES_FLOOR").ShouldBe("SalesFloor");
        EnumText.ToEnumName("Active").ShouldBe("Active");
        EnumText.Same("Terminal", "TERMINAL").ShouldBeTrue();
        EnumText.Same("InTransit", "SALES_FLOOR").ShouldBeFalse();
    }
}
