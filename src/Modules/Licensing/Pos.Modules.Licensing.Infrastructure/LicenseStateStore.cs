using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;

namespace Pos.Modules.Licensing.Infrastructure;

/// <summary>
/// SQL directo sobre el esquema <c>licensing</c>, en la conexión y transacción del contexto EF de la petición. El estado sensible va
/// firmado (<c>state_mac</c>, H4) con el secreto de <see cref="LicenseVault"/>: cada escritura vuelve a firmar SOLO si lo guardado
/// cuadraba antes (así una alteración no queda "bendecida" por el recálculo de cada minuto), salvo las que traen la confirmación de la
/// nube (token nuevo, equipo liberado).
/// </summary>
internal sealed class LicenseStateStore(PosDbContext context, IInstallationContext installation, LicenseVault vault) : ILicenseStateStore
{
    private const string SealedColumns =
        """
        SELECT node_id AS NodeId, token AS Token, revoked AS Revoked, reactivation_required AS ReactivationRequired, deactivated AS Deactivated,
               clock_offset_seconds AS ClockOffsetSeconds, max_observed_utc AS MaxObservedUtc, last_checkin_at AS LastCheckinAt,
               restricted_since AS RestrictedSince, state_mac AS StateMac
        FROM licensing.license_state WHERE node_id = @node
        """;

    public async Task<LicenseRecord?> GetAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            """
            SELECT s.node_id AS NodeId, s.token AS Token, s.license_key_prefix AS LicenseKeyPrefix, s.activated_at AS ActivatedAt,
                   s.last_checkin_at AS LastCheckinAt, s.last_checkin_error AS LastCheckinError, s.revoked AS Revoked,
                   s.reactivation_required AS ReactivationRequired, s.deactivated AS Deactivated, s.clock_offset_seconds AS ClockOffsetSeconds,
                   s.max_observed_utc AS MaxObservedUtc, i.created_at AS DemoStartedAt, s.payment_url AS PaymentUrl,
                   s.restricted_since AS RestrictedSince, s.state_mac AS StateMac,
                   (SELECT c.identification_number || COALESCE('-' || c.check_digit, '') FROM org.companies c WHERE c.id = @company) AS CompanyNit
            FROM licensing.license_state s
            CROSS JOIN system.installation i
            WHERE s.node_id = @node
            """,
            new { node = installation.NodeId, company = installation.CompanyId }, transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        // Restaurar un backup viejo o editar la BD no retrocede la marca: vale la mayor entre la BD y el archivo protegido.
        var maxObserved = Utc(row.MaxObservedUtc);
        if (vault.Watermark is { } watermark && watermark > maxObserved)
        {
            maxObserved = watermark;
        }

