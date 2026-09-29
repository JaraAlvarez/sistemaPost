using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.PortalIdentity.Application;

/// <summary>Página de la bitácora: filas de la página pedida y si hay más (se consulta una fila adicional).</summary>
public sealed record CloudAuditPageDto(IReadOnlyList<CloudAuditEntryDto> Rows, int Page, int PageSize, bool HasMore);

/// <summary>Consulta paginada de la auditoría de la nube (de la más reciente a la más antigua). <paramref name="Page"/> empieza en 1.</summary>
[RequiresPermission(CloudPermissions.AuditView)]
public sealed record SearchCloudAuditQuery(
    DateTimeOffset? From, DateTimeOffset? To, string? Module, string? Action, string? Text, int Page = 1, int PageSize = 50) : IQuery<CloudAuditPageDto>;

internal sealed class SearchCloudAuditHandler(ICloudAuditLog log) : IQueryHandler<SearchCloudAuditQuery, CloudAuditPageDto>
{
    public async Task<Result<CloudAuditPageDto>> Handle(SearchCloudAuditQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 10, 200);
        var rows = await log.SearchAsync(
            new CloudAuditFilter(
                request.From, request.To, Blank(request.Module), Blank(request.Action), null, Blank(request.Text), size + 1, (page - 1) * size),
            cancellationToken);
        return new CloudAuditPageDto([.. rows.Take(size)], page, size, rows.Count > size);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Verifica filas, sellos y la cadena de hashes de la auditoría (misma verificación que la consola).</summary>
[RequiresPermission(CloudPermissions.AuditView)]
public sealed record VerifyCloudAuditQuery : IQuery<CloudAuditIntegrityDto>;

internal sealed class VerifyCloudAuditHandler(ICloudAuditLog log) : IQueryHandler<VerifyCloudAuditQuery, CloudAuditIntegrityDto>
{
    public async Task<Result<CloudAuditIntegrityDto>> Handle(VerifyCloudAuditQuery request, CancellationToken cancellationToken) =>
        await log.VerifyAsync(cancellationToken);
}
