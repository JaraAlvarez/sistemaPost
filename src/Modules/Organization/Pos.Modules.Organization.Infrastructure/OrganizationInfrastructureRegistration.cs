using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Organization.Application;

namespace Pos.Modules.Organization.Infrastructure;

public static class OrganizationInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, OrganizationModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, OrganizationConstraintErrors>();
        services.AddScoped<IOrganizationStore, OrganizationStore>();
        services.AddScoped<Application.Devices.IDeviceStore, DeviceStore>();
        services.AddSingleton<Contracts.IDeviceAuthenticator, DeviceAuthenticator>();
        services.AddSingleton<Contracts.ITerminalDirectory, TerminalDirectory>();
        services.AddSingleton<Contracts.IWarehouseDirectory, WarehouseDirectory>();
    }
}
