using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Identity.Application;
using Pos.Modules.Identity.Contracts;

namespace Pos.Modules.Identity.Infrastructure;

public static class IdentityInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, IdentityModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, IdentityConstraintErrors>();
        services.AddScoped<IIdentityStore, IdentityStore>();
        services.AddScoped<SessionStore>();
        services.AddScoped<ISessionStore>(sp => sp.GetRequiredService<SessionStore>());
        services.AddScoped<ILoginAttemptLog>(sp => sp.GetRequiredService<SessionStore>());
        services.AddScoped<IAuthorizationGrantStore>(sp => sp.GetRequiredService<SessionStore>());

        services.AddSingleton<IPermissionEvaluator, PermissionEvaluator>();
        services.AddSingleton<ISessionAuthenticator, SessionAuthenticator>();
        services.AddSingleton<ISessionRevoker, SessionRevoker>();
        services.AddSingleton<IIdentityState, IdentityState>();
        services.AddScoped<ISupervisorAuthorization, SupervisorAuthorization>();
        services.TryAddScoped<ICurrentSecurityVersion, NoSecurityVersion>();

        // Fase 3: la verificación permisiva de la Fase 2 se reemplaza por la real.
        services.Replace(ServiceDescriptor.Scoped<IPermissionChecker, PermissionChecker>());
    }

    /// <summary>Fuera de una petición HTTP no hay versión de seguridad: los permisos se calculan sin caché.</summary>
    private sealed class NoSecurityVersion : ICurrentSecurityVersion
    {
        public long? SecurityVersion => null;
    }
}
