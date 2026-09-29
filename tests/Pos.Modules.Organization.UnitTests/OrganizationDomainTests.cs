using Pos.Modules.Identity.Domain;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Domain;

namespace Pos.Modules.Organization.UnitTests;

public class CompanyTests
{
    internal static CompanyData Data(string nit = "900123456", string? dv = "8", string type = "NIT", params string[] responsibilities) => new(
        "Supermercado La Economía SAS", "La Economía", PersonType.Legal, type, nit, dv, "48",
        responsibilities.Length == 0 ? ["O-15"] : responsibilities, "CO", "05001", "  Calle 50  ", null, null, "COP", "America/Bogota");

    [Fact]
    public void Se_crea_con_NIT_valido_y_etiqueta_de_auditoria()
    {
        var company = Company.Create(Guid.CreateVersion7(), Data()).Value;

        company.Address.ShouldBe("Calle 50");
        company.AuditLabel.ShouldBe("La Economía (NIT 900123456-8)");
        company.FiscalResponsibilities.Select(r => r.Code).ShouldBe(["O-15"]);
        company.Status.ShouldBe(RecordStatus.Active);
    }

    [Fact]
    public void Rechaza_un_NIT_con_DV_incorrecto() =>
        Company.Create(Guid.CreateVersion7(), Data(dv: "1")).Error.ShouldBe(OrganizationErrors.InvalidNitCheckDigit);

    [Fact]
    public void Otros_tipos_de_identificacion_no_llevan_DV()
    {
        var company = Company.Create(Guid.CreateVersion7(), Data(nit: "1020304050", dv: null, type: "CC")).Value;
        company.AuditLabel.ShouldBe("La Economía (CC 1020304050)");
    }

    [Fact]
    public void La_identificacion_es_inmutable_y_las_responsabilidades_se_sincronizan()
    {
        var company = Company.Create(Guid.CreateVersion7(), Data(responsibilities: ["O-15", "O-15", "O-13"])).Value;
        company.FiscalResponsibilities.Count.ShouldBe(2);

        company.Update(Data(nit: "800197268", dv: "4")).Error.Code.ShouldBe("ORGANIZATION.IDENTIFICATION_IMMUTABLE");

        company.Update(Data(responsibilities: ["R-99-PN"])).IsSuccess.ShouldBeTrue();
        company.FiscalResponsibilities.Select(r => r.Code).ShouldBe(["R-99-PN"]);
    }
}

public class BranchWarehouseTerminalTests
{
    private static readonly Guid Company = Guid.CreateVersion7();

    private static Branch NewBranch(string code = "S01") =>
        Branch.Create(Guid.CreateVersion7(), Company, code, " Centro ", "05001", " Calle 1 ", null).Value;

    private static Warehouse NewWarehouse(Branch branch, WarehouseKind kind = WarehouseKind.SalesFloor, bool sells = true, string code = "PISO") =>
        Warehouse.Create(Guid.CreateVersion7(), Company, branch.Id, code, " Piso ", kind, sells).Value;

