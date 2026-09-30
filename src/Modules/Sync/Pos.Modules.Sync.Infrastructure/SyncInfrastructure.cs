using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Sync.Application;
using Pos.Modules.Sync.Contracts;
using Pos.SharedKernel.Time;
using Pos.Sync.Contracts;

namespace Pos.Modules.Sync.Infrastructure;

/// <summary>
/// Fuentes con cursor (D16-01): cada tipo de dato se lee de sus tablas ordenado por (marca de tiempo, id), con un margen de ⚙️ 60 s para no
/// saltar filas de transacciones que aún no confirman. El JSON lo arma PostgreSQL.
/// </summary>
internal sealed class SyncFeeds(PosDbContext context, SyncOptions options) : ISyncFeeds
{

    private static readonly Dictionary<string, (string Version, string From, string Data, string Date)> Sources = new(StringComparer.Ordinal)
    {
        [SyncKinds.Sale] = (
            "COALESCE(s.voided_at, s.completed_at)",
            "sales.sales s JOIN org.pos_terminals t ON t.id = s.pos_terminal_id JOIN org.branches b ON b.id = s.branch_id JOIN identity.users u ON u.id = s.cashier_id WHERE s.status IN ('COMPLETED', 'VOIDED')",
            """
            json_build_object('number', s.number, 'status', s.status, 'branch', b.name, 'branchCode', b.code, 'terminal', t.code, 'cashier', u.display_name,
                'businessDate', s.business_date, 'completedAt', s.completed_at, 'voidedAt', s.voided_at, 'voidReason', s.void_reason,
                'customer', s.customer_name, 'customerIdentification', s.customer_identification, 'gross', s.gross,
                'discounts', s.discount_total + s.promotion_total, 'subtotal', s.subtotal, 'tax', s.tax_total, 'total', s.total,
                'lines', (SELECT json_agg(json_build_object('sku', l.sku, 'name', l.name, 'quantity', l.quantity, 'unitPrice', l.unit_price,
                    'total', l.total, 'tax', l.tax_total, 'status', l.status) ORDER BY l.line_no) FROM sales.sale_lines l WHERE l.sale_id = s.id),
                'payments', (SELECT json_agg(json_build_object('method', p.method_code, 'amount', p.applied) ORDER BY p.line_no)
                    FROM sales.sale_payments p WHERE p.sale_id = s.id))
            """,
            "s.business_date"),
        [SyncKinds.CashSession] = (
            "s.closed_at",
            "cash.cash_sessions s JOIN org.pos_terminals t ON t.id = s.pos_terminal_id JOIN org.branches b ON b.id = s.branch_id JOIN identity.users u ON u.id = s.cashier_id WHERE s.status = 'CLOSED'",
            """
            json_build_object('number', s.number, 'branch', b.name, 'branchCode', b.code, 'terminal', t.code, 'cashier', u.display_name,
                'businessDate', s.business_date, 'openedAt', s.opened_at, 'closedAt', s.closed_at, 'openingFloat', s.opening_float,
                'expected', s.expected_total, 'counted', s.counted_total, 'difference', s.difference, 'differenceNote', s.difference_note)
            """,
            "s.business_date"),
        [SyncKinds.Stock] = (
            "s.last_movement_at",
            "inventory.stock_balances s JOIN org.warehouses w ON w.id = s.warehouse_id JOIN org.branches b ON b.id = s.branch_id JOIN catalog.products p ON p.id = s.product_id WHERE s.last_movement_at IS NOT NULL",
            """
            json_build_object('branch', b.name, 'branchCode', b.code, 'warehouse', w.code, 'sku', p.sku, 'name', p.name, 'lotId', s.lot_id,
                'quantity', s.quantity, 'averageCost', s.average_cost, 'totalValue', s.total_value, 'lastMovementAt', s.last_movement_at)
            """,
            "NULL::date"),
        [SyncKinds.Product] = (
            "COALESCE(s.updated_at, s.created_at)",
            "catalog.products s JOIN catalog.categories c ON c.id = s.category_id WHERE true",
            """
            json_build_object('sku', s.sku, 'name', s.name, 'status', s.status, 'category', c.name,
                'barcode', (SELECT pb.code FROM catalog.product_barcodes pb WHERE pb.product_id = s.id ORDER BY pb.is_primary DESC LIMIT 1),
                'price', (SELECT pp.price FROM catalog.product_prices pp JOIN catalog.price_lists pl ON pl.id = pp.price_list_id
                          WHERE pp.product_id = s.id AND pl.is_default AND pp.branch_id IS NULL AND pp.packaging_id IS NULL AND pp.deleted_at IS NULL
                            AND pp.valid_from <= now() AND (pp.valid_to IS NULL OR pp.valid_to > now())
                          ORDER BY pp.valid_from DESC LIMIT 1))
            """,
            "NULL::date"),
    };

