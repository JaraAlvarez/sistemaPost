namespace Pos.ArchitectureTests;

/// <summary>
/// Casos negativos: demuestran que cada regla SÍ detecta una violación. Sin esto, una regla que nunca
/// falla (p. ej. por un filtro mal escrito) pasaría desapercibida.
/// </summary>
public class ArchitectureRulesNegativeTests
{
    private static AssemblyNode Node(string name, params string[] references) => new(name, references);

    [Fact]
    public void R1_detecta_SharedKernel_con_dependencias_externas()
    {
        var violations = ArchitectureRules.SharedKernelIsPure(
            [Node("Pos.SharedKernel", "System.Runtime", "Microsoft.EntityFrameworkCore", "Pos.Infrastructure")]);

        violations.Count().ShouldBe(2);
    }

    [Fact]
    public void R2_detecta_Domain_que_depende_de_infraestructura()
    {
        var violations = ArchitectureRules.DomainDependsOnlyOnSharedKernel(
            [
                Node("Pos.Modules.Sales.Domain", "System.Runtime", "Pos.SharedKernel", "Npgsql"),
                Node("Pos.Modules.Catalog.Domain", "System.Runtime", "Pos.SharedKernel"),
            ]);

        violations.ShouldHaveSingleItem().ShouldContain("Npgsql");
    }

    [Fact]
    public void R3_detecta_acceso_a_capas_internas_de_otro_modulo()
    {
        var violations = ArchitectureRules.ModulesOnlyUseOtherModulesContracts(
            [
                Node("Pos.Modules.Sales.Application", "Pos.Modules.Inventory.Contracts", "Pos.Modules.Sales.Domain"),
                Node("Pos.Modules.Sales.Api", "Pos.Modules.Inventory.Infrastructure"),
                Node("Pos.Modules.Sales.Infrastructure", "Pos.Modules.Catalog.Domain"),
            ]).ToList();

        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Contains("Pos.Modules.Inventory.Infrastructure"));
        violations.ShouldContain(v => v.Contains("Pos.Modules.Catalog.Domain"));
    }

    [Fact]
    public void R4_detecta_Application_que_depende_de_Infrastructure()
    {
        var violations = ArchitectureRules.ApplicationDoesNotDependOnOuterLayers(
            [Node("Pos.Modules.Sales.Application", "Pos.Modules.Sales.Infrastructure", "Pos.Modules.Sales.Domain")]);

        violations.ShouldHaveSingleItem();
    }

    [Fact]
    public void R5_detecta_capas_invertidas_y_dependencias_del_host()
    {
        var violations = ArchitectureRules.BuildingBlocksLayering(
            [
                Node("Pos.Application.Abstractions", "Pos.Infrastructure"),
                Node("Pos.Modules.Sales.Api", "Pos.Server.Host"),
                Node("Pos.Server.Host", "Pos.Infrastructure"),
            ]);

        violations.Count().ShouldBe(2);
    }

    [Fact]
    public void R6_detecta_double_y_float_en_la_superficie_publica()
    {
        var violations = ArchitectureRules.NoFloatingPointInPublicSurface([typeof(BadDomainType), typeof(GoodDomainType)]).ToList();

        // Price, Weight, Discount, Total(factor) y el parámetro del constructor.
        violations.Count.ShouldBe(5);
        violations.ShouldAllBe(v => v.Contains(nameof(BadDomainType)));
    }

    [Theory]
    [InlineData("Pos.Modules.Sales.Domain", true)]
    [InlineData("Pos.Modules.Sales.Contracts", true)]
    [InlineData("Pos.SharedKernel", true)]
    [InlineData("Pos.Modules.Sales.Infrastructure", false)]
    [InlineData("Pos.Server.Host", false)]
    public void R6_aplica_a_las_capas_correctas(string assembly, bool applies) =>
        ArchitectureRules.AppliesFloatingPointRule(assembly).ShouldBe(applies);

    [Fact]
    public void ParseModule_reconoce_submodulos()
    {
        ArchitectureRules.ParseModule("Pos.Modules.Billing.Providers.Acme").ShouldBe(("Billing", "Providers"));
        ArchitectureRules.ParseModule("Pos.Infrastructure").ShouldBeNull();
    }
}

#pragma warning disable CA1822, CA1051 // Tipos de ejemplo para los casos negativos.
public sealed class BadDomainType(float discount)
{
    public double Price { get; set; }

    public double? Weight;

    public float Discount { get; } = discount;

    public decimal Total(double factor) => (decimal)factor;
}

public sealed class GoodDomainType
{
    public decimal Price { get; set; }

    internal double InternalValue { get; set; }
}
#pragma warning restore CA1822, CA1051
