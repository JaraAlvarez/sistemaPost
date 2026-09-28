using Pos.Application.Abstractions.Licensing;

namespace Pos.Infrastructure.Licensing;

/// <summary>
/// Implementación provisional: todo habilitado y sin límites. Se reemplaza en la Fase 12 por la que lee
/// el token de licencia firmado. Existe desde ahora para que cada endpoint nazca declarando su feature.
/// </summary>
public sealed class AllowAllFeatureGate : IFeatureGate
{
    public bool IsEnabled(string featureCode) => true;

    public int? GetLimit(string limitCode) => null;
}