    public async Task<IReadOnlyList<FeedItem>> ReadAsync(string kind, CursorPosition after, int limit, CancellationToken cancellationToken)
    {
        var (version, from, data, date) = Sources[kind];
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT s.id AS Id, {version} AS Version, ({date})::timestamp AS BusinessDate, ({data})::text AS Data
            FROM {from}
              AND ({version}, s.id) > (@ts, @id)
              AND {version} < now() - make_interval(secs => @lag)
            ORDER BY {version}, s.id
            LIMIT @limit
            """,
            new { ts = after.Timestamp, id = after.Id, lag = options.SafetyLagSeconds, limit }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r =>
        {
            var at = new DateTimeOffset(DateTime.SpecifyKind(r.Version, DateTimeKind.Utc));
            using var json = JsonDocument.Parse(r.Data);
            return new FeedItem(
                new SyncItem(kind, r.Id.ToString(), at, r.BusinessDate is { } d ? DateOnly.FromDateTime(d) : null, json.RootElement.Clone()),
                new CursorPosition(at, r.Id));
        })];
    }

    public async Task<int> CountAsync(string kind, CursorPosition after, CancellationToken cancellationToken)
    {
        var (version, from, _, _) = Sources[kind];
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT count(*)::int FROM {from} AND ({version}, s.id) > (@ts, @id)",
            new { ts = after.Timestamp, id = after.Id }, transaction, cancellationToken: cancellationToken));
    }

    private async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    private sealed record Row(Guid Id, DateTime Version, DateTime? BusinessDate, string Data);
}

internal sealed class SyncStore(PosDbContext context) : ISyncStore
{
    public async Task<IReadOnlyDictionary<string, SyncCursor>> CursorsAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        var rows = await context.Database.GetDbConnection().QueryAsync<CursorRow>(new CommandDefinition(
            "SELECT kind AS Kind, acked_ts AS AckedTs, acked_id AS AckedId, exported_ts AS ExportedTs, exported_id AS ExportedId FROM sync.cursors",
            cancellationToken: cancellationToken));
        return rows.ToDictionary(
            r => r.Kind,
            r => new SyncCursor(r.Kind, new CursorPosition(Utc(r.AckedTs), r.AckedId), new CursorPosition(Utc(r.ExportedTs), r.ExportedId)),
            StringComparer.Ordinal);
    }

    public Task SaveAckedAsync(string kind, CursorPosition position, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            INSERT INTO sync.cursors (kind, acked_ts, acked_id, updated_at) VALUES (@kind, @ts, @id, now())
            ON CONFLICT (kind) DO UPDATE SET acked_ts = @ts, acked_id = @id, updated_at = now()
            """,
            new { kind, ts = position.Timestamp, id = position.Id }, cancellationToken);

