using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Organization.Application;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Infrastructure;

/// <summary>
/// Mapeo de las tablas del esquema org. Las relaciones se declaran para que EF ordene los INSERT (empresa → sucursal
/// → bodegas → cajas). La bodega por defecto de la sucursal NO se mapea como relación: en la BD es una FK diferible
/// (se crean juntas) y declararla crearía un ciclo para EF.
/// </summary>
internal sealed class OrganizationModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Company>(b =>
        {
            b.ToTable("companies", "org");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PersonType).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasControlColumns();
            b.HasXminConcurrency();
            b.OwnsMany(x => x.FiscalResponsibilities, r =>
            {
                r.ToTable("company_fiscal_responsibilities", "org");
                r.WithOwner().HasForeignKey("CompanyId");
                r.Property(x => x.Code).HasColumnName("responsibility_code");
                r.HasKey("CompanyId", nameof(FiscalResponsibility.Code));
            });
            b.Navigation(x => x.FiscalResponsibilities).HasField("_fiscalResponsibilities");
        });

        modelBuilder.Entity<Branch>(b =>
        {
            b.ToTable("branches", "org");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Warehouse>(b =>
        {
            b.ToTable("warehouses", "org");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.BranchId });
            b.HasOne<Branch>().WithMany().HasForeignKey(x => new { x.BranchId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PosTerminal>(b =>
        {
            b.ToTable("pos_terminals", "org");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasOne<Branch>().WithMany().HasForeignKey(x => new { x.BranchId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.WarehouseId, x.BranchId })
                .HasPrincipalKey(x => new { x.Id, x.BranchId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Device>(b =>
        {
            b.ToTable("devices", "org");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.MachineFingerprintHash).HasColumnType("char(64)");
            b.Property(x => x.CredentialHash).HasColumnType("char(64)");
            b.Property(x => x.CertificatePin).HasColumnType("char(64)");
            b.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PairingCodeRecord>(b =>
        {
            b.ToTable("device_pairing_codes", "org");
            b.HasKey(x => x.Id);
            b.Property(x => x.CodeHash).HasColumnType("char(64)");
        });

        modelBuilder.Entity<Node>(b =>
        {
            b.ToTable("nodes", "org");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasOne<Branch>().WithMany().HasForeignKey(x => new { x.BranchId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
        });
    }
}

internal sealed class OrganizationStore(PosDbContext context) : IOrganizationStore
{
    public void Add(Company company) => context.Add(company);

    public void Add(Branch branch) => context.Add(branch);

    public void Add(Warehouse warehouse) => context.Add(warehouse);

    public void Add(PosTerminal terminal) => context.Add(terminal);

    public void Add(Node node) => context.Add(node);

    public Task<Company?> GetCompanyAsync(Guid companyId, CancellationToken cancellationToken) =>
        context.Set<Company>().SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);

    public Task<bool> CompanyExistsAsync(string identificationType, string identificationNumber, CancellationToken cancellationToken) =>
        context.Set<Company>().AnyAsync(
            c => c.IdentificationType == identificationType && c.IdentificationNumber == identificationNumber, cancellationToken);

    public Task<Branch?> GetBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        context.Set<Branch>().SingleOrDefaultAsync(b => b.Id == branchId, cancellationToken);

    public Task<Warehouse?> GetWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken) =>
        context.Set<Warehouse>().SingleOrDefaultAsync(w => w.Id == warehouseId, cancellationToken);

    public Task<PosTerminal?> GetTerminalAsync(Guid terminalId, CancellationToken cancellationToken) =>
        context.Set<PosTerminal>().SingleOrDefaultAsync(t => t.Id == terminalId, cancellationToken);

    public Task<PosTerminal?> GetTerminalByDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        context.Set<PosTerminal>().SingleOrDefaultAsync(t => t.DeviceId == deviceId, cancellationToken);

    public Task<bool> BranchCodeExistsAsync(Guid companyId, string code, CancellationToken cancellationToken) =>
        context.Set<Branch>().AnyAsync(b => b.CompanyId == companyId && b.Code == code, cancellationToken);

    public Task<bool> WarehouseCodeExistsAsync(Guid branchId, string code, CancellationToken cancellationToken) =>
        context.Set<Warehouse>().AnyAsync(w => w.BranchId == branchId && w.Code == code, cancellationToken);

    public Task<bool> TerminalCodeExistsAsync(Guid branchId, string code, CancellationToken cancellationToken) =>
        context.Set<PosTerminal>().AnyAsync(t => t.BranchId == branchId && t.Code == code, cancellationToken);

    public Task<bool> BranchHasInTransitWarehouseAsync(Guid branchId, CancellationToken cancellationToken) =>
        context.Set<Warehouse>().AnyAsync(w => w.BranchId == branchId && w.Kind == WarehouseKind.InTransit, cancellationToken);

    public Task<int> CountActiveBranchesAsync(Guid companyId, CancellationToken cancellationToken) =>
        context.Set<Branch>().CountAsync(b => b.CompanyId == companyId && b.Status == RecordStatus.Active, cancellationToken);

    public Task<int> CountActiveTerminalsAsync(Guid? branchId, CancellationToken cancellationToken) =>
        context.Set<PosTerminal>().CountAsync(
            t => (branchId == null || t.BranchId == branchId) && t.Status == TerminalStatus.Active, cancellationToken);

    public Task<int> CountTerminalsAsync(CancellationToken cancellationToken) => context.Set<PosTerminal>().CountAsync(cancellationToken);

    public Task<bool> WarehouseAssignedToActiveTerminalAsync(Guid warehouseId, CancellationToken cancellationToken) =>
        context.Set<PosTerminal>().AnyAsync(t => t.WarehouseId == warehouseId && t.Status == TerminalStatus.Active, cancellationToken);

    public async Task<IReadOnlyList<BranchDto>> ListBranchesAsync(Guid? homeBranchId, CancellationToken cancellationToken)
    {
        var branches = await context.Set<Branch>().AsNoTracking().OrderBy(b => b.Code).ToListAsync(cancellationToken);
        return branches
            .Select(b => new BranchDto(b.Id, b.Code, b.Name, b.MunicipalityCode, b.Address, b.Phone, b.DefaultWarehouseId,
                b.Status.ToString(), b.Id == homeBranchId))
            .ToList();
    }

    public async Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var warehouses = await context.Set<Warehouse>().AsNoTracking()
            .Where(w => w.BranchId == branchId).OrderBy(w => w.Code).ToListAsync(cancellationToken);
        return warehouses
            .Select(w => new WarehouseDto(w.Id, w.BranchId, w.Code, w.Name, w.Kind.ToString(), w.AllowsSales, w.Status.ToString()))
            .ToList();
    }

    public async Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(Guid? branchId, CancellationToken cancellationToken)
    {
        var terminals = await context.Set<PosTerminal>().AsNoTracking()
            .Where(t => branchId == null || t.BranchId == branchId).OrderBy(t => t.Code).ToListAsync(cancellationToken);
        return terminals
            .Select(t => new TerminalDto(t.Id, t.BranchId, t.Code, t.Name, t.WarehouseId, t.DeviceId, t.Status.ToString()))
            .ToList();
    }

    public async Task<bool> MunicipalityExistsAsync(string code, CancellationToken cancellationToken) =>
        await context.Database
            .SqlQuery<bool>($"""SELECT EXISTS (SELECT 1 FROM ref.municipalities WHERE code = {code}) AS "Value" """)
            .SingleAsync(cancellationToken);
}

/// <summary>Restricciones de la BD → errores de negocio legibles (ninguna violación llega como 500).</summary>
internal sealed class OrganizationConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_companies__identification"] = OrganizationErrors.CompanyAlreadyExists,
        ["ux_branches__company_code"] = OrganizationErrors.BranchCodeDuplicated,
        ["ux_warehouses__branch_code"] = OrganizationErrors.WarehouseCodeDuplicated,
        ["ux_warehouses__branch_in_transit"] = OrganizationErrors.InTransitWarehouseDuplicated,
        ["ck_warehouses__sales"] = OrganizationErrors.WarehouseKindCannotSell,
        ["ux_pos_terminals__branch_code"] = OrganizationErrors.TerminalCodeDuplicated,
        ["ck_pos_terminals__warehouse_allows_sales"] = OrganizationErrors.TerminalWarehouseMustSell,
        ["fk_pos_terminals__warehouse"] = OrganizationErrors.TerminalWarehouseMustSell,
        ["fk_companies__municipality"] = Error.Validation("ORGANIZATION.UNKNOWN_MUNICIPALITY", "El municipio no existe en DIVIPOLA."),
        ["fk_branches__municipality"] = Error.Validation("ORGANIZATION.UNKNOWN_MUNICIPALITY", "El municipio no existe en DIVIPOLA."),
        ["fk_companies__identification_type"] = Error.Validation("ORGANIZATION.UNKNOWN_IDENTIFICATION_TYPE", "El tipo de identificación no existe."),
        ["fk_companies__tax_regime"] = Error.Validation("ORGANIZATION.UNKNOWN_TAX_REGIME", "El régimen tributario no existe."),
        ["fk_company_fiscal_responsibilities__responsibility"] =
            Error.Validation("ORGANIZATION.UNKNOWN_FISCAL_RESPONSIBILITY", "Una de las responsabilidades fiscales no existe."),
        ["ux_document_series__company_type_prefix"] =
            Error.Conflict("ORGANIZATION.SERIES_PREFIX_DUPLICATED", "Ya existe una serie de numeración con ese prefijo."),
    };
}
