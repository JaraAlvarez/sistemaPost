using System.Net;

namespace Pos.Application.Abstractions.Security;

/// <summary>Tipo de secreto: define los parámetros de Argon2id (docs/fases/fase-03-propuesta.md §7).</summary>
public enum SecretKind
{
    /// <summary>Contraseña de backoffice: 64 MiB, 3 pasadas.</summary>
    Password,

    /// <summary>PIN de caja: 19 MiB, 2 pasadas (lo protegen además el bloqueo y el emparejamiento).</summary>
    Pin,
}

public enum SecretVerification
{
    Failed,
    Succeeded,

    /// <summary>Correcto, pero el hash usa parámetros antiguos: hay que recalcularlo (D3-02).</summary>
    SucceededRehashNeeded,
}

/// <summary>Hash de contraseñas y PIN con Argon2id en formato PHC (los parámetros viajan en el propio hash).</summary>
public interface ISecretHasher
{
    Task<string> HashAsync(string secret, SecretKind kind, CancellationToken cancellationToken = default);

    Task<SecretVerification> VerifyAsync(string secret, string phcHash, SecretKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Tipo de equipo emparejado.</summary>
public enum DeviceKind
{
    Terminal,
    AdminWorkstation,
}

/// <summary>
/// Desde dónde llega la petición: el propio servidor (localhost) o un equipo emparejado y autenticado
/// (D3-04). Lo completa el middleware de autenticación del host.
/// </summary>
public interface IClientContext
{
    /// <summary>La petición viene del propio servidor.</summary>
    bool IsLocal { get; }

    Guid? DeviceId { get; }

    DeviceKind? DeviceKind { get; }

    /// <summary>Caja asociada al equipo (equipos tipo caja) o, en Caja Única, la única caja.</summary>
    Guid? PosTerminalId { get; }

    IPAddress? IpAddress { get; }

    string? UserAgent { get; }
}

/// <summary>Identidad TLS del servidor: la huella SHA-256 del certificado que los equipos fijan al emparejarse (D3-05).</summary>
public interface IServerIdentity
{
    /// <summary><c>null</c> en la edición Caja Única (solo HTTP local).</summary>
    string? CertificateFingerprint { get; }
}

/// <summary>Resultado de consumir una autorización de supervisor.</summary>
public sealed record ConsumedAuthorization(Guid GrantId, Guid AuthorizedBy);

/// <summary>Autorización de supervisor de un solo uso (D3-07, RN-GEN-05).</summary>
public interface ISupervisorAuthorization
{
    /// <summary>
    /// Consume la autorización si corresponde exactamente a este permiso, acción, objetivo, usuario y caja, y no ha
    /// vencido ni se ha usado. Es atómico: dos peticiones no pueden usar la misma autorización.
    /// </summary>
    Task<ConsumedAuthorization?> TryConsumeAsync(
        Guid grantId, string permissionCode, string action, Guid? targetId, CancellationToken cancellationToken = default);
}

/// <summary>Autorización de supervisor aplicada a la petición en curso (la auditoría la registra en authorized_by).</summary>
public interface IAuthorizationScope
{
    ConsumedAuthorization? Current { get; }

    void Use(ConsumedAuthorization authorization);
}
