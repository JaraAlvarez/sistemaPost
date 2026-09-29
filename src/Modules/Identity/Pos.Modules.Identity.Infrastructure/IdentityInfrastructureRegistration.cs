using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Identity.Application;

namespace Pos.Modules.Identity.Infrastructure;

public static class IdentityInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, IdentityModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, IdentityConstraintErrors>();
        services.AddScoped<IIdentityStore, IdentityStore>();
    }
}