    public Task SaveExportedAsync(string kind, CursorPosition position, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            INSERT INTO sync.cursors (kind, exported_ts, exported_id, updated_at) VALUES (@kind, @ts, @id, now())
            ON CONFLICT (kind) DO UPDATE SET exported_ts = @ts, exported_id = @id, updated_at = now()
            """,
            new { kind, ts = position.Timestamp, id = position.Id }, cancellationToken);

    public Task RecordBatchAsync(SyncBatchDto batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return ExecuteAsync(
            """
            INSERT INTO sync.batches (id, created_at, via, items, status, applied, ignored, error, acked_at)
            VALUES (@Id, @CreatedAt, @Via, @Items, @Status, @Applied, @Ignored, left(@Error, 500), @AckedAt)
            """,
            batch, cancellationToken);
    }

    public async Task<IReadOnlyList<SyncBatchDto>> ListBatchesAsync(int limit, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        var rows = await context.Database.GetDbConnection().QueryAsync<BatchRow>(new CommandDefinition(
            """
            SELECT id AS Id, created_at AS CreatedAt, via AS Via, items AS Items, status AS Status, applied AS Applied, ignored AS Ignored, error AS Error,
                   acked_at AS AckedAt
            FROM sync.batches ORDER BY created_at DESC LIMIT @limit
            """,
            new { limit }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new SyncBatchDto(r.Id, Utc(r.CreatedAt), r.Via, r.Items, r.Status, r.Applied, r.Ignored, r.Error,
            r.AckedAt is { } a ? Utc(a) : null))];
    }

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        await context.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    private static DateTimeOffset Utc(DateTime value) =>
        value == DateTime.MinValue ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record CursorRow(string Kind, DateTime AckedTs, Guid AckedId, DateTime ExportedTs, Guid ExportedId);

    private sealed record BatchRow(Guid Id, DateTime CreatedAt, string Via, int Items, string Status, int? Applied, int? Ignored, string? Error, DateTime? AckedAt);
}

/// <summary>Configuración <c>Pos:Sync</c>. La dirección es la del servidor de licencias salvo que se indique otra.</summary>
public sealed class SyncOptions
{
    public string? ServerUrl { get; set; }

    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Margen (⚙️ 60 s) para no leer filas de transacciones aún abiertas; las pruebas lo ponen en 0.</summary>
    public int SafetyLagSeconds { get; set; } = 60;

    /// <summary>Clave pública de la nube para el paquete; SOLO fuera de producción (en producción, la embebida: <c>sync-key.json</c>).</summary>
    public string? DevelopmentCloudPublicKey { get; set; }
}

/// <summary>Cliente HTTP de la nube: envía el lote con el token de licencia y la huella (D16-03).</summary>
internal sealed partial class SyncCloudClient(HttpClient? http, string? cloudPublicKey, ILogger<SyncCloudClient> logger) : ISyncCloud
{
    public bool IsConfigured => http?.BaseAddress is not null;

    public string? CloudPublicKey => string.IsNullOrWhiteSpace(cloudPublicKey) ? null : cloudPublicKey;

    public async Task<SyncSendResult> SendAsync(SyncBatch batch, Modules.Licensing.Contracts.LicenseCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (http is null)
        {
            return new SyncSendResult(null, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SyncRoutes.Batches.TrimStart('/'))
            {
                Content = JsonContent.Create(batch, options: SyncJson.Options),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(SyncRoutes.AuthorizationScheme, credentials.Token);
            request.Headers.Add(SyncRoutes.FingerprintHeader, credentials.Fingerprint);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new SyncSendResult(await response.Content.ReadFromJsonAsync<SyncAck>(SyncJson.Options, cancellationToken), null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new SyncSendResult(null, $"La nube respondió {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, ex.Message);
            return new SyncSendResult(null, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Nube no disponible para sincronizar: {Error}")]
    private static partial void LogUnreachable(ILogger logger, string error);
}

internal sealed class SyncEnvironment(IInstallationContext installation, IClock clock) : ISyncEnvironment
{
    public Guid InstallationId => installation.NodeId;

    public string AppVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public DateTimeOffset UtcNow => clock.UtcNow;
}

/// <summary>Envío periódico (⚙️ cada 60 s): sube lo pendiente mientras haya Internet; si no, reintenta en el siguiente ciclo.</summary>
internal sealed partial class SyncWorker(
    IServiceScopeFactory scopes,
    DatabaseReadiness readiness,
    IInstallationContext installation,
    SyncOptions options,
    TimeProvider time,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.IntervalSeconds, 10, 3600)), time, stoppingToken);
                if (readiness.IsReady && installation.IsSetupCompleted)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<SyncService>().PushAsync(maxBatches: 20, stoppingToken);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el ciclo de sincronización.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

public static class SyncInfrastructureRegistration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(sp => Read(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ISyncCloud>(sp =>
        {
            var options = sp.GetRequiredService<SyncOptions>();
            var production = sp.GetRequiredService<IHostEnvironment>().IsProduction();
            return new SyncCloudClient(
                Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out var url)
                    ? new HttpClient { BaseAddress = new Uri(url.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) }
                    : null,
                EmbeddedKey() ?? (production ? null : options.DevelopmentCloudPublicKey),
                sp.GetRequiredService<ILogger<SyncCloudClient>>());
        });
        services.AddScoped<ISyncFeeds, SyncFeeds>();
        services.AddScoped<ISyncStore, SyncStore>();
        services.AddScoped<ISyncEnvironment, SyncEnvironment>();
        services.AddScoped<SyncService>();
        services.AddHostedService<SyncWorker>();
    }

    private static SyncOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Pos:Sync");
        return new SyncOptions
        {
            ServerUrl = section["ServerUrl"] is { Length: > 0 } url ? url : configuration["Pos:Licensing:ServerUrl"],
            IntervalSeconds = int.TryParse(section["IntervalSeconds"], out var seconds) ? seconds : 60,
            SafetyLagSeconds = int.TryParse(section["SafetyLagSeconds"], out var lag) ? Math.Max(0, lag) : 60,
            DevelopmentCloudPublicKey = section["DevelopmentCloudPublicKey"],
        };
    }

    /// <summary>Clave pública de sincronización embebida (se completa al compilar la versión de producción).</summary>
    private static string? EmbeddedKey()
    {
        using var stream = typeof(SyncInfrastructureRegistration).Assembly.GetManifestResourceStream("Pos.Sync.CloudKey.json");
        return stream is null ? null
            : JsonSerializer.Deserialize<KeyFile>(stream, Json)?.PublicKey is { Length: > 0 } key ? key : null;
    }

    private sealed record KeyFile(string? PublicKey);
}