    [Theory]
    [InlineData("S01", true)]
    [InlineData("NORTE", true)]
    [InlineData("s01", false)]
    [InlineData("S", false)]
    [InlineData("SUCURSAL1", false)]
    public void Codigo_de_sucursal(string code, bool valid)
    {
        Branch.IsValidCode(code).ShouldBe(valid);
        Branch.Create(Guid.CreateVersion7(), Company, code, "x", "05001", "x", null).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    public void La_sucursal_se_actualiza_se_inactiva_y_se_reactiva()
    {
        var branch = NewBranch();
        branch.Name.ShouldBe("Centro");
        branch.AuditLabel.ShouldBe("Sucursal S01 · Centro");

        branch.Update("Norte", "11001", "Cra 7", "300");
        branch.Deactivate();
        branch.Status.ShouldBe(RecordStatus.Inactive);
        branch.Activate();
        branch.Status.ShouldBe(RecordStatus.Active);
        branch.MunicipalityCode.ShouldBe("11001");
    }

    [Fact]
    public void La_bodega_por_defecto_debe_ser_de_la_sucursal_y_vender()
    {
        var branch = NewBranch();
        var damaged = NewWarehouse(branch, WarehouseKind.Damaged, sells: false, code: "AVERIAS");
        var other = NewWarehouse(NewBranch("S02"));

        Should.Throw<DomainException>(() => branch.SetDefaultWarehouse(damaged));
        Should.Throw<DomainException>(() => branch.SetDefaultWarehouse(other));

        var floor = NewWarehouse(branch);
        branch.SetDefaultWarehouse(floor);
        branch.DefaultWarehouseId.ShouldBe(floor.Id);
    }

    [Theory]
    [InlineData(WarehouseKind.Damaged)]
    [InlineData(WarehouseKind.InTransit)]
    public void Averias_y_transito_nunca_venden(WarehouseKind kind)
    {
        var branch = NewBranch();
        Warehouse.Create(Guid.CreateVersion7(), Company, branch.Id, "X1", "x", kind, allowsSales: true)
            .Error.ShouldBe(OrganizationErrors.WarehouseKindCannotSell);

        var warehouse = NewWarehouse(branch, kind, sells: false, code: "X1");
        warehouse.IsSystemWarehouse.ShouldBeTrue();
        warehouse.Update("x", allowsSales: true).Error.ShouldBe(OrganizationErrors.WarehouseKindCannotSell);
    }

    [Fact]
    public void Bodega_codigo_invalido_actualizacion_e_inactivacion()
    {
        var branch = NewBranch();
        Warehouse.Create(Guid.CreateVersion7(), Company, branch.Id, "piso", "x", WarehouseKind.Storage, false).IsFailure.ShouldBeTrue();

        var storage = NewWarehouse(branch, WarehouseKind.Storage, sells: false, code: "DEP-1");
        storage.Update(" Depósito ", allowsSales: true).IsSuccess.ShouldBeTrue();
        storage.Name.ShouldBe("Depósito");
        storage.AuditLabel.ShouldBe("Bodega DEP-1 · Depósito");
        storage.Deactivate();
        storage.Status.ShouldBe(RecordStatus.Inactive);
        storage.Activate();
        storage.Status.ShouldBe(RecordStatus.Active);
    }

    [Fact]
    public void La_caja_solo_despacha_de_una_bodega_activa_de_su_sucursal_que_venda()
    {
        var branch = NewBranch();
        var floor = NewWarehouse(branch);
        var terminal = PosTerminal.Create(Guid.CreateVersion7(), Company, branch.Id, "C01", " Caja 1 ", floor).Value;
        terminal.Name.ShouldBe("Caja 1");
        terminal.AuditLabel.ShouldBe("Caja C01 · Caja 1");

        terminal.AssignWarehouse(NewWarehouse(NewBranch("S02"))).Error.ShouldBe(OrganizationErrors.TerminalWarehouseMustSell);
        terminal.AssignWarehouse(NewWarehouse(branch, WarehouseKind.Storage, sells: false, code: "DEP")).IsFailure.ShouldBeTrue();

        var inactive = NewWarehouse(branch, code: "PISO2");
        inactive.Deactivate();
        terminal.AssignWarehouse(inactive).IsFailure.ShouldBeTrue();

        PosTerminal.Create(Guid.CreateVersion7(), Company, branch.Id, "c-1", "x", floor).IsFailure.ShouldBeTrue();
        PosTerminal.Create(Guid.CreateVersion7(), Company, branch.Id, "C02", "x", NewWarehouse(branch, WarehouseKind.Damaged, false, "AV"))
            .Error.ShouldBe(OrganizationErrors.TerminalWarehouseMustSell);
    }

    [Fact]
    public void Estados_de_la_caja()
    {
        var branch = NewBranch();
        var terminal = PosTerminal.Create(Guid.CreateVersion7(), Company, branch.Id, "C01", "Caja", NewWarehouse(branch)).Value;

        terminal.Block();
        terminal.Status.ShouldBe(TerminalStatus.Blocked);
        terminal.Deactivate();
        terminal.Status.ShouldBe(TerminalStatus.Inactive);
        terminal.Activate();
        terminal.Status.ShouldBe(TerminalStatus.Active);
        terminal.Rename(" Rápida ");
        terminal.Name.ShouldBe("Rápida");
    }

    [Fact]
    public void Un_nodo_local_siempre_pertenece_a_una_sucursal()
    {
        var branch = NewBranch();
        var node = Node.RegisterLocal(Guid.CreateVersion7(), Company, branch.Id, NodeKind.StoreServer, "S01 · Centro", DateTimeOffset.UtcNow);
        node.Epoch.ShouldBe(1);
        node.BranchId.ShouldBe(branch.Id);
        node.AuditLabel.ShouldBe("Nodo S01 · Centro (StoreServer)");

        Should.Throw<DomainException>(() =>
            Node.RegisterLocal(Guid.CreateVersion7(), Company, branch.Id, NodeKind.Cloud, "Nube", DateTimeOffset.UtcNow));
    }
}

public class IdentityDomainTests
{
    [Fact]
    public void El_usuario_system_nace_deshabilitado_y_sin_credenciales()
    {
        var user = User.CreateSystem(Guid.CreateVersion7(), Guid.CreateVersion7());

        user.Username.ShouldBe(User.SystemUsername);
        user.Kind.ShouldBe(UserKind.System);
        user.Status.ShouldBe(UserStatus.Disabled);
        user.MustChangePassword.ShouldBeFalse();
        user.Email.ShouldBeNull();
        user.AuditLabel.ShouldBe("Usuario system · Sistema");
    }

    [Fact]
    public void Un_rol_de_sistema_no_repite_permisos()
    {
        var definition = new SystemRoleDefinition("OWNER", "Propietario", "Todo", ["a.b.c", "a.b.c", "x.y.z"]);
        var role = Role.CreateSystem(Guid.CreateVersion7(), Guid.CreateVersion7(), definition);

        role.IsSystem.ShouldBeTrue();
        role.Permissions.Select(p => p.PermissionCode).ShouldBe(["a.b.c", "x.y.z"]);
        role.AuditLabel.ShouldBe("Rol OWNER · Propietario");
        role.Description.ShouldBe("Todo");
    }
}
