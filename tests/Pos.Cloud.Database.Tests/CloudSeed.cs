using System.Globalization;
using System.Security.Cryptography;
using Pos.Cloud.Abstractions;
using Pos.SharedKernel.Fiscal;

namespace Pos.Cloud.Database.Tests;

/// <summary>Datos mínimos de licencias insertados por SQL con el rol de la aplicación (pos_app).</summary>
public static class CloudSeed
{
    public static readonly Guid SystemUser = SystemActor.Id;

    /// <summary>NIT aleatorio de 9 dígitos con su dígito de verificación correcto.</summary>
    public static (string Nit, string Dv) NewNit()
    {
        var number = RandomNumberGenerator.GetInt32(800_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture);
        return (number, Nit.ComputeCheckDigit(number).ToString(CultureInfo.InvariantCulture));
    }

    public static string NewKeyHash() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string Account(Guid id) => $"""
        INSERT INTO licensing.accounts (id, name, kind, status, created_at, created_by)
        VALUES ('{id}', 'Cliente {id:N}', 'DIRECT', 'ACTIVE', now(), '{SystemUser}');
        """;

    public static string Organization(Guid id, Guid accountId, string? nit = null)
    {
        var (number, dv) = nit is null ? NewNit() : (nit, Nit.ComputeCheckDigit(nit).ToString(CultureInfo.InvariantCulture));
        return $"""
            INSERT INTO licensing.organizations (id, account_id, legal_name, nit, nit_check_digit, status, created_at, created_by)
            VALUES ('{id}', '{accountId}', 'Empresa {id:N}', '{number}', '{dv}', 'ACTIVE', now(), '{SystemUser}');
            """;
    }

    public static string Subscription(Guid id, Guid organizationId, string status = "TRIAL") => $"""
        INSERT INTO licensing.subscriptions (id, organization_id, edition, billing_period, status, trial_ends_at, grace_days,
            suspended_reason, cancelled_at, created_at, created_by)
        VALUES ('{id}', '{organizationId}', 'MULTI', 'MONTHLY', '{status}', now() + interval '30 days', 7,
            {(status == "SUSPENDED" ? "'Falta de pago'" : "NULL")}, {(status == "CANCELLED" ? "now()" : "NULL")}, now(), '{SystemUser}');
        """;

    public static string License(Guid id, Guid organizationId, Guid subscriptionId, string? keyHash = null, string status = "ACTIVE") => $"""
        INSERT INTO licensing.licenses (id, organization_id, subscription_id, key_hash, key_prefix, status, issued_at, revoked_at,
            revoked_reason, created_at, created_by)
        VALUES ('{id}', '{organizationId}', '{subscriptionId}', '{keyHash ?? NewKeyHash()}', 'POS-ABCDE', '{status}', now(),
            {(status == "REVOKED" ? "now(), 'Revocada en la prueba'" : "NULL, NULL")}, now(), '{SystemUser}');
        """;

    public static string Installation(Guid id, Guid licenseId, Guid organizationId) => $"""
        INSERT INTO licensing.installations (id, installation_id, license_id, organization_id, app_version, status,
            first_activated_at, created_at, created_by)
        VALUES ('{id}', gen_random_uuid(), '{licenseId}', '{organizationId}', '1.0.0', 'ACTIVE', now(), now(), '{SystemUser}');
        """;

    public static string Device(Guid id, Guid installationId) => $"""
        INSERT INTO licensing.devices (id, installation_id, fingerprint, role, first_seen_at, last_seen_at)
        VALUES ('{id}', '{installationId}', 'fp1.{Hex()}.{Hex()}.-', 'STORE_SERVER', now(), now());
        """;

    public static string Activation(Guid id, Guid installationId, Guid licenseId, Guid deviceId) => $"""
        INSERT INTO licensing.activations (id, installation_id, license_id, device_id, status, activated_at)
        VALUES ('{id}', '{installationId}', '{licenseId}', '{deviceId}', 'ACTIVE', now());
        """;

    public static string Checkin(Guid installationId) => $"""
        INSERT INTO licensing.checkins (id, installation_id, occurred_at, app_version, active_terminals, reported_clock, result,
            subscription_status, token_valid_until)
        VALUES (gen_random_uuid(), '{installationId}', now(), '1.0.0', 2, now(), 'TOKEN_ISSUED', 'TRIAL', now() + interval '30 days');
        """;

    /// <summary>Cuenta → empresa → suscripción → licencia → instalación → equipo → activación, con sus ids.</summary>
    public static (string Sql, SeededLicense Ids) FullChain()
    {
        var ids = new SeededLicense(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7());
        var sql = string.Concat(
            Account(ids.Account),
            Organization(ids.Organization, ids.Account),
            Subscription(ids.Subscription, ids.Organization),
            License(ids.License, ids.Organization, ids.Subscription),
            Installation(ids.Installation, ids.License, ids.Organization),
            Device(ids.Device, ids.Installation),
            Activation(ids.Activation, ids.Installation, ids.License, ids.Device));
        return (sql, ids);
    }

    private static string Hex() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

public sealed record SeededLicense(
    Guid Account, Guid Organization, Guid Subscription, Guid License, Guid Installation, Guid Device, Guid Activation);
