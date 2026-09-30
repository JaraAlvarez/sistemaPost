using System.Security.Cryptography;
using Pos.Cloud.PortalIdentity.Domain;

namespace Pos.Cloud.PortalIdentity.Application;

/// <summary>Parámetros del acceso al portal (⚙️ sección <c>Portal</c> de la configuración).</summary>
public sealed class PortalIdentityOptions
{
    public const string SectionName = "Portal";

    public int MaxFailedAttempts { get; set; } = LoginPolicy.Default.MaxFailedAttempts;

    public int LockoutMinutes { get; set; } = (int)LoginPolicy.Default.LockoutDuration.TotalMinutes;

    /// <summary>Tiempo para completar el segundo factor tras la contraseña.</summary>
    public int PendingMinutes { get; set; } = (int)SessionPolicy.Default.PendingLifetime.TotalMinutes;

    public int SessionHours { get; set; } = (int)SessionPolicy.Default.AbsoluteLifetime.TotalHours;

    public int IdleMinutes { get; set; } = (int)SessionPolicy.Default.IdleTimeout.TotalMinutes;

    /// <summary>
    /// ¿El doble factor (TOTP) es obligatorio para entrar con contraseña? Por defecto NO (ADR-0062): la contraseña basta, salvo
    /// para quien activó su TOTP en "Mi cuenta", a quien siempre se le pide. <c>true</c> = comportamiento de la Fase 12-A (todos
    /// deben enrolar su autenticador). El ingreso con Google nunca pide el TOTP.
    /// </summary>
    public bool RequireTotp { get; set; }

    /// <summary>Nombre que muestra la aplicación autenticadora del teléfono.</summary>
    public string TotpIssuer { get; set; } = "POS Licencias";

    public LoginPolicy Login => new(Math.Max(1, MaxFailedAttempts), TimeSpan.FromMinutes(Math.Max(1, LockoutMinutes)));

    public SessionPolicy Sessions => new(
        TimeSpan.FromMinutes(Math.Max(1, PendingMinutes)), TimeSpan.FromHours(Math.Max(1, SessionHours)), TimeSpan.FromMinutes(Math.Max(1, IdleMinutes)));
}

/// <summary>Persistencia de usuarios y sesiones del portal.</summary>
public interface IPortalIdentityStore
{
    void Add(PortalUser user);

    void Add(PortalSession session);

    Task<PortalUser?> GetUserAsync(Guid id, CancellationToken cancellationToken);

    Task<PortalUser?> FindUserByEmailAsync(string email, CancellationToken cancellationToken);

    Task<IReadOnlyList<PortalUser>> ListUsersAsync(CancellationToken cancellationToken);

    Task<bool> AnyActiveSuperadminAsync(Guid? except, CancellationToken cancellationToken);

    Task<PortalSession?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<PortalSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Sesiones no revocadas del usuario (para revocarlas al cambiar su seguridad).</summary>
    Task<IReadOnlyList<PortalSession>> GetOpenSessionsAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PortalSession>> ListSessionsAsync(Guid? userId, int limit, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Cifrado del secreto TOTP en reposo (las llaves de cifrado están fuera de la BD).</summary>
public interface ITotpSecretProtector
{
    string Protect(byte[] secret);

    /// <summary>Descifra; <c>null</c> si no se puede (llaves perdidas o dato alterado).</summary>
    byte[]? Unprotect(string protectedSecret);
}

/// <summary>Contraseñas temporales (se muestran una sola vez): 16 caracteres sin símbolos ambiguos, con letras y dígitos.</summary>
public static class TemporaryPasswords
{
    private const string Letters = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";

    public static string Create()
    {
        const string all = Letters + Digits;
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        }

        chars[RandomNumberGenerator.GetInt32(8)] = Letters[RandomNumberGenerator.GetInt32(Letters.Length)];
        chars[8 + RandomNumberGenerator.GetInt32(8)] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        return new string(chars);
    }
}

/// <summary>
/// Usuario en curso. <see cref="MustChangePassword"/> es el efectivo de la sesión (quien entró con Google no está obligado);
/// <see cref="HasTemporaryPassword"/> dice si la contraseña guardada sigue siendo la temporal.
/// </summary>
public sealed record PortalMeDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool MustChangePassword,
    IReadOnlyCollection<string> Permissions,
    bool TotpEnabled = false,
    bool HasTemporaryPassword = false,
    string AuthMethod = "PASSWORD");

/// <summary>
/// Resultado de la contraseña correcta: token de la sesión y qué falta: <c>TOTP_REQUIRED</c>, <c>ENROLLMENT_REQUIRED</c> o
/// <c>ACTIVE</c> (nada: el TOTP no es obligatorio y el usuario no lo tiene activo; el token ya es el de la sesión).
/// </summary>
public sealed record LoginChallengeDto(string Token, string Stage, DateTimeOffset ExpiresAt);

public sealed record TotpEnrollmentDto(string Secret, string EnrollmentLink);

/// <summary>Sesión activa: el token se muestra una sola vez (cookie o Bearer).</summary>
public sealed record PortalSessionDto(string Token, Guid SessionId, DateTimeOffset ExpiresAt, PortalMeDto User);

public sealed record PortalUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Kind,
    string Role,
    Guid? ResellerAccountId,
    string Status,
    bool TotpEnabled,
    bool MustChangePassword,
    DateTimeOffset? LockedUntil,
    DateTimeOffset? LastLoginAt);

public sealed record PortalSessionInfoDto(
    Guid Id,
    Guid UserId,
    string Stage,
    string Channel,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason,
    string? IpAddress,
    string? UserAgent,
    string AuthMethod = "PASSWORD");

/// <summary>Usuario creado o contraseña restablecida: la temporal se muestra una sola vez.</summary>
public sealed record TemporaryPasswordDto(Guid UserId, string Email, string TemporaryPassword);

/// <summary>Identidad resuelta de un token de sesión (cookie o Bearer).</summary>
public sealed record AuthenticatedPortalSession(
    Guid SessionId,
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    SessionStage Stage,
    bool MustChangePassword,
    IReadOnlyCollection<string> Permissions);
