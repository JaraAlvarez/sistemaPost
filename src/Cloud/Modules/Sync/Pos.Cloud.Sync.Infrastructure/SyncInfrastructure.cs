using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Sync.Application;
using Pos.Infrastructure;
using Pos.Sync.Contracts;

namespace Pos.Cloud.Sync.Infrastructure;

/// <summary>
/// Documentos recibidos de las tiendas (D16-05): una fila por instalación, tipo e id con su versión; el más reciente gana. Las consultas del
/// portal leen el JSON directamente (suficiente para el volumen de v1).
/// </summary>
internal sealed class SyncRepository(CloudDbContext context) : ISyncRepository
{
    /// <summary>Documentos por sentencia al aplicar (un paquete trae hasta 200 000).</summary>
    public const int ApplyChunk = 5_000;

    /// <summary>Tolerancia de la versión sobre la hora del servidor (S2): una versión más futura se acota para no congelar el documento.</summary>
    public const int VersionToleranceMinutes = 10;

    public async Task<SyncAck> ApplyAsync(
        SyncBatch batch, PosInstallationIdentity installation, string via, Guid? uploadedBy, CancellationToken cancellationToken, string? signatureStatus = null,
        string? signerKey = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(installation);
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var fresh = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO sync.batches (id, installation_id, organization_id, via, received_at, items, uploaded_by, signature_status, signer_key)
            VALUES (@id, @installation, @organization, @via, now(), @items, @uploadedBy, @signatureStatus, @signerKey)
            -- Un paquete DESCARTADO se puede volver a cargar (p. ej. cuando la tienda ya registró su clave): se aplica de nuevo.
            ON CONFLICT (id) DO UPDATE
                SET received_at = now(), uploaded_by = EXCLUDED.uploaded_by, signature_status = EXCLUDED.signature_status,
                    signer_key = EXCLUDED.signer_key, reviewed_at = NULL, reviewed_by = NULL, applied = NULL, ignored = NULL
                WHERE sync.batches.signature_status = 'DISCARDED' AND sync.batches.installation_id = EXCLUDED.installation_id
            """,
            new
            {
                id = batch.BatchId, installation = installation.InstallationId, organization = installation.OrganizationId, via, items = batch.Items.Count, uploadedBy,
                signatureStatus, signerKey,
            },
            transaction, cancellationToken: cancellationToken)) == 1;
        if (!fresh)
        {
            var previous = await connection.QuerySingleAsync<(int Applied, int Ignored)>(new CommandDefinition(
                "SELECT applied, ignored FROM sync.batches WHERE id = @id", new { id = batch.BatchId }, transaction, cancellationToken: cancellationToken));
            return new SyncAck(batch.BatchId, batch.Items.Count, previous.Applied, previous.Ignored, Duplicate: true);
        }

        var applied = 0;
        foreach (var chunk in Chunks(batch.Items))
        {
            applied += await connection.ExecuteAsync(new CommandDefinition(
                """
                WITH incoming AS (
                    -- Lo que la nube no usa no se guarda aunque lo mande una tienda sin actualizar (cédula/NIT del cliente, costo promedio).
                    SELECT DISTINCT ON (x.kind, x.id) x.kind, x.id, LEAST(x.version, now() + make_interval(mins => @tolerance)) AS version,
                           x."businessDate" AS business_date,
                           CASE x.kind WHEN 'SALE' THEN x.data - 'customerIdentification' WHEN 'STOCK' THEN x.data - 'averageCost' ELSE x.data END AS data
                    FROM jsonb_to_recordset(@items::jsonb) AS x(kind text, id text, version timestamptz, "businessDate" date, data jsonb)
                    WHERE x.kind IN ('SALE', 'CASH_SESSION', 'STOCK', 'PRODUCT') AND x.id IS NOT NULL AND length(x.id) <= 64 AND x.version IS NOT NULL
                    ORDER BY x.kind, x.id, x.version DESC)
                INSERT INTO sync.documents (installation_id, kind, doc_id, organization_id, version, business_date, data, received_at, received_via, batch_id)
                SELECT @installation, i.kind, i.id, @organization, i.version, i.business_date, i.data, now(), @via, @batch FROM incoming i
                ON CONFLICT (installation_id, kind, doc_id) DO UPDATE
                    SET version = EXCLUDED.version, business_date = EXCLUDED.business_date, data = EXCLUDED.data,
                        received_at = EXCLUDED.received_at, received_via = EXCLUDED.received_via, batch_id = EXCLUDED.batch_id,
                        -- Un lote sin verificar guarda lo que reemplaza (para restaurarlo si se descarta); uno verificado o en línea lo limpia.
                        previous_version = CASE WHEN @unverified THEN sync.documents.version END,
                        previous_business_date = CASE WHEN @unverified THEN sync.documents.business_date END,
                        previous_data = CASE WHEN @unverified THEN sync.documents.data END,
                        previous_received_at = CASE WHEN @unverified THEN sync.documents.received_at END,
                        previous_received_via = CASE WHEN @unverified THEN sync.documents.received_via END,
                        previous_batch_id = CASE WHEN @unverified THEN sync.documents.batch_id END
                    WHERE sync.documents.version < EXCLUDED.version
                """,
                new
                {
                    items = JsonSerializer.Serialize(chunk, SyncJson.Options), installation = installation.InstallationId,
                    organization = installation.OrganizationId, via, tolerance = VersionToleranceMinutes, batch = batch.BatchId,
                    unverified = signatureStatus == "UNVERIFIED",
                },
                transaction, cancellationToken: cancellationToken));
        }

        var ignored = batch.Items.Count - applied;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE sync.batches SET applied = @applied, ignored = @ignored WHERE id = @id", new { applied, ignored, id = batch.BatchId }, transaction,
            cancellationToken: cancellationToken));
        return new SyncAck(batch.BatchId, batch.Items.Count, applied, ignored, Duplicate: false);
    }

    public async Task<SyncSigningKeyState> GetSigningKeyAsync(Guid installationId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<KeyRow>(new CommandDefinition(
            "SELECT public_key AS PublicKey, previous_key AS PreviousKey, rotated_at AS RotatedAt FROM sync.installation_keys WHERE installation_id = @installationId",
            new { installationId }, transaction, cancellationToken: cancellationToken));
        return row is null ? new SyncSigningKeyState(null, null, null) : new SyncSigningKeyState(row.PublicKey, row.PreviousKey, Utc(row.RotatedAt));
    }

    public async Task<string?> RegisterSigningKeyAsync(PosInstallationIdentity installation, string publicKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var replaced = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT public_key FROM sync.installation_keys WHERE installation_id = @installation FOR UPDATE",
            new { installation = installation.InstallationId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO sync.installation_keys (installation_id, organization_id, public_key, registered_at, updated_at)
            VALUES (@installation, @organization, @publicKey, now(), now())
            ON CONFLICT (installation_id) DO UPDATE
                SET previous_key = sync.installation_keys.public_key,
                    rotated_at = CASE WHEN sync.installation_keys.public_key IS NULL THEN NULL ELSE now() END,
                    public_key = EXCLUDED.public_key, updated_at = now(), organization_id = EXCLUDED.organization_id
                WHERE sync.installation_keys.public_key IS DISTINCT FROM EXCLUDED.public_key
            """,
            new { installation = installation.InstallationId, organization = installation.OrganizationId, publicKey }, transaction,
            cancellationToken: cancellationToken));
        return replaced;
    }

    public async Task<bool> ResetSigningKeyAsync(Guid installationId, Guid? resetBy, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE sync.installation_keys
            SET public_key = NULL, previous_key = NULL, rotated_at = NULL, reset_at = now(), reset_by = @resetBy, updated_at = now()
            WHERE installation_id = @installationId AND public_key IS NOT NULL
            """,
            new { installationId, resetBy }, transaction, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<IReadOnlyList<UnverifiedBatchDto>> UnverifiedBatchesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<UnverifiedRow>(
            """
            SELECT b.id AS BatchId, b.installation_id AS InstallationId, i.branch_name AS BranchName, b.received_at AS ReceivedAt, b.items AS Items,
                   b.signer_key IS NOT NULL AS HasSignature,
                   (b.signer_key IS NOT NULL AND (b.signer_key = k.public_key
                       OR (b.signer_key = k.previous_key AND k.rotated_at > now() - interval '7 days'))) AS MatchesKey
            FROM sync.batches b
            LEFT JOIN licensing.installations i ON i.installation_id = b.installation_id
            LEFT JOIN sync.installation_keys k ON k.installation_id = b.installation_id
            WHERE b.organization_id = @organizationId AND b.signature_status = 'UNVERIFIED'
            ORDER BY b.received_at DESC
            LIMIT 100
            """,
            new { organizationId }, cancellationToken);
        return [.. rows.Select(r => new UnverifiedBatchDto(r.BatchId, r.InstallationId, r.BranchName, Utc(r.ReceivedAt)!.Value, r.Items, r.HasSignature, r.MatchesKey))];
    }

    public async Task<Guid?> InstallationOrganizationAsync(Guid installationId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT organization_id FROM licensing.installations WHERE installation_id = @installationId", new { installationId }, transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<SyncBatchRecord?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<SyncBatchRecord>(new CommandDefinition(
            """
            SELECT id AS BatchId, installation_id AS InstallationId, organization_id AS OrganizationId, signature_status AS SignatureStatus, signer_key AS SignerKey
            FROM sync.batches WHERE id = @batchId FOR UPDATE
            """,
            new { batchId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task MarkVerifiedAsync(Guid batchId, Guid? reviewedBy, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE sync.batches SET signature_status = 'VERIFIED', reviewed_at = now(), reviewed_by = @reviewedBy WHERE id = @batchId",
            new { batchId, reviewedBy }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE sync.documents
            SET previous_version = NULL, previous_business_date = NULL, previous_data = NULL, previous_received_at = NULL, previous_received_via = NULL,
                previous_batch_id = NULL
            WHERE batch_id = @batchId AND previous_data IS NOT NULL
            """,
            new { batchId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<int> DiscardAsync(Guid batchId, Guid? reviewedBy, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        // Lo que el lote reemplazó vuelve a su estado anterior; lo que el lote creó se borra.
        var restored = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE sync.documents
            SET version = previous_version, business_date = previous_business_date, data = previous_data, received_at = previous_received_at,
                received_via = previous_received_via, batch_id = previous_batch_id,
                previous_version = NULL, previous_business_date = NULL, previous_data = NULL, previous_received_at = NULL,
                previous_received_via = NULL, previous_batch_id = NULL
            WHERE batch_id = @batchId AND previous_data IS NOT NULL
            """,
            new { batchId }, transaction, cancellationToken: cancellationToken));
        var deleted = restored + await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sync.documents WHERE batch_id = @batchId", new { batchId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE sync.batches SET signature_status = 'DISCARDED', reviewed_at = now(), reviewed_by = @reviewedBy WHERE id = @batchId",
            new { batchId, reviewedBy }, transaction, cancellationToken: cancellationToken));
        return deleted;
    }

    private static IEnumerable<List<SyncItem>> Chunks(IReadOnlyList<SyncItem> items)
    {
        for (var i = 0; i < items.Count; i += ApplyChunk)
        {
            yield return [.. items.Skip(i).Take(ApplyChunk)];
        }
    }

    public async Task<IReadOnlyList<SyncOrganizationDto>> OrganizationsAsync(Guid? accountId, CancellationToken cancellationToken) =>
        await QueryAsync<SyncOrganizationDto>(
            """
            SELECT o.id AS Id, o.legal_name AS LegalName, a.name AS AccountName
            FROM licensing.organizations o JOIN licensing.accounts a ON a.id = o.account_id
            WHERE @account::uuid IS NULL OR o.account_id = @account
            ORDER BY o.legal_name
            """,
            new { account = accountId }, cancellationToken);

    public async Task<IReadOnlyList<SyncInstallationStatusDto>> StatusAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<StatusRow>(
            """
            SELECT i.installation_id AS InstallationId, i.branch_name AS BranchName, b.received_at AS LastBatchAt, b.via AS LastVia,
                   (SELECT count(*)::int FROM sync.documents d WHERE d.installation_id = i.installation_id AND d.kind = 'SALE') AS Sales,
                   (SELECT count(*)::int FROM sync.documents d WHERE d.installation_id = i.installation_id AND d.kind = 'CASH_SESSION') AS CashSessions,
                   (SELECT count(*)::int FROM sync.documents d WHERE d.installation_id = i.installation_id AND d.kind = 'STOCK') AS StockRows,
                   (SELECT count(*)::int FROM sync.documents d WHERE d.installation_id = i.installation_id AND d.kind = 'PRODUCT') AS Products,
                   (SELECT count(*)::int FROM sync.batches u WHERE u.installation_id = i.installation_id AND u.signature_status = 'UNVERIFIED') AS Unverified,
                   EXISTS (SELECT 1 FROM sync.installation_keys k WHERE k.installation_id = i.installation_id AND k.public_key IS NOT NULL) AS KeyRegistered
            FROM licensing.installations i
            LEFT JOIN LATERAL (SELECT received_at, via FROM sync.batches sb WHERE sb.installation_id = i.installation_id ORDER BY received_at DESC LIMIT 1) b ON true
            WHERE i.organization_id = @organizationId
            ORDER BY i.branch_name
            """,
            new { organizationId }, cancellationToken);
        return [.. rows.Select(r => new SyncInstallationStatusDto(
            r.InstallationId, r.BranchName, Utc(r.LastBatchAt), r.LastVia, r.Sales, r.CashSessions, r.StockRows, r.Products, r.Unverified, r.KeyRegistered))];
    }

    public async Task<IReadOnlyList<SalesDayDto>> SalesByDayAsync(Guid organizationId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<SalesDayRow>(
            """
            SELECT d.business_date::timestamp AS BusinessDate, COALESCE(d.data->>'branch', '—') AS Branch,
                   count(*) FILTER (WHERE d.data->>'status' = 'COMPLETED')::int AS Tickets,
                   count(*) FILTER (WHERE d.data->>'status' = 'VOIDED')::int AS Voided,
                   COALESCE(sum((d.data->>'total')::numeric) FILTER (WHERE d.data->>'status' = 'COMPLETED'), 0) AS Total
            FROM sync.documents d
            WHERE d.organization_id = @organizationId AND d.kind = 'SALE' AND d.business_date BETWEEN @from AND @to
            GROUP BY d.business_date, d.data->>'branch'
            ORDER BY d.business_date DESC, 2
            """,
            new { organizationId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) }, cancellationToken);
        return [.. rows.Select(r => new SalesDayDto(DateOnly.FromDateTime(r.BusinessDate), r.Branch, r.Tickets, r.Voided, r.Total))];
    }

    public async Task<IReadOnlyList<SaleRowDto>> RecentSalesAsync(Guid organizationId, int limit, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<SaleRow>(
            """
            SELECT d.data->>'number' AS Number, COALESCE(d.data->>'branch', '—') AS Branch, d.data->>'terminal' AS Terminal, d.data->>'cashier' AS Cashier,
                   d.data->>'status' AS Status, (d.data->>'total')::numeric AS Total, c.completed_at AS CompletedAt, d.data->>'customer' AS Customer
            FROM sync.documents d
            -- La fecha de la venta es la que dice la tienda (completedAt), no la versión: una anulación o una supresión del titular cambian
            -- la versión y no deben mover la venta al principio. Un valor ilegible no rompe la consulta.
            CROSS JOIN LATERAL (SELECT CASE WHEN pg_input_is_valid(d.data->>'completedAt', 'timestamptz')
                                            THEN (d.data->>'completedAt')::timestamptz END AS completed_at) c
            WHERE d.organization_id = @organizationId AND d.kind = 'SALE'
            ORDER BY c.completed_at DESC NULLS LAST, d.version DESC LIMIT @limit
            """,
            new { organizationId, limit }, cancellationToken);
        return [.. rows.Select(r => new SaleRowDto(r.Number, r.Branch, r.Terminal, r.Cashier, r.Status, r.Total, Utc(r.CompletedAt), r.Customer))];
    }

    public async Task<IReadOnlyList<CashSessionRowDto>> CashSessionsAsync(Guid organizationId, int limit, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<CashRow>(
            """
            SELECT d.data->>'number' AS Number, COALESCE(d.data->>'branch', '—') AS Branch, d.data->>'terminal' AS Terminal, d.data->>'cashier' AS Cashier,
                   d.business_date::timestamp AS BusinessDate, (d.data->>'expected')::numeric AS Expected, (d.data->>'counted')::numeric AS Counted,
                   (d.data->>'difference')::numeric AS Difference, d.version AS ClosedAt
            FROM sync.documents d WHERE d.organization_id = @organizationId AND d.kind = 'CASH_SESSION'
            ORDER BY d.version DESC LIMIT @limit
            """,
            new { organizationId, limit }, cancellationToken);
        return [.. rows.Select(r => new CashSessionRowDto(r.Number, r.Branch, r.Terminal, r.Cashier, r.BusinessDate is { } b ? DateOnly.FromDateTime(b) : null,
            r.Expected, r.Counted, r.Difference, Utc(r.ClosedAt)))];
    }

    public async Task<IReadOnlyList<StockRowDto>> StockAsync(Guid organizationId, string? search, int limit, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<StockRow>(
            """
            SELECT COALESCE(d.data->>'branch', '—') AS Branch, d.data->>'warehouse' AS Warehouse, d.data->>'sku' AS Sku, d.data->>'name' AS Name,
                   COALESCE((d.data->>'quantity')::numeric, 0) AS Quantity, COALESCE((d.data->>'totalValue')::numeric, 0) AS TotalValue, d.version AS LastMovementAt
            FROM sync.documents d
            WHERE d.organization_id = @organizationId AND d.kind = 'STOCK'
              AND (@search::text IS NULL OR d.data->>'sku' ILIKE '%' || @search || '%' OR d.data->>'name' ILIKE '%' || @search || '%')
            ORDER BY d.data->>'name', d.data->>'branch' LIMIT @limit
            """,
            new { organizationId, search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(), limit }, cancellationToken);
        return [.. rows.Select(r => new StockRowDto(r.Branch, r.Warehouse, r.Sku, r.Name, r.Quantity, r.TotalValue, Utc(r.LastMovementAt)))];
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))];
    }

    private async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private sealed record StatusRow(
        Guid InstallationId, string? BranchName, DateTime? LastBatchAt, string? LastVia, int Sales, int CashSessions, int StockRows, int Products, int Unverified,
        bool KeyRegistered);

    private sealed record KeyRow(string? PublicKey, string? PreviousKey, DateTime? RotatedAt);

    private sealed record UnverifiedRow(Guid BatchId, Guid InstallationId, string? BranchName, DateTime ReceivedAt, int Items, bool HasSignature, bool MatchesKey);

    private sealed record SalesDayRow(DateTime BusinessDate, string Branch, int Tickets, int Voided, decimal Total);

    private sealed record SaleRow(string? Number, string Branch, string? Terminal, string? Cashier, string Status, decimal Total, DateTime? CompletedAt, string? Customer);

    private sealed record CashRow(
        string? Number, string Branch, string? Terminal, string? Cashier, DateTime? BusinessDate, decimal? Expected, decimal? Counted, decimal? Difference, DateTime? ClosedAt);

    private sealed record StockRow(string Branch, string? Warehouse, string? Sku, string? Name, decimal Quantity, decimal TotalValue, DateTime? LastMovementAt);
}

/// <summary>El equipo del propietario ve todas las empresas; el cliente, solo las de su cuenta (D16-06).</summary>
internal sealed class SyncAccess(CloudDbContext context, IPortalUserContext user) : ISyncAccess
{
    public async Task<(bool Allowed, Guid? AccountId)> ScopeAsync(CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || !CloudPermissions.RoleHas(user.Role, CloudPermissions.SyncDataView))
        {
            return (false, null);
        }

        if (user.Role != PortalRoles.Customer)
        {
            return (true, null);
        }

        await context.Database.OpenConnectionAsync(cancellationToken);
        var account = await context.Database.GetDbConnection().ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT reseller_account_id FROM portal.portal_users WHERE id = @id", new { id = user.UserId },
            context.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: cancellationToken));
        return account is null ? (false, null) : (true, account);
    }

    public async Task<bool> CanAccessAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var (allowed, account) = await ScopeAsync(cancellationToken);
        if (!allowed || account is null)
        {
            return allowed;
        }

        return await context.Database.GetDbConnection().ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM licensing.organizations WHERE id = @organizationId AND account_id = @account)", new { organizationId, account },
            context.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: cancellationToken));
    }
}

/// <summary>Configuración <c>Sync</c> de la nube: la clave privada de sincronización (archivo protegido, como la de firma de licencias).</summary>
public sealed class SyncCloudOptions
{
    public const string SectionName = "Sync";

    public string? PrivateKeyPath { get; set; }
}

internal sealed class SyncPackageOpener : ISyncPackageOpener, IDisposable
{
    private readonly Key? _key;

    public SyncPackageOpener(SyncCloudOptions options)
    {
        if (options.PrivateKeyPath is { Length: > 0 } path && File.Exists(path))
        {
            _key = SyncPackage.ImportPrivate(File.ReadAllText(path));
        }
    }

    public bool IsConfigured => _key is not null;

    public OpenedSyncPackage Open(byte[] package) =>
        SyncPackage.OpenSigned(package, _key ?? throw new InvalidOperationException("Sin clave de sincronización."));

    public void Dispose() => _key?.Dispose();
}

public static class SyncInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddRequestHandlersFrom(typeof(ISyncRepository).Assembly);
        services.AddSingleton(sp => new SyncCloudOptions { PrivateKeyPath = sp.GetRequiredService<IConfiguration>()[$"{SyncCloudOptions.SectionName}:PrivateKeyPath"] });
        services.AddSingleton<ISyncPackageOpener, SyncPackageOpener>();
        services.AddSingleton(sp => new SyncUploadThrottle(
            sp.GetRequiredService<Pos.SharedKernel.Time.IClock>(),
            int.TryParse(sp.GetRequiredService<IConfiguration>()[$"{SyncCloudOptions.SectionName}:MaxUploadsPer10Minutes"], out var max)
                ? max : SyncUploadThrottle.DefaultMaxUploads));
        services.AddScoped<ISyncRepository, SyncRepository>();
        services.AddScoped<ISyncAccess, SyncAccess>();
    }
}
