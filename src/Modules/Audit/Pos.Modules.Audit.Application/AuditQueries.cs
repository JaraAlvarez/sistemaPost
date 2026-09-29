using FluentValidation;
using Pos.Application.Abstractions.Messaging;
using Pos.Modules.Audit.Contracts;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Audit.Application;

/// <summary>Filtros de consulta de la bitácora.</summary>
public sealed record AuditLogFilter(
    string? EntityType,
    Guid? EntityId,
    Guid? UserId,
    string? Module,
    string? Action,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize);

public interface IAuditReadModel
{
    Task<AuditLogPage> SearchAsync(AuditLogFilter filter, CancellationToken cancellationToken);

    Task<AuditVerificationDto> VerifyAsync(CancellationToken cancellationToken);
}

public sealed record SearchAuditLogQuery(
    string? EntityType = null,
    Guid? EntityId = null,
    Guid? UserId = null,
    string? Module = null,
    string? Action = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50) : IQuery<AuditLogPage>;

internal sealed class SearchAuditLogValidator : AbstractValidator<SearchAuditLogQuery>
{
    public SearchAuditLogValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
        RuleFor(x => x.To).GreaterThan(x => x.From).When(x => x.From is not null && x.To is not null);
    }
}

internal sealed class SearchAuditLogHandler(IAuditReadModel read) : IQueryHandler<SearchAuditLogQuery, AuditLogPage>
{
    public async Task<Result<AuditLogPage>> Handle(SearchAuditLogQuery q, CancellationToken cancellationToken) =>
        await read.SearchAsync(
            new AuditLogFilter(q.EntityType, q.EntityId, q.UserId, q.Module, q.Action, q.From, q.To, q.Page, q.PageSize),
            cancellationToken);
}

/// <summary>Verifica la integridad de toda la bitácora (filas, sellos y cadena).</summary>
public sealed record VerifyAuditQuery : IQuery<AuditVerificationDto>;

internal sealed class VerifyAuditHandler(IAuditReadModel read) : IQueryHandler<VerifyAuditQuery, AuditVerificationDto>
{
    public async Task<Result<AuditVerificationDto>> Handle(VerifyAuditQuery request, CancellationToken cancellationToken) =>
        await read.VerifyAsync(cancellationToken);
}
