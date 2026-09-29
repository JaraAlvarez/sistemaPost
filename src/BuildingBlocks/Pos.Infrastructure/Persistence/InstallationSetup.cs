using Microsoft.EntityFrameworkCore;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Settings;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.Persistence;

internal sealed class InstallationSetup(
    PosDbContext context, IInstallationContext installation, IClock clock, CommitCallbacks callbacks) : IInstallationSetup
{
    public async Task<bool> TryBeginAsync(CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("El asistente inicial debe ejecutarse dentro de una transacción.");
        }

        var completed = await context.Database
            .SqlQuery<DateTimeOffset?>($"""SELECT setup_completed_at AS "Value" FROM system.installation FOR UPDATE""")
            .SingleAsync(cancellationToken);
        return completed is null;
    }

    public async Task CompleteAsync(Guid companyId, Guid branchId, SetupMode mode, CancellationToken cancellationToken = default)
    {
        var record = await context.Set<InstallationRecord>().SingleAsync(cancellationToken);
        record.HomeCompanyId = companyId;
        record.HomeBranchId = branchId;
        record.SetupMode = mode == SetupMode.JoinCompany ? "JOIN_COMPANY" : "NEW_COMPANY";
        record.SetupCompletedAt = clock.UtcNow;

        // El contexto de instalación (empresa local, usuario system) se recarga solo si el asistente se confirma.
        callbacks.OnCommitted(() => installation.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult());
    }
}

/// <summary>
/// Verificación de permisos PERMISIVA de la Fase 2 (todavía no hay login). La Fase 3 la reemplaza por la real
/// (roles + excepciones + licencia) sin tocar los endpoints. Riesgo aceptado: el servidor solo escucha en localhost.
/// </summary>
internal sealed class PermissiveFase2PermissionChecker : IPermissionChecker
{
    public Task<bool> HasPermissionAsync(string permissionCode, Guid? branchId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
