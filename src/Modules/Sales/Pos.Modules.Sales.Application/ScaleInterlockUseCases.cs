using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Sales.Application;

/// <summary>
/// Retorno a cero de la báscula de esta caja según el servidor (BN1): la caja lo consulta después de eliminar una pesada o de reiniciar
/// la báscula y solo entonces libera también el agente, para que los dos queden de acuerdo.
/// </summary>
public sealed record GetScaleInterlockQuery : IQuery<ScaleInterlockDto>;

internal sealed class GetScaleInterlockHandler(TerminalResolver terminals, ISalesStore store) : IQueryHandler<GetScaleInterlockQuery, ScaleInterlockDto>
{
    public async Task<Result<ScaleInterlockDto>> Handle(GetScaleInterlockQuery request, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: false, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var interlock = await store.GetScaleInterlockAsync(scope.Value.PosTerminalId, cancellationToken);
        return new ScaleInterlockDto(interlock?.AwaitingEmpty ?? false);
    }
}

/// <summary>
/// "Reiniciar báscula" (BB5): libera el retorno a cero de esta caja cuando la báscula no puede quedar vacía (una bandeja fija, la
/// báscula se quedó marcando un peso). Exige el permiso <c>sales.scale.reset</c> o la autorización de un supervisor y queda auditado.
/// </summary>
public sealed record ResetScaleInterlockCommand : ICommand<ScaleInterlockDto>, IAllowedWhenRestricted;

internal sealed class ResetScaleInterlockHandler(
    TerminalResolver terminals, ISalesStore store, IClock clock, IAuditWriter audit, IAuthorizationScope authorization,
    Pos.Modules.Organization.Contracts.ITerminalDirectory directory)
    : ICommandHandler<ResetScaleInterlockCommand, ScaleInterlockDto>
{
    public async Task<Result<ScaleInterlockDto>> Handle(ResetScaleInterlockCommand request, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: false, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var interlock = await store.GetScaleInterlockAsync(scope.Value.PosTerminalId, cancellationToken);
        var released = interlock?.Reset(clock.UtcNow) ?? false;
        var code = (await directory.GetAsync(scope.Value.PosTerminalId, cancellationToken))?.Code ?? scope.Value.PosTerminalId.ToString();
        await audit.WriteAsync(
            new AuditEntry("sales", "SALE_SCALE_RESET", nameof(ScaleInterlock), scope.Value.PosTerminalId, $"Báscula de la caja {code}",
                released
                    ? "Báscula reiniciada: se liberó la espera de retirar el producto de la pesada anterior."
                    : "Báscula reiniciada (no había una pesada esperando que se retirara el producto).",
                AuthorizedBy: authorization.Current?.AuthorizedBy, Severity: AuditSeverity.Warning),
            cancellationToken);
        return new ScaleInterlockDto(false);
    }
}
