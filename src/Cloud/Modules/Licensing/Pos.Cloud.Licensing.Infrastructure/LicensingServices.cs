using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.Licensing.Domain;
using Pos.Infrastructure;
using Pos.Infrastructure.Persistence;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Infrastructure;

/// <summary>
/// Firma con la clave privada Ed25519 del archivo <c>Licensing:Signing:PrivateKeyPath</c> (L-04, L-11): nunca en la BD ni en el
/// repositorio. Se lee una vez al arrancar; si falta o no se puede leer, el portal funciona pero no se emiten tokens.
/// </summary>
internal sealed partial class FileLicenseTokenSigner : ILicenseTokenSigner, IDisposable
{
    private readonly LicenseSigningKey? _key;
    private volatile bool _enabled;

    public FileLicenseTokenSigner(LicensingOptions options, ILogger<FileLicenseTokenSigner> logger)
    {
        var path = options.Signing.PrivateKeyPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            LogMissing(logger, "(sin configurar)");
            return;
        }

        try
        {
            _key = LicenseSigningKey.ImportPem(File.ReadAllText(path));
            LogLoaded(logger, _key.Kid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            LogMissing(logger, path, ex);
        }
    }

    public bool IsAvailable => _key is not null && _enabled;

    public LicensePublicKey? PublicKey => _key?.PublicKey;

    public string Sign(LicenseClaims claims) =>
        IsAvailable ? LicenseToken.Sign(claims, _key!) : throw new InvalidOperationException("La firma de tokens no está disponible.");

    public void SetEnabled(bool enabled) => _enabled = enabled && _key is not null;

    public void Dispose() => _key?.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Clave de firma de licencias cargada ({Kid})")]
    private static partial void LogLoaded(ILogger logger, string kid);

    [LoggerMessage(Level = LogLevel.Critical, Message = "No se pudo cargar la clave privada de firma de licencias desde {Path}: el servidor no emitirá tokens")]
    private static partial void LogMissing(ILogger logger, string path, Exception? exception = null);
}

/// <summary>Claves públicas de confianza leídas de la BD, en caché un minuto (se invalida al registrar, activar o revocar).</summary>
internal sealed class CachedTrustedSigningKeys(IServiceScopeFactory scopes, IClock clock) : ITrustedSigningKeys
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private (LicenseKeyRing Ring, DateTimeOffset LoadedAt)? _cache;

    public async Task<LicenseKeyRing> GetAsync(CancellationToken cancellationToken)
    {
        if (_cache is { } cached && clock.UtcNow - cached.LoadedAt < Lifetime)
        {
            return cached.Ring;
        }

        await using var scope = scopes.CreateAsyncScope();
        var keys = await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Set<SigningKey>().AsNoTracking()
            .Where(k => k.Status != SigningKeyStatus.Revoked)
            .Select(k => k.PublicKey)
            .ToListAsync(cancellationToken);
        var ring = new LicenseKeyRing(keys.Select(k => LicensePublicKey.TryParse(k, out var key) ? key : null).OfType<LicensePublicKey>());
        _cache = (ring, clock.UtcNow);
        return ring;
    }

    public void Invalidate() => _cache = null;
}

/// <summary>Ventana fija por licencia (en memoria del proceso): N solicitudes por hora.</summary>
internal sealed class LicenseThrottle(LicensingOptions options, IClock clock) : ILicenseThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, Counter> _counters = new();

    public bool TryAcquire(Guid licenseId)
    {
        var now = clock.UtcNow;
        if (_counters.Count > 50_000)
        {
            foreach (var stale in _counters.Where(c => now - c.Value.WindowStart >= Window).Select(c => c.Key).ToList())
            {
                _counters.TryRemove(stale, out _);
            }
        }

        var counter = _counters.GetOrAdd(licenseId, _ => new Counter(now));
        lock (counter)
        {
            if (now - counter.WindowStart >= Window)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            if (counter.Count >= options.RequestsPerLicensePerHour)
            {
                return false;
            }

            counter.Count++;
            return true;
        }
    }

    private sealed class Counter(DateTimeOffset windowStart)
    {
        public DateTimeOffset WindowStart { get; set; } = windowStart;

        public int Count { get; set; }
    }
}

public static class LicensingInfrastructureRegistration
{
    public static void Register(IServiceCollection services, Func<IServiceProvider, LicensingOptions> options)
    {
        services.AddSingleton(options);
        services.AddRequestHandlersFrom(typeof(ILicensingStore).Assembly);
        services.AddSingleton<ICloudModelContributor, LicensingModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, LicensingConstraintErrors>();
        services.AddScoped<ILicensingStore, LicensingStore>();
        services.AddScoped<ILicensingReadModel, LicensingReadModel>();
        services.AddScoped<PortalChange>();
        services.AddScoped<LicenseTokenFactory>();
        services.AddSingleton<ILicenseTokenSigner, FileLicenseTokenSigner>();
        services.AddSingleton<ITrustedSigningKeys, CachedTrustedSigningKeys>();
        services.AddSingleton<ILicenseThrottle, LicenseThrottle>();
    }
}
