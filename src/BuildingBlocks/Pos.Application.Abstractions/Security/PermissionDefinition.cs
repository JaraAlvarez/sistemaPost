using System.Text.RegularExpressions;

namespace Pos.Application.Abstractions.Security;

/// <summary>
/// Permiso declarado en el código (fuente de verdad del catálogo <c>identity.permissions</c>).
/// Formato del código: <c>modulo.recurso.accion</c>, p. ej. <c>organization.branch.manage</c>.
/// </summary>
public sealed partial record PermissionDefinition
{
    public PermissionDefinition(string code, string description, bool isSensitive)
    {
        if (!CodePattern().IsMatch(code ?? string.Empty))
        {
            throw new ArgumentException($"Código de permiso inválido '{code}'.", nameof(code));
        }

        Code = code!;
        Module = Code[..Code.IndexOf('.', StringComparison.Ordinal)];
        Description = description;
        IsSensitive = isSensitive;
    }

    public string Code { get; }

    public string Module { get; }

    public string Description { get; }

    /// <summary>Permiso delicado (p. ej. modificar la empresa): se destaca al asignarlo y se audita con severidad alta.</summary>
    public bool IsSensitive { get; }

    [GeneratedRegex(@"^[a-z]+\.[a-z_]+\.[a-z_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>Cada módulo declara sus permisos implementando esta interfaz.</summary>
public interface IPermissionCatalogProvider
{
    IEnumerable<PermissionDefinition> GetPermissions();
}
