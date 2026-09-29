using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Numbering;

namespace Pos.Modules.Organization.Application;

/// <summary>
/// Series de numeración de la sucursal local y sus cajas para TODOS los tipos de documento vigentes: una instalación
/// creada antes de que existiera un tipo (p. ej. órdenes de compra de la Fase 5) recibe su serie al arrancar.
/// Idempotente: el aprovisionador reutiliza la serie existente del mismo prefijo.
/// </summary>
public sealed class DocumentSeriesInitializer(IOrganizationStore store, IDocumentSeriesProvisioner series, IInstallationContext installation)
    : ICompanyInitializer
{
    public int Order => 10;

    public async Task InitializeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        if (installation.BranchId is not { } branchId
            || (await store.ListBranchesAsync(branchId, cancellationToken)).FirstOrDefault(b => b.Id == branchId) is not { } branch)
        {
            return;
        }

        await series.CreateBranchSeriesAsync(companyId, branch.Id, branch.Code, cancellationToken);
        foreach (var terminal in (await store.ListTerminalsAsync(branchId, cancellationToken)).Where(t => t.Status == "Active"))
        {
            await series.CreateTerminalSeriesAsync(companyId, branch.Id, branch.Code, terminal.Id, terminal.Code, cancellationToken);
        }
    }
}
