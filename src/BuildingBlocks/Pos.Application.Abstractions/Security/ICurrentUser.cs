namespace Pos.Application.Abstractions.Security;

/// <summary>Contexto del usuario y del puesto que ejecuta la petición. Se implementa en la Fase 3.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string? DisplayName { get; }

    Guid? CompanyId { get; }

    Guid? BranchId { get; }

    Guid? PosTerminalId { get; }

    Guid? SessionId { get; }
}

/// <summary>Verifica permisos efectivos (roles + excepciones + licencia). Se implementa en la Fase 3.</summary>
public interface IPermissionChecker
{
    Task<bool> HasPermissionAsync(string permissionCode, Guid? branchId = null, CancellationToken cancellationToken = default);
}
