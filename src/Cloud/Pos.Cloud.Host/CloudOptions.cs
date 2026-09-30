namespace Pos.Cloud.Host;

/// <summary>Configuración del servidor de la nube (sección <c>Cloud</c>; en el contenedor, variables <c>Cloud__…</c>).</summary>
internal sealed class CloudOptions
{
    public const string SectionName = "Cloud";

    public CloudDatabaseOptions Database { get; set; } = new();

    /// <summary>
    /// Directorio de las llaves de la protección de datos (cifran el secreto TOTP y los tokens antifalsificación). Debe estar en
    /// un volumen persistente y respaldado aparte de la BD. Vacío = en memoria (solo desarrollo y pruebas).
    /// </summary>
    public string? DataProtectionKeysPath { get; set; }

    /// <summary>Aceptar X-Forwarded-For/Proto del proxy (Caddy). Solo si el puerto de la aplicación NO está expuesto a internet.</summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Ruta base cuando el servidor vive bajo una ruta de un dominio existente (p. ej. <c>/businesspost</c> en
    /// <c>https://tutiendanueva.com/businesspost/</c>). Vacío = raíz de un (sub)dominio propio. El proxy debe reenviar la ruta
    /// COMPLETA (sin quitar el prefijo); ver docs/despliegue-nube.md.
    /// </summary>
    public string? PathBase { get; set; }

    /// <summary><see cref="PathBase"/> normalizada: <c>/businesspost</c> (con barra inicial y sin barra final) o vacía.</summary>
    public PathString NormalizedPathBase =>
        PathBase?.Trim().Trim('/') is { Length: > 0 } path ? new PathString("/" + path) : PathString.Empty;

    public CloudSecurityOptions Security { get; set; } = new();

    /// <summary>Sellado de la auditoría en segundo plano (las pruebas pueden desactivarlo).</summary>
    public bool AuditSealing { get; set; } = true;

    /// <summary>Cada cuánto se actualizan los estados de las suscripciones por fecha.</summary>
    public int SubscriptionRefreshMinutes { get; set; } = 60;
}

internal sealed class CloudDatabaseOptions
{
    /// <summary>Cadena del rol <c>pos_app</c> (la aplicación).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Cadena del rol <c>pos_migrator</c>; solo si <see cref="MigrateOnStartup"/>.</summary>
    public string? MigratorConnectionString { get; set; }

    public bool MigrateOnStartup { get; set; }
}

internal sealed class CloudSecurityOptions
{
    /// <summary>Peticiones por minuto y por IP a la API del POS (/v1).</summary>
    public int PosApiPermitsPerMinute { get; set; } = 60;

    /// <summary>Intentos de acceso por minuto y por IP (formularios y /admin/auth).</summary>
    public int LoginPermitsPerMinute { get; set; } = 10;
}
