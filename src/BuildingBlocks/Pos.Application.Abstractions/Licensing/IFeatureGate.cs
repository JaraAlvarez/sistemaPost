namespace Pos.Application.Abstractions.Licensing;

/// <summary>
/// Funcionalidades y límites habilitados por la licencia vigente (doc 09).
/// Hasta la Fase 12 se usa una implementación que habilita todo.
/// </summary>
public interface IFeatureGate
{
    bool IsEnabled(string featureCode);

    /// <summary>Límite numérico de la licencia (p. ej. <c>max_terminals</c>); <c>null</c> = ilimitado.</summary>
    int? GetLimit(string limitCode);
}
