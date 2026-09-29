using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.Licensing.Application;

/// <summary>Parámetros del servidor de licencias (⚙️ sección <c>Licensing</c> de la configuración).</summary>
public sealed class LicensingOptions
{
    public const string SectionName = "Licensing";

    /// <summary>Días de prueba por defecto (resolución 5: 30).</summary>
    public int TrialDays { get; set; } = 30;

    /// <summary>Días de gracia por defecto tras el fin del periodo (resolución 5: 7).</summary>
    public int GraceDays { get; set; } = 7;

    /// <summary>El POS intenta renovar el token pasado este tiempo (doc 09: 24 h).</summary>
    public int RefreshAfterHours { get; set; } = 24;

    /// <summary>Días antes del vencimiento en que el token lleva el aviso para el usuario.</summary>
    public int ExpiryWarningDays { get; set; } = 7;

    /// <summary>Tablero: instalaciones activas sin check-in hace más de N días.</summary>
    public int NoCheckinAlertDays { get; set; } = 3;

    /// <summary>Última versión publicada del POS (si el POS reporta una menor, el token lleva el aviso de actualización).</summary>
    public string? LatestPosVersion { get; set; }

    /// <summary>Solicitudes de activación, check-in o liberación por licencia y por hora (además del límite por IP).</summary>
    public int RequestsPerLicensePerHour { get; set; } = 120;

    /// <summary>Clave privada de firma.</summary>
    public SigningOptions Signing { get; set; } = new();
}

public sealed class SigningOptions
{
    /// <summary>Ruta del archivo PEM con la clave privada Ed25519 ACTIVA (fuera de la BD y del repositorio; permisos 600).</summary>
    public string? PrivateKeyPath { get; set; }
}

/// <summary>Persistencia de las entidades de licencias (EF Core).</summary>
public interface ILicensingStore
{
    void Add(Account account);

    void Add(Organization organization);

    void Add(Subscription subscription);

    void Add(License license);

    void Add(Installation installation);

    void Add(Checkin checkin);

    void Add(SigningKey key);

    Task<Account?> GetAccountAsync(Guid id, CancellationToken cancellationToken);

    Task<Organization?> GetOrganizationAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> NitExistsAsync(string nit, CancellationToken cancellationToken);

    Task<Subscription?> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>La suscripción no cancelada de la empresa (hay como máximo una).</summary>
    Task<Subscription?> GetCurrentSubscriptionAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Subscription>> GetSubscriptionsToRefreshAsync(CancellationToken cancellationToken);

    Task<License?> GetLicenseAsync(Guid id, CancellationToken cancellationToken);

    Task<License?> GetActiveLicenseAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Licencias (vigentes o no) cuyo prefijo visible coincide; la clave se compara después en tiempo constante.</summary>
    Task<IReadOnlyList<License>> FindLicensesByPrefixAsync(string keyPrefix, CancellationToken cancellationToken);

    /// <summary>Bloquea la fila de la licencia hasta el fin de la transacción (el conteo de instalaciones no se adelanta).</summary>
    Task LockLicenseAsync(Guid licenseId, CancellationToken cancellationToken);

    /// <summary>Instalación por el <c>installation_id</c> del POS, con sus equipos y activaciones.</summary>
    Task<Installation?> FindInstallationAsync(Guid posInstallationId, CancellationToken cancellationToken);

    Task<Installation?> FindInstallationByActivationAsync(Guid activationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Installation>> GetInstallationsOfLicenseAsync(Guid licenseId, CancellationToken cancellationToken);

    Task<int> CountActiveInstallationsAsync(Guid licenseId, CancellationToken cancellationToken);

    Task<bool> HasActiveStoreServersAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SigningKey>> GetSigningKeysAsync(CancellationToken cancellationToken);

    /// <summary>Guarda lo pendiente dentro de la transacción en curso (p. ej. revocar antes de insertar la licencia nueva).</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Firma de tokens con la clave privada del archivo configurado. Si el archivo falta o su clave fue revocada, el servidor sigue
/// atendiendo el portal pero no emite tokens (<see cref="LicenseApiErrors.SigningUnavailable"/>).
/// </summary>
public interface ILicenseTokenSigner
{
    bool IsAvailable { get; }

    /// <summary>Clave pública de la clave privada cargada (aunque aún no esté registrada como activa).</summary>
    LicensePublicKey? PublicKey { get; }

    string Sign(LicenseClaims claims);

    /// <summary>Habilita o deshabilita la firma según el registro de claves (lo decide el arranque).</summary>
    void SetEnabled(bool enabled);
}

/// <summary>Claves públicas de confianza (todas salvo las revocadas), con caché corta.</summary>
public interface ITrustedSigningKeys
{
    Task<LicenseKeyRing> GetAsync(CancellationToken cancellationToken);

    void Invalidate();
}

/// <summary>Límite de solicitudes por licencia (complementa el límite por IP del host).</summary>
public interface ILicenseThrottle
{
    bool TryAcquire(Guid licenseId);
}
