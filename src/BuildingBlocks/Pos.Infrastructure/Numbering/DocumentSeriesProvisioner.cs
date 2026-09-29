using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Abstractions.Numbering;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Identifiers;

namespace Pos.Infrastructure.Numbering;

internal sealed class DocumentSeriesProvisioner(PosDbContext context, IIdGenerator ids) : IDocumentSeriesProvisioner
{
    public const short DefaultPadding = 6;

    public Task CreateBranchSeriesAsync(Guid companyId, Guid branchId, string branchCode, CancellationToken cancellationToken = default) =>
        CreateAsync("BRANCH", companyId, branchId, null, branchCode, cancellationToken);

    public Task CreateTerminalSeriesAsync(
        Guid companyId, Guid branchId, string branchCode, Guid posTerminalId, string terminalCode, CancellationToken cancellationToken = default) =>
        CreateAsync("TERMINAL", companyId, branchId, posTerminalId, branchCode + terminalCode, cancellationToken);

    public async Task DeactivateTerminalSeriesAsync(Guid posTerminalId, CancellationToken cancellationToken = default)
    {
        var series = await context.Set<DocumentSeriesRecord>()
            .Where(s => s.PosTerminalId == posTerminalId && s.Status == "ACTIVE")
            .ToListAsync(cancellationToken);
        foreach (var s in series)
        {
            s.Status = "INACTIVE";
        }
    }

    private async Task CreateAsync(
        string scope, Guid companyId, Guid branchId, Guid? posTerminalId, string prefix, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var types = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT code FROM system.document_types WHERE series_scope = @scope ORDER BY code",
            new { scope },
            context.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken: cancellationToken));

        foreach (var type in types)
        {
            // Si el prefijo ya existió (caja o sucursal eliminada y recreada con el mismo código), se REUTILIZA su serie
            // y la numeración continúa: un número interno nunca se repite en la empresa.
            var existing = await context.Set<DocumentSeriesRecord>()
                .SingleOrDefaultAsync(s => s.CompanyId == companyId && s.DocumentType == type && s.Prefix == prefix, cancellationToken);
            if (existing is not null)
            {
                existing.BranchId = branchId;
                existing.PosTerminalId = posTerminalId;
                existing.Status = "ACTIVE";
                continue;
            }

            context.Add(new DocumentSeriesRecord
            {
                Id = ids.NewId(),
                CompanyId = companyId,
                BranchId = branchId,
                PosTerminalId = posTerminalId,
                DocumentType = type,
                Prefix = prefix,
                NextNumber = 1,
                Padding = DefaultPadding,
                Status = "ACTIVE",
            });
        }
    }
}
