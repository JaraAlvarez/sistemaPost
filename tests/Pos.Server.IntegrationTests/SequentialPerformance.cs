namespace Pos.Server.IntegrationTests;

/// <summary>
/// Pruebas de rendimiento y de carga: corren solas, al final y sin paralelismo, para que sus tiempos (y el límite de 30 s
/// por transacción del rol de la aplicación) no dependan de las demás pruebas que se ejecutan a la vez.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SequentialPerformance
{
    public const string Name = "Rendimiento";
}
