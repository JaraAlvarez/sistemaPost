namespace Pos.Modules.Identity.Domain;

/// <summary>Definición de un rol de sistema (doc 06). Los permisos se completan en cada fase.</summary>
public sealed record SystemRoleDefinition(string Code, string Name, string Description, IReadOnlyCollection<string> Permissions);
