namespace Pos.ArchitectureTests;

/// <summary>Las reglas aplicadas a los ensamblados reales de la solución.</summary>
public class ArchitectureTests
{
    [Fact]
    public void Se_cargan_todos_los_building_blocks_y_el_host()
    {
        ProductionAssemblies.All.Select(a => a.GetName().Name).ShouldBe(
            [
                ArchitectureRules.ApiAbstractions,
                ArchitectureRules.ApplicationAbstractions,
                ArchitectureRules.Infrastructure,
                ArchitectureRules.ServerHost,
                ArchitectureRules.SharedKernel,
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void R1_SharedKernel_es_puro() =>
        ArchitectureRules.SharedKernelIsPure(ProductionAssemblies.Graph).ShouldBeEmpty();

    [Fact]
    public void R2_Domain_solo_depende_del_SharedKernel() =>
        ArchitectureRules.DomainDependsOnlyOnSharedKernel(ProductionAssemblies.Graph).ShouldBeEmpty();

    [Fact]
    public void R3_Modulos_solo_usan_Contracts_de_otros_modulos() =>
        ArchitectureRules.ModulesOnlyUseOtherModulesContracts(ProductionAssemblies.Graph).ShouldBeEmpty();

    [Fact]
    public void R4_Application_no_depende_de_capas_externas() =>
        ArchitectureRules.ApplicationDoesNotDependOnOuterLayers(ProductionAssemblies.Graph).ShouldBeEmpty();

    [Fact]
    public void R5_Capas_de_building_blocks() =>
        ArchitectureRules.BuildingBlocksLayering(ProductionAssemblies.Graph).ShouldBeEmpty();

    [Fact]
    public void R6_Sin_double_ni_float_en_dominio_contratos_y_SharedKernel()
    {
        var types = ProductionAssemblies.All
            .Where(a => ArchitectureRules.AppliesFloatingPointRule(a.GetName().Name!))
            .SelectMany(a => a.GetTypes());

        ArchitectureRules.NoFloatingPointInPublicSurface(types).ShouldBeEmpty();
    }
}
