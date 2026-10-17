using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Licensing.Domain;

/// <summary>Estado guardado de una solicitud de "Continuar con Google" del instalador (en la BD: MAYÚSCULAS). "Vencida" se calcula.</summary>
public enum InstallerSignInStatus
{
    /// <summary>Esperando que el dueño la autorice en el navegador.</summary>
    Pending,

    /// <summary>Autorizada por un usuario CLIENTE de la cuenta dueña; el instalador aún no recoge el enlace.</summary>
    Approved,

    /// <summary>El dueño la canceló en el navegador.</summary>
    Denied,

    /// <summary>El instalador ya recibió el enlace firmado (un solo uso).</summary>
    Consumed,
}

/// <summary>Resultado de registrar una consulta del instalador.</summary>
public enum InstallerSignInPollCheck
{
    Accepted,

    /// <summary>Consultó antes del intervalo: se alarga el intervalo (como <c>slow_down</c> de OAuth 2.0 Device Authorization).</summary>
    SlowDown,
}

/// <summary>Resultado de revisar el código que el dueño escribió en el portal.</summary>
public enum InstallerSignInCodeCheck
{
    Matches,

    /// <summary>No coincide; quedan intentos.</summary>
    Mismatch,

    /// <summary>No coincide y se acabaron los intentos: la solicitud quedó cancelada.</summary>
    Exhausted,

    /// <summary>La solicitud ya no está pendiente (vencida, decidida o usada).</summary>
    Closed,
}

/// <summary>Reglas de la solicitud (Fase 13-B, parte E pendiente: "Continuar con Google").</summary>
public static class InstallerSignInRules
{
    /// <summary>
    /// Vigencia de la solicitud: alcanza para entrar con Google, escribir el código y autorizar, y es corta a propósito (un enlace
    /// enviado por otra persona para "pescar" la autorización deja de servir enseguida).
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Intentos con un código escrito incorrecto antes de cancelar la solicitud (el dueño vuelve a empezar en el instalador).</summary>
    public const int MaxCodeAttempts = 5;

    /// <summary>Segundos mínimos entre consultas del instalador (OAuth recomienda 5).</summary>
    public const int IntervalSeconds = 5;

    /// <summary>Cada consulta adelantada suma estos segundos al intervalo, hasta <see cref="MaxIntervalSeconds"/>.</summary>
    public const int SlowDownStepSeconds = 5;

    public const int MaxIntervalSeconds = 30;

    /// <summary>Tolerancia de reloj y de red al comparar el intervalo (un temporizador de 5 s puede llegar unos milisegundos antes).</summary>
    public static readonly TimeSpan PollTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Solicitudes abiertas (pendientes o autorizadas, sin vencer) por licencia: evita llenar la tabla con una clave filtrada.</summary>
    public const int MaxOpenPerLicense = 5;

    /// <summary>Letras y números sin los que se confunden (0/O, 1/I/L, 5/S, U/V): el dueño compara el código a simple vista.</summary>
    private const string UserCodeAlphabet = "ABCDEFGHJKMNPQRTWXYZ2346789";

