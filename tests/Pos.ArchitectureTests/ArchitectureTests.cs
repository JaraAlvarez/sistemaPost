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
            "Pos.Modules.Cash.Api", "Pos.Modules.Cash.Application", "Pos.Modules.Cash.Contracts", "Pos.Modules.Cash.Domain",
            "Pos.Modules.Cash.Infrastructure",
            "Pos.Modules.Catalog.Api", "Pos.Modules.Catalog.Application", "Pos.Modules.Catalog.Contracts", "Pos.Modules.Catalog.Domain",
            "Pos.Modules.Catalog.Infrastructure",
            "Pos.Modules.Expenses.Api", "Pos.Modules.Expenses.Application", "Pos.Modules.Expenses.Contracts", "Pos.Modules.Expenses.Domain",
            "Pos.Modules.Expenses.Infrastructure",
            "Pos.Modules.Identity.Api", "Pos.Modules.Identity.Application", "Pos.Modules.Identity.Contracts",
            "Pos.Modules.Identity.Domain", "Pos.Modules.Identity.Infrastructure",
            "Pos.Modules.Inventory.Api", "Pos.Modules.Inventory.Application", "Pos.Modules.Inventory.Contracts", "Pos.Modules.Inventory.Domain",
            "Pos.Modules.Inventory.Infrastructure",
            "Pos.Modules.Organization.Api", "Pos.Modules.Organization.Application", "Pos.Modules.Organization.Contracts",
            "Pos.Modules.Organization.Domain", "Pos.Modules.Organization.Infrastructure",
            "Pos.Modules.Parties.Api", "Pos.Modules.Parties.Application", "Pos.Modules.Parties.Contracts", "Pos.Modules.Parties.Domain",
            "Pos.Modules.Parties.Infrastructure",
            "Pos.Modules.Purchasing.Api", "Pos.Modules.Purchasing.Application", "Pos.Modules.Purchasing.Contracts", "Pos.Modules.Purchasing.Domain",
            "Pos.Modules.Purchasing.Infrastructure",
            "Pos.Modules.Reference.Api", "Pos.Modules.Reference.Application", "Pos.Modules.Reference.Contracts",
            "Pos.Modules.Reference.Infrastructure",
            "Pos.Modules.Reporting.Api", "Pos.Modules.Reporting.Application", "Pos.Modules.Reporting.Contracts",
            "Pos.Modules.Reporting.Infrastructure",
            "Pos.Modules.Backup.Api", "Pos.Modules.Backup.Application", "Pos.Modules.Backup.Contracts",
            "Pos.Modules.Backup.Infrastructure",
            "Pos.Modules.Licensing.Api", "Pos.Modules.Licensing.Application", "Pos.Modules.Licensing.Contracts",
            "Pos.Modules.Licensing.Infrastructure", "Pos.Licensing.Contracts", "Pos.Updates.Contracts",
            "Pos.Modules.Customers.Api", "Pos.Modules.Customers.Application", "Pos.Modules.Customers.Contracts", "Pos.Modules.Customers.Domain",
            "Pos.Modules.Customers.Infrastructure",
            "Pos.Modules.Sales.Api", "Pos.Modules.Sales.Application", "Pos.Modules.Sales.Contracts", "Pos.Modules.Sales.Domain",
            "Pos.Modules.Sales.Infrastructure",
            "Pos.Modules.Promotions.Api", "Pos.Modules.Promotions.Application", "Pos.Modules.Promotions.Contracts", "Pos.Modules.Promotions.Domain",
            "Pos.Modules.Promotions.Infrastructure",
            "Pos.Modules.Billing.Api", "Pos.Modules.Billing.Application", "Pos.Modules.Billing.Contracts", "Pos.Modules.Billing.Domain",
            "Pos.Modules.Billing.Infrastructure",
            "Pos.Printing",
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

    /// <summary>
    /// R8 (Fase 4, D4-01): solo el módulo Inventory escribe el kardex y los saldos; los demás módulos usan IInventoryPosting.
    /// Se revisa el código fuente porque la escritura es SQL directo.
    /// </summary>
    [Fact]
    public void R8_Solo_el_modulo_Inventory_escribe_el_kardex_y_los_saldos()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Pos.slnx")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull("No se encontró la raíz del repositorio.");
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+inventory\.(stock_movements|stock_balances)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var inventory = Path.Combine(root.FullName, "src", "Modules", "Inventory") + Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.StartsWith(inventory, StringComparison.OrdinalIgnoreCase))
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root.FullName, f))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    /// <summary>
    /// RN-AUD-02 (Fase 10, D10-01): toda entidad <c>[Audited]</c> y todo código de acción escrito con <c>new AuditEntry("modulo", "CODIGO"…)</c>
    /// está en el catálogo de acciones (nombre en español y severidad). Se revisa el código fuente para los códigos literales.
    /// </summary>
    [Fact]
    public void R9_Toda_accion_de_auditoria_esta_en_el_catalogo()
    {
        var contracts = ProductionAssemblies.All.Single(a => a.GetName().Name == "Pos.Modules.Audit.Contracts");
        var catalog = contracts.GetType("Pos.Modules.Audit.Contracts.AuditActions", throwOnError: true)!;
        var codes = ((System.Collections.IEnumerable)catalog.GetProperty("All")!.GetValue(null)!)
            .Cast<object>()
            .Select(a => (string)a.GetType().GetProperty("Code")!.GetValue(a)!)
            .ToHashSet(StringComparer.Ordinal);
        var entities = ((System.Collections.IDictionary)catalog.GetProperty("Entities")!.GetValue(null)!).Keys.Cast<string>().ToHashSet(StringComparer.Ordinal);

        var audited = ProductionAssemblies.All
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetCustomAttributes(inherit: false).Any(x => x.GetType().Name == "AuditedAttribute"))
            .Select(t => t.Name)
            .Where(name => !entities.Contains(name))
            .Select(name => $"La entidad auditada {name} no está en AuditActions.Entities.");

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Pos.slnx")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull("No se encontró la raíz del repositorio.");
        // Códigos literales dentro de los argumentos de new AuditEntry(...) (incluye switch y ternarios) y de los ayudantes AuditAsync(usuario, "CODIGO"…).
        var entry = new System.Text.RegularExpressions.Regex(@"new AuditEntry\(");
        var code = new System.Text.RegularExpressions.Regex(@"""([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)""");
        var helper = new System.Text.RegularExpressions.Regex(@"AuditAsync\(\s*\w+\s*,\s*""([A-Z][A-Z0-9_]+)""");
        var literals = Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Cloud{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f =>
            {
                var text = File.ReadAllText(f);
                var inEntries = entry.Matches(text).SelectMany(m => code.Matches(text.Substring(m.Index, Math.Min(400, text.Length - m.Index))).Select(c => c.Groups[1].Value));
                var inHelpers = helper.Matches(text).Select(m => m.Groups[1].Value);
                return inEntries.Concat(inHelpers).Select(c => (File: Path.GetRelativePath(root.FullName, f), Code: c));
            })
            .Where(x => !codes.Contains(x.Code))
            .Distinct()
            .Select(x => $"{x.File}: la acción {x.Code} no está en el catálogo AuditActions.");

        audited.Concat(literals).ToList().ShouldBeEmpty();
    }

    /// <summary>
    /// R10 (Fase 12-B, D12B-02): lista de comandos que siguen disponibles con la licencia RESTRINGIDA. Cambiarla exige revisar esta
    /// prueba: un comando nuevo nace bloqueado en RESTRICTED (nunca en los demás estados) y solo se agrega aquí si es vender, cerrar la
    /// jornada abierta, iniciar sesión, respaldar, exportar, verificar o atender la licencia.
    /// </summary>
    [Fact]
    public void R10_Comandos_permitidos_con_la_licencia_restringida()
    {
        var allowed = ProductionAssemblies.All
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetInterfaces().Any(i => i.Name == "IAllowedWhenRestricted"))
            .Select(t => $"{t.Namespace!.Split('.')[2]}.{t.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

        allowed.ShouldBe(
        [
            "Audit.AcknowledgeIncidentCommand", "Audit.IssueIntegrityCertificateCommand", "Audit.VerifyAuditCommand",
            "Backup.ConfirmRecoveryCodeCommand", "Backup.DownloadBackupCommand", "Backup.GenerateRecoveryCodeCommand", "Backup.RunBackupNowCommand",
            "Backup.SaveDestinationCommand",
            "Billing.RetryFiscalDocumentCommand",
            "Cash.CancelClosingCommand", "Cash.CloseSessionCommand", "Cash.RegisterMovementCommand", "Cash.ReviewSessionCommand", "Cash.StartClosingCommand",
            "Customers.AnonymizeCustomerCommand", "Customers.CloseDataRequestCommand", "Customers.CompleteCustomerCommand", "Customers.CreateCustomerCommand",
            "Customers.CreateDataRequestCommand", "Customers.ExportCustomerDataCommand", "Customers.QuickCreateCustomerCommand", "Customers.RecordConsentsCommand",
            "Expenses.CreateExpenseCommand", "Expenses.VoidExpenseCommand",
            "Identity.ChangePasswordCommand", "Identity.ChangePinCommand", "Identity.CreateAuthorizationCommand", "Identity.CreateOwnerCommand",
            "Identity.LoginCommand", "Identity.LogoutCommand", "Identity.PosLoginCommand", "Identity.RevokeSessionCommand", "Identity.UnlockUserCommand",
            "Inventory.RebuildStockCommand", "Inventory.VerifyStockCommand",
            "Licensing.ActivateLicenseCommand", "Licensing.CheckLicenseNowCommand", "Licensing.DeactivateLicenseCommand", "Licensing.RefreshLicenseCommand",
            "Organization.SetupCommand",
            "Reporting.ExportReportCommand",
            "Sales.AddLineCommand", "Sales.ApplyDiscountCommand", "Sales.CancelSaleCommand", "Sales.ChangeQuantityCommand", "Sales.CompleteSaleCommand",
            "Sales.HoldSaleCommand", "Sales.OverridePriceCommand", "Sales.RemoveDiscountCommand", "Sales.ReprintSaleCommand", "Sales.ResumeSaleCommand",
            "Sales.SetCustomerCommand", "Sales.StartExchangeCommand", "Sales.StartSaleCommand", "Sales.VoidLineCommand", "Sales.VoidSaleCommand",
            "Sales.WarrantyRefundCommand",
        ],
        ignoreOrder: true);
    }
}
