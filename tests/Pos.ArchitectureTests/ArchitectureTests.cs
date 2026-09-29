namespace Pos.ArchitectureTests;

/// <summary>Las reglas aplicadas a los ensamblados reales de la solución.</summary>
public class ArchitectureTests
{
    [Fact]
    public void Se_cargan_los_building_blocks_el_host_y_los_modulos()
    {
        string[] modules =
        [
            "Pos.Modules.Audit.Api", "Pos.Modules.Audit.Application", "Pos.Modules.Audit.Contracts", "Pos.Modules.Audit.Infrastructure",
            "Pos.Modules.Identity.Api", "Pos.Modules.Identity.Application", "Pos.Modules.Identity.Contracts",
            "Pos.Modules.Identity.Domain", "Pos.Modules.Identity.Infrastructure",
            "Pos.Modules.Organization.Api", "Pos.Modules.Organization.Application", "Pos.Modules.Organization.Contracts",
            "Pos.Modules.Organization.Domain", "Pos.Modules.Organization.Infrastructure",
            "Pos.Modules.Reference.Api", "Pos.Modules.Reference.Application", "Pos.Modules.Reference.Contracts",
            "Pos.Modules.Reference.Infrastructure",
        ];

        ProductionAssemblies.All.Select(a => a.GetName().Name).ShouldBe(
            [
                ArchitectureRules.ApiAbstractions,
                ArchitectureRules.ApplicationAbstractions,
                ArchitectureRules.Infrastructure,
                ArchitectureRules.ServerHost,
                ArchitectureRules.SharedKernel,
                "Pos.Server.Migrations",
                .. modules,
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void R7_Contracts_de_un_modulo_no_dependen_de_sus_capas_internas()
    {
        var violations = ProductionAssemblies.Graph
            .Where(a => ArchitectureRules.ParseModule(a.Name) is { Layer: "Contracts" })
            .SelectMany(a => a.References
                .Where(r => ArchitectureRules.ParseModule(r) is { Layer: not "Contracts" })
                .Select(r => $"{a.Name} no puede depender de {r}: los contratos no exponen el interior de ningún módulo."));

        violations.ShouldBeEmpty();
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
