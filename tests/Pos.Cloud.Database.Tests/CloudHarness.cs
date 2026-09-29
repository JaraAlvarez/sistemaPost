using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Infrastructure;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Api;
using Pos.Cloud.PortalIdentity.Api;
using Pos.Infrastructure;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Database.Tests;

/// <summary>
/// Composición real de la nube (persistencia, auditoría, módulos de licencias y usuarios del portal) sobre una BD migrada, sin
/// HTTP. Los casos de uso se ejecutan con el despachador como el usuario técnico con todos los permisos (los permisos por rol
/// se prueban en las pruebas de integración). La clave privada de firma vive en un archivo temporal, como en producción.
/// </summary>
public sealed class CloudHarness : IAsyncDisposable
{
    private readonly string _keyDirectory;

    private CloudHarness(CloudTestDatabase database, ServiceProvider services, string keyDirectory)
    {
        Database = database;
        Services = services;
        _keyDirectory = keyDirectory;
    }

    public CloudTestDatabase Database { get; }

    public ServiceProvider Services { get; }

    public static async Task<CloudHarness> CreateAsync(CloudPostgresFixture postgres)
    {
        var database = await postgres.CreateDatabaseAsync();
        var keyDirectory = Path.Combine(Path.GetTempPath(), "pos-cloud-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(keyDirectory);
        var keyPath = Path.Combine(keyDirectory, "signing.pem");
        using (var key = LicenseSigningKey.Generate())
        {
            await File.WriteAllTextAsync(keyPath, key.ExportPem());
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Licensing:Signing:PrivateKeyPath"] = keyPath })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPosInfrastructure(BusinessTimeZones.Colombia);
        services.AddScoped<IPortalUserContext, FullAccessUser>();
        services.AddCloudPersistence(_ => new CloudPersistenceOptions { ConnectionString = database.AppConnectionString }, runBackgroundServices: false);
        services.AddDataProtection().SetApplicationName("pos-cloud-tests");
        new PortalIdentityModule().Register(services, configuration);
        new LicensingModule().Register(services, configuration);
        services.Replace(ServiceDescriptor.Scoped<IPermissionChecker, AllowAllPermissions>());

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await provider.GetRequiredService<CloudNodeContext>().RefreshAsync(TestContext.Current.CancellationToken);
        return new CloudHarness(database, provider, keyDirectory);
    }

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    /// <summary>Ejecuta una petición en un ámbito nuevo (un contexto EF por operación, como la API).</summary>
    public async Task<TResult> SendAsync<TResult>(IRequest<TResult> request)
        where TResult : Pos.SharedKernel.Results.Result
    {
        await using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(request, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        try
        {
            Directory.Delete(_keyDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    /// <summary>El usuario técnico, autenticado (autor de los cambios en estas pruebas).</summary>
    private sealed class FullAccessUser : IPortalUserContext
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => SystemActor.Id;

        public string? DisplayName => SystemActor.DisplayName;

        public string? Role => PortalRoles.Superadmin;

        public Guid? SessionId => null;
    }

    private sealed class AllowAllPermissions : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(string permissionCode, Guid? branchId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
