namespace Pos.Application.Abstractions.Security;

/// <summary>Contexto del usuario autenticado y del puesto desde el que trabaja (Fase 3: sesión opaca validada por el host).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string? DisplayName { get; }

    Guid? CompanyId { get; }

    Guid? BranchId { get; }

    Guid? PosTerminalId { get; }

    Guid? SessionId { get; }

    /// <summary>Sesión de caja (código + PIN) en lugar de backoffice.</summary>
    bool IsTerminalSession { get; }

    /// <summary>Debe cambiar su contraseña: solo puede cambiarla, consultar su perfil o salir.</summary>
    bool MustChangePassword { get; }
}

/// <summary>Verifica permisos efectivos del usuario actual: roles con alcance por sucursal ∪ GRANT − DENY (D3-06).</summary>
public interface IPermissionChecker
{
    Task<bool> HasPermissionAsync(string permissionCode, Guid? branchId = null, CancellationToken cancellationToken = default);
}
