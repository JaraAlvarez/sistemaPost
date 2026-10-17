using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Security;
using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Domain;

namespace Pos.Modules.Cash.Infrastructure.Sistecredito;

internal sealed class SistecreditoModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SistecreditoSettings>(b =>
        {
            b.ToTable("sistecredito_settings", "cash");
            b.HasKey(x => x.CompanyId);
            b.Property(x => x.CompanyId).ValueGeneratedNever();
            b.Property(x => x.Environment).HasUpperSnakeConversion();
            b.Ignore(x => x.HasCredentials);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<SistecreditoAttempt>(b =>
        {
            b.ToTable("sistecredito_attempts", "cash");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Environment).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Ignore(x => x.MaskedDocument);
            b.Ignore(x => x.IsUsable);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });
    }
}

internal sealed class SistecreditoStore(PosDbContext context) : ISistecreditoStore
{
    public void Add(SistecreditoSettings settings) => context.Add(settings);

    public void Add(SistecreditoAttempt attempt) => context.Add(attempt);

    public async Task<SistecreditoSettings?> GetSettingsAsync(Guid companyId, CancellationToken cancellationToken) =>
        context.ChangeTracker.Entries<SistecreditoSettings>().Select(e => e.Entity).FirstOrDefault(s => s.CompanyId == companyId)
        ?? await context.Set<SistecreditoSettings>().SingleOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

    public Task<SistecreditoAttempt?> GetAttemptAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<SistecreditoAttempt>().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<SistecreditoAttempt?> FindByTransactionAsync(string transactionId, CancellationToken cancellationToken) =>
        context.Set<SistecreditoAttempt>().SingleOrDefaultAsync(a => a.TransactionId == transactionId, cancellationToken);