        return new LicenseRecord(
            row.NodeId, row.Token, row.LicenseKeyPrefix, Utc(row.ActivatedAt), Utc(row.LastCheckinAt), row.LastCheckinError, row.Revoked,
            row.ReactivationRequired, row.Deactivated, row.ClockOffsetSeconds, maxObserved, Utc(row.DemoStartedAt), row.PaymentUrl,
            row.CompanyNit, !Intact(new SealedRow(
                row.NodeId, row.Token, row.Revoked, row.ReactivationRequired, row.Deactivated, row.ClockOffsetSeconds, row.MaxObservedUtc, row.LastCheckinAt,
                row.RestrictedSince, row.StateMac)),
            Utc(row.RestrictedSince));
    }

    /// <summary>Crea la fila del nodo si no existe (al arrancar). La mayor hora observada empieza en la creación de la instalación.</summary>
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var created = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO licensing.license_state (node_id, max_observed_utc, created_at, updated_at)
            SELECT @node, i.created_at, now(), now() FROM system.installation i
            ON CONFLICT (node_id) DO NOTHING
            """,
            new { node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
        if (created > 0)
        {
            await SealAsync(connection, transaction, cancellationToken);
        }
    }

    public async Task<string?> GetLastStateAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT last_state FROM licensing.license_state WHERE node_id = @node", new { node = installation.NodeId }, transaction,
            cancellationToken: cancellationToken));
    }

    public Task SetLastStateAsync(string state, DateTimeOffset? restrictedSince, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE licensing.license_state SET last_state = @state, restricted_since = @restrictedSince, updated_at = now() WHERE node_id = @node",
            new { state, restrictedSince, node = installation.NodeId }, trusted: false, cancellationToken);

    public async Task SaveTokenAsync(
        string token, string? keyPrefix, DateTimeOffset at, bool activation, long clockOffsetSeconds, string? paymentUrl, DateTimeOffset trustedNow,
        CancellationToken cancellationToken)
    {
        // R2: la hora confiable de la nube REEMPLAZA la marca (también en el archivo), aunque sea menor.
        await ExecuteAsync(
            """
            UPDATE licensing.license_state SET token = @token, license_key_prefix = COALESCE(@keyPrefix, license_key_prefix),
                activated_at = CASE WHEN @activation THEN @at ELSE activated_at END, last_checkin_at = @at, last_checkin_error = NULL,
                revoked = false, reactivation_required = false, deactivated = false, clock_offset_seconds = @clockOffsetSeconds,
                max_observed_utc = @trustedNow, payment_url = @paymentUrl, updated_at = now()
            WHERE node_id = @node
            """,
            new { token, keyPrefix, at, activation, clockOffsetSeconds, trustedNow, paymentUrl, node = installation.NodeId }, trusted: true, cancellationToken);
        vault.Reset(trustedNow);
    }

    public Task SaveCheckinErrorAsync(string error, bool? revoked, bool? reactivationRequired, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            UPDATE licensing.license_state SET last_checkin_error = left(@error, 500), revoked = COALESCE(@revoked, revoked),
                reactivation_required = COALESCE(@reactivationRequired, reactivation_required), updated_at = now()
            WHERE node_id = @node
            """,
            new { error, revoked, reactivationRequired, node = installation.NodeId }, trusted: false, cancellationToken);

    public Task ClearTokenAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            UPDATE licensing.license_state SET token = NULL, deactivated = true, revoked = false, reactivation_required = false,
                last_checkin_error = NULL, payment_url = NULL, updated_at = now()
            WHERE node_id = @node
            """,
            new { node = installation.NodeId }, trusted: true, cancellationToken);

    public async Task RaiseMaxObservedAsync(DateTimeOffset value, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            "UPDATE licensing.license_state SET max_observed_utc = greatest(max_observed_utc, @value) WHERE node_id = @node",
            new { value, node = installation.NodeId }, trusted: false, cancellationToken);
        vault.Raise(value);
    }

    public Task RecordCheckinAsync(LicenseCheckinDto checkin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkin);
        return ExecuteAsync(
            """
            INSERT INTO licensing.checkins (id, node_id, occurred_at, kind, succeeded, error_code, subscription_status, duration_ms)
            VALUES (@id, @node, @OccurredAt, @Kind, @Succeeded, @ErrorCode, @SubscriptionStatus, @DurationMs)
            """,
            new
            {
                id = Guid.CreateVersion7(), node = installation.NodeId, checkin.OccurredAt, checkin.Kind, checkin.Succeeded, checkin.ErrorCode,
                checkin.SubscriptionStatus, checkin.DurationMs,
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<LicenseCheckinDto>> ListCheckinsAsync(int limit, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<CheckinRow>(new CommandDefinition(
            """
            SELECT occurred_at AS OccurredAt, kind AS Kind, succeeded AS Succeeded, error_code AS ErrorCode, subscription_status AS SubscriptionStatus,
                   duration_ms AS DurationMs
            FROM licensing.checkins WHERE node_id = @node ORDER BY occurred_at DESC LIMIT @limit
            """,
            new { node = installation.NodeId, limit }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new LicenseCheckinDto(Utc(r.OccurredAt), r.Kind, r.Succeeded, r.ErrorCode, r.SubscriptionStatus, r.DurationMs))];
    }

    public async Task<LicenseSiteInfo?> SiteAsync(CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return null;
        }

        var (connection, transaction) = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SiteRow>(new CommandDefinition(
            """
            SELECT c.identification_number AS Number, c.check_digit AS CheckDigit, c.legal_name AS LegalName, b.name AS BranchName,
                   (SELECT count(*)::int FROM cash.cash_sessions s WHERE s.status <> 'CLOSED') AS OpenSessions,
                   c.business_profile AS BusinessProfile
            FROM org.companies c
            LEFT JOIN org.branches b ON b.id = @branch
            WHERE c.id = @company
            """,
            new { company = companyId, branch = installation.BranchId }, transaction, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new LicenseSiteInfo(row.CheckDigit is null ? row.Number : $"{row.Number}-{row.CheckDigit}", row.LegalName, row.BranchName, row.OpenSessions, row.BusinessProfile);
    }

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Escribe y vuelve a firmar el estado. <paramref name="trusted"/>: la escritura viene confirmada por la nube y firma aunque lo
    /// anterior estuviera alterado; si no, solo firma si lo guardado cuadraba antes (una alteración sigue detectándose).
    /// </summary>
    private async Task ExecuteAsync(string sql, object parameters, bool trusted, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var intact = trusted || await ReadSealedAsync(connection, transaction, cancellationToken) is not { } before || Intact(before);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
        if (intact)
        {
            await SealAsync(connection, transaction, cancellationToken);
        }
    }

    private async Task SealAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (await ReadSealedAsync(connection, transaction, cancellationToken) is not { } row)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE licensing.license_state SET state_mac = @mac WHERE node_id = @node",
            new { mac = vault.Mac(Canonical(row)), node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
        vault.MarkSealed();
    }

    private Task<SealedRow?> ReadSealedAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<SealedRow?>(new CommandDefinition(
            SealedColumns, new { node = installation.NodeId }, transaction, cancellationToken: cancellationToken));

    /// <summary>
    /// ¿Cuadra la firma? Una fila sin firma solo se acepta mientras el archivo no haya sellado nada (instalaciones anteriores a la
    /// firma: se adopta en la siguiente escritura).
    /// </summary>
    private bool Intact(SealedRow row) =>
        row.StateMac is null
            ? !vault.Sealed
            : CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.StateMac), Encoding.ASCII.GetBytes(vault.Mac(Canonical(row))));

    /// <summary>Texto canónico de lo firmado (las horas en microsegundos, la precisión de PostgreSQL).</summary>
    private static string Canonical(SealedRow row) =>
        string.Join(
            '|',
            "v1",
            row.NodeId.ToString("N"),
            row.Token is null ? "-" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(row.Token))),
            row.Revoked ? "1" : "0",
            row.ReactivationRequired ? "1" : "0",
            row.Deactivated ? "1" : "0",
            row.ClockOffsetSeconds.ToString(CultureInfo.InvariantCulture),
            Micros(row.MaxObservedUtc),
            row.LastCheckinAt is { } last ? Micros(last) : "-",
            row.RestrictedSince is { } since ? Micros(since) : "-");

    private static string Micros(DateTime value) => (DateTime.SpecifyKind(value, DateTimeKind.Utc).Ticks / 10).ToString(CultureInfo.InvariantCulture);

    private async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private sealed record StateRow(
        Guid NodeId, string? Token, string? LicenseKeyPrefix, DateTime? ActivatedAt, DateTime? LastCheckinAt, string? LastCheckinError, bool Revoked,
        bool ReactivationRequired, bool Deactivated, long ClockOffsetSeconds, DateTime MaxObservedUtc, DateTime DemoStartedAt, string? PaymentUrl,
        DateTime? RestrictedSince, string? StateMac, string? CompanyNit);

    private sealed record SealedRow(
        Guid NodeId, string? Token, bool Revoked, bool ReactivationRequired, bool Deactivated, long ClockOffsetSeconds, DateTime MaxObservedUtc,
        DateTime? LastCheckinAt, DateTime? RestrictedSince, string? StateMac);

    private sealed record CheckinRow(DateTime OccurredAt, string Kind, bool Succeeded, string? ErrorCode, string? SubscriptionStatus, int DurationMs);

    private sealed record SiteRow(string Number, string? CheckDigit, string LegalName, string? BranchName, int OpenSessions, string? BusinessProfile);
}
