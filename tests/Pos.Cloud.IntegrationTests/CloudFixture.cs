using Pos.License.Simulator;

namespace Pos.Cloud.IntegrationTests;

/// <summary>Un servidor de licencias por clase de pruebas, con el superadministrador ya dentro (contraseña + TOTP).</summary>
public class CloudFixture : IAsyncLifetime
{
    public CloudServerFactory Factory { get; } = new();

    public HttpClient Superadmin { get; private set; } = null!;

    public PortalCredentials SuperadminUser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Factory.StartAsync();
        (Superadmin, SuperadminUser) = await PortalApi.BootstrapSuperadminAsync(Factory);
    }

    public async ValueTask DisposeAsync()
    {
        Superadmin.Dispose();
        await Factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Un POS simulado que habla con este servidor en memoria.</summary>
    public SimulatedPos NewPos(string nit, string role = Pos.Licensing.Contracts.DeviceRoles.StoreServer) =>
        new(Factory.CreateClient(), SimulatorIdentity.Create(nit, role, board: $"PLACA-{Guid.NewGuid():N}", disk: $"DISCO-{Guid.NewGuid():N}", machine: $"MAQ-{Guid.NewGuid():N}"));
}