    public async Task<IReadOnlyList<SistecreditoAttempt>> ListForSaleAsync(Guid saleId, CancellationToken cancellationToken) =>
        await context.Set<SistecreditoAttempt>().Where(a => a.SaleId == saleId).OrderBy(a => a.StartedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SistecreditoAttempt>> ListToReconcileAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var pendingBefore = now.AddMinutes(-1);
        var cancelledSince = now.AddHours(-24);
        var recheckBefore = now.AddMinutes(-5);
        var failed = SistecreditoProtocol.FailedStatuses.Append(SistecreditoProtocol.NotFoundStatus).ToList();

        // El que lleva más tiempo sin consultarse va primero: así todos tienen turno aunque haya más de "limit" abiertos.
        return await context.Set<SistecreditoAttempt>()
            .Where(a => (a.Status == SistecreditoAttemptStatus.Pending && a.StartedAt < pendingBefore)
                        || (a.Status == SistecreditoAttemptStatus.Cancelled && a.TransactionId != null && a.CompletedSaleId == null && a.StartedAt > cancelledSince
                            && (a.ProviderStatus == null || !failed.Contains(a.ProviderStatus))
                            && (a.LastCheckedAt == null || a.LastCheckedAt < recheckBefore))
                        || (a.Status == SistecreditoAttemptStatus.Approved && a.CompletedSaleId == null))
            .OrderBy(a => a.LastCheckedAt ?? a.StartedAt)
            .ThenBy(a => a.StartedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SistecreditoAttempt>> ListAsync(SistecreditoAttemptStatus? status, int limit, CancellationToken cancellationToken) =>
        await context.Set<SistecreditoAttempt>().AsNoTracking()
            .Where(a => status == null || a.Status == status)
            .OrderByDescending(a => a.StartedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<int> CountPendingReviewsAsync(CancellationToken cancellationToken) =>
        context.Set<SistecreditoAttempt>().CountAsync(a => a.Status == SistecreditoAttemptStatus.ReviewRequired && a.ReviewedAt == null, cancellationToken);
}

/// <summary>
/// Llaves de Sistecrédito cifradas con DPAPI de la máquina y una entropía propia (mismo mecanismo que las credenciales de Factus y el
/// secreto de los backups). Fuera de Windows (desarrollo) se guardan sin cifrar, igual que aquellas.
/// </summary>
internal sealed class DpapiSistecreditoCredentialProtector : ISistecreditoCredentialProtector
{
    // Identificador interno estable: cambiarlo haría ilegibles las llaves ya cifradas.
    public const string Purpose = "BusinessPost.Cash.Sistecredito.Credentials.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public byte[] Protect(SistecreditoCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Stored(credentials.SubscriptionKey, credentials.ApplicationKey, credentials.ApplicationToken), Json);
        return OperatingSystem.IsWindows() ? ProtectedSecret.ProtectBytes(plain, Purpose) : plain;
    }

    public SistecreditoCredentials? Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        try
        {
            var plain = OperatingSystem.IsWindows() ? ProtectedSecret.UnprotectBytes(protectedBytes, Purpose) : protectedBytes;
            return JsonSerializer.Deserialize<Stored>(plain, Json) is { } stored
                ? new SistecreditoCredentials(stored.SubscriptionKey, stored.ApplicationKey, stored.ApplicationToken)
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private sealed record Stored(string SubscriptionKey, string ApplicationKey, string ApplicationToken);
}

/// <summary>Proceso en segundo plano de la revisión automática de Sistecrédito (D7B-05), cada ⚙️ 60 s.</summary>
internal sealed partial class SistecreditoReconcileWorker(
    IServiceScopeFactory scopes, DatabaseReadiness readiness, IInstallationContext installation, SistecreditoOptions options, TimeProvider time,
    ILogger<SistecreditoReconcileWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Worker)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.ReconcileIntervalSeconds, 10, 3600)), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (readiness.IsReady && installation.IsSetupCompleted)
                {
                    await RunOnceAsync(scopes, time.GetUtcNow(), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    /// <summary>Una vuelta: consulta a Sistecrédito fuera de la transacción y guarda los cambios (con su bitácora) al final.</summary>
    public static async Task<(int Checked, int Changed)> RunOnceAsync(IServiceScopeFactory scopes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var result = await services.GetRequiredService<SistecreditoReconciler>().RunAsync(now, cancellationToken);
        var context = services.GetRequiredService<PosDbContext>();
        if (context.ChangeTracker.HasChanges())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la revisión automática de los cobros con Sistecrédito.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

public static class SistecreditoInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(sp => Read(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IModelContributor, SistecreditoModelContributor>();
        services.AddScoped<ISistecreditoStore, SistecreditoStore>();
        services.AddSingleton<ISistecreditoCredentialProtector, DpapiSistecreditoCredentialProtector>();
        services.TryAddSingleton(TimeProvider.System);

        // El tiempo de espera lo controla cada petición (SistecreditoOptions.TimeoutSeconds).
        services.AddHttpClient<ISistecreditoGateway, SistecreditoHttpGateway>(SistecreditoHttpGateway.HttpClientName, client =>
        {
            client.BaseAddress = SistecreditoHttpGateway.DefaultBaseAddress;
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddHostedService<SistecreditoReconcileWorker>();
    }

    /// <summary>
    /// <c>Pos:Sistecredito</c>: <c>ResponseUrl</c>, <c>ConfirmationUrl</c>, <c>Description</c>, <c>TimeoutSeconds</c> (⚙️ 20),
    /// <c>RedirectPollAttempts</c> (⚙️ 3), <c>RedirectPollIntervalMilliseconds</c> (⚙️ 1500), <c>ReconcileIntervalSeconds</c> (⚙️ 60),
    /// <c>ReviewAfterMinutes</c> (⚙️ 15), <c>PendingExpiryHours</c> (⚙️ 24), <c>UseRecheckTimeoutSeconds</c> (⚙️ 5) y <c>Worker</c> (⚙️ true).
    /// </summary>
    private static SistecreditoOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Pos:Sistecredito");
        var options = new SistecreditoOptions();
        if (section["ResponseUrl"] is { Length: > 0 } response)
        {
            options.ResponseUrl = response.Trim();
        }

        if (section["ConfirmationUrl"] is { Length: > 0 } confirmation)
        {
            options.ConfirmationUrl = confirmation.Trim();
        }

        if (section["Description"] is { Length: > 0 } description)
        {
            options.Description = description.Trim();
        }

        options.TimeoutSeconds = int.TryParse(section["TimeoutSeconds"], out var timeout) ? timeout : options.TimeoutSeconds;
        options.RedirectPollAttempts = int.TryParse(section["RedirectPollAttempts"], out var attempts) ? attempts : options.RedirectPollAttempts;
        options.RedirectPollIntervalMilliseconds = int.TryParse(section["RedirectPollIntervalMilliseconds"], out var interval)
            ? interval
            : options.RedirectPollIntervalMilliseconds;
        options.ReconcileIntervalSeconds = int.TryParse(section["ReconcileIntervalSeconds"], out var reconcile) ? reconcile : options.ReconcileIntervalSeconds;
        options.ReviewAfterMinutes = int.TryParse(section["ReviewAfterMinutes"], out var review) ? review : options.ReviewAfterMinutes;
        options.PendingExpiryHours = int.TryParse(section["PendingExpiryHours"], out var expiry) ? expiry : options.PendingExpiryHours;
        options.UseRecheckTimeoutSeconds = int.TryParse(section["UseRecheckTimeoutSeconds"], out var recheck) ? recheck : options.UseRecheckTimeoutSeconds;
        options.Worker = !bool.TryParse(section["Worker"], out var worker) || worker;
        return options;
    }
}