    /// <summary>Secreto de consulta: 256 bits aleatorios en base64url (43 caracteres). Solo lo conoce el instalador.</summary>
    public static string NewPollToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 en hexadecimal (minúsculas) del secreto de consulta: lo único que se guarda.</summary>
    public static string HashPollToken(string pollToken)
    {
        ArgumentNullException.ThrowIfNull(pollToken);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pollToken)));
    }

    public static bool IsPollToken(string? token) =>
        token is { Length: 43 } && token.AsSpan().ContainsOnlyBase64Url();

    /// <summary>Código corto para comparar en el instalador y en el portal: <c>XXXX-XXXX</c>.</summary>
    public static string NewUserCode()
    {
        Span<char> code = stackalloc char[9];
        for (var i = 0; i < code.Length; i++)
        {
            code[i] = i == 4 ? '-' : UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        }

        return new string(code);
    }

    /// <summary>
    /// Código ESCRITO por el dueño en el portal, normalizado: mayúsculas, sin espacios ni guiones y con el guion en su lugar
    /// (<c>k7qf m2xp</c> → <c>K7QF-M2XP</c>). <c>null</c> si no tiene 8 letras o números.
    /// </summary>
    public static string? NormalizeUserCode(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed) || typed.Length > 40)
        {
            return null;
        }

        var clean = new string([.. typed.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]);
        return clean.Length == 8 ? $"{clean[..4]}-{clean[4..]}" : null;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool ContainsOnlyBase64Url(this ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// "Continuar con Google" en el instalador premium, al estilo de OAuth 2.0 Device Authorization (RFC 8628): Google no permite su ingreso
/// dentro de un WebView incrustado, así que el instalador crea esta solicitud con la clave, abre el portal en el navegador del sistema y
/// consulta hasta que un usuario CLIENTE de la cuenta dueña de la empresa la autoriza (con su sesión normal del portal: contraseña o
/// Google). Entonces la nube entrega UNA vez el mismo enlace firmado de <c>/v1/owner-links</c>. Vence a los
/// <see cref="InstallerSignInRules.Lifetime"/>; nunca se borra (L-09): queda como rastro junto con la auditoría.
/// </summary>
public sealed class InstallerSignInRequest : Entity<Guid>
{
    /// <summary>Para EF Core.</summary>
    private InstallerSignInRequest(Guid id)
        : base(id)
    {
        PollTokenHash = string.Empty;
        UserCode = string.Empty;
    }

    private InstallerSignInRequest(Guid id, Guid licenseId, Guid organizationId, string pollTokenHash, string userCode, IPAddress? createdIp, DateTimeOffset now)
        : base(id)
    {
        LicenseId = licenseId;
        OrganizationId = organizationId;
        PollTokenHash = pollTokenHash;
        UserCode = userCode;
        Status = InstallerSignInStatus.Pending;
        CreatedAt = now;
        ExpiresAt = now + InstallerSignInRules.Lifetime;
        CreatedIp = createdIp;
        PollIntervalSeconds = InstallerSignInRules.IntervalSeconds;
    }

    public Guid LicenseId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string PollTokenHash { get; private set; }

    public string UserCode { get; private set; }

    public InstallerSignInStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public IPAddress? CreatedIp { get; private set; }

    public int PollIntervalSeconds { get; private set; }

    public DateTimeOffset? LastPolledAt { get; private set; }

    /// <summary>Usuario del portal que autorizó o canceló.</summary>
    public Guid? PortalUserId { get; private set; }

    public string? ApprovedEmail { get; private set; }

    public string? ApprovedDisplayName { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public IPAddress? DecidedIp { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary><c>jti</c> del enlace firmado entregado (auditoría).</summary>
    public Guid? IssuedTokenId { get; private set; }

    /// <summary>Códigos incorrectos escritos en el portal (V2026.10.012).</summary>
    public int CodeAttempts { get; private set; }

    public static InstallerSignInRequest Create(Guid id, Guid licenseId, Guid organizationId, string pollToken, IPAddress? createdIp, DateTimeOffset now) =>
        new(id, licenseId, organizationId, InstallerSignInRules.HashPollToken(pollToken), InstallerSignInRules.NewUserCode(), createdIp, now);

    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>Pendiente y sin vencer: se puede autorizar o cancelar en el portal.</summary>
    public bool IsPendingAt(DateTimeOffset now) => Status == InstallerSignInStatus.Pending && !IsExpiredAt(now);

    /// <summary>Compara el secreto de consulta con el hash guardado en tiempo constante.</summary>
    public bool MatchesPollToken(string? pollToken)
    {
        if (!InstallerSignInRules.IsPollToken(pollToken))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(InstallerSignInRules.HashPollToken(pollToken!)), Encoding.ASCII.GetBytes(PollTokenHash));
    }

    /// <summary>Registra la consulta; si llegó antes del intervalo, lo alarga (y la consulta no se atiende).</summary>
    public InstallerSignInPollCheck RegisterPoll(DateTimeOffset now)
    {
        var early = LastPolledAt is { } last && now - last < TimeSpan.FromSeconds(PollIntervalSeconds) - InstallerSignInRules.PollTolerance;
        LastPolledAt = now;
        if (!early)
        {
            return InstallerSignInPollCheck.Accepted;
        }

        PollIntervalSeconds = Math.Min(InstallerSignInRules.MaxIntervalSeconds, PollIntervalSeconds + InstallerSignInRules.SlowDownStepSeconds);
        return InstallerSignInPollCheck.SlowDown;
    }

    /// <summary>
    /// Revisa el código que el dueño ESCRIBIÓ en el portal (el que muestra su instalador). Así, quien tenga la clave y le envíe al dueño
    /// el enlace de una solicitud suya no consigue la autorización con un solo clic: el dueño tendría que escribir un código que solo
    /// ve el instalador de esa otra persona. Tras <see cref="InstallerSignInRules.MaxCodeAttempts"/> códigos incorrectos la solicitud
    /// queda cancelada.
    /// </summary>
    public InstallerSignInCodeCheck CheckUserCode(string? typed, Guid portalUserId, IPAddress? ip, DateTimeOffset now)
    {
        if (!IsPendingAt(now))
        {
            return InstallerSignInCodeCheck.Closed;
        }

        var normalized = InstallerSignInRules.NormalizeUserCode(typed);
        if (normalized is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes(UserCode)))
        {
            return InstallerSignInCodeCheck.Matches;
        }

        CodeAttempts++;
        if (CodeAttempts < InstallerSignInRules.MaxCodeAttempts)
        {
            return InstallerSignInCodeCheck.Mismatch;
        }

        Deny(portalUserId, ip, now);
        return InstallerSignInCodeCheck.Exhausted;
    }

    /// <summary>Códigos incorrectos que quedan antes de cancelar la solicitud.</summary>
    public int CodeAttemptsLeft => Math.Max(0, InstallerSignInRules.MaxCodeAttempts - CodeAttempts);

    public Result Approve(Guid portalUserId, string email, string displayName, IPAddress? ip, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!IsPendingAt(now))
        {
            return LicenseApiErrors.OwnerLinkRequestClosed;
        }

        Status = InstallerSignInStatus.Approved;
        PortalUserId = portalUserId;
        ApprovedEmail = email;
        ApprovedDisplayName = displayName;
        DecidedAt = now;
        DecidedIp = ip;
        return Result.Success();
    }

    public Result Deny(Guid portalUserId, IPAddress? ip, DateTimeOffset now)
    {
        if (!IsPendingAt(now))
        {
            return LicenseApiErrors.OwnerLinkRequestClosed;
        }

        Status = InstallerSignInStatus.Denied;
        PortalUserId = portalUserId;
        DecidedAt = now;
        DecidedIp = ip;
        return Result.Success();
    }

    /// <summary>El enlace se entregó: la solicitud no vuelve a servir.</summary>
    public void Consume(Guid issuedTokenId, DateTimeOffset now)
    {
        if (Status != InstallerSignInStatus.Approved)
        {
            throw new InvalidOperationException("Solo una solicitud autorizada entrega el enlace.");
        }

        Status = InstallerSignInStatus.Consumed;
        ConsumedAt = now;
        IssuedTokenId = issuedTokenId;
    }
}
