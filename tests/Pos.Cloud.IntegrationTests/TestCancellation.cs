global using static Pos.Cloud.IntegrationTests.TestCancellation;

namespace Pos.Cloud.IntegrationTests;

/// <summary>Cancelación de la prueba en curso (xUnit v3) para todas las llamadas asíncronas.</summary>
public static class TestCancellation
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
