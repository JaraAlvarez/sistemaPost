namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>Ambiente de Factus (D11B-08). Cada empresa elige uno.</summary>
public enum FactusEnvironment
{
    Sandbox,
    Production,
}

/// <summary>
/// Conexión a Factus API v2. Fuente: https://developers.factus.com.co/autenticacion/auth
/// (password grant con <c>client_id</c>, <c>client_secret</c>, <c>username</c> = correo y <c>password</c>).
/// <para>
/// Las credenciales NO vienen de appsettings: se entregan en tiempo de ejecución por <see cref="IFactusOptionsProvider"/>
/// (descifradas con DPAPI desde la configuración del módulo Billing, D11B-08). Nunca se registran en los logs:
/// <see cref="ToString"/> las oculta.
/// </para>
/// </summary>
public sealed record FactusOptions
{
    /// <summary>URL del sandbox (documentada en todos los endpoints de developers.factus.com.co).</summary>
    public static readonly Uri SandboxUrl = new("https://api-sandbox.factus.com.co/");

    /// <summary>URL de producción (documentada en todos los endpoints de developers.factus.com.co).</summary>
    public static readonly Uri ProductionUrl = new("https://api.factus.com.co/");

    public FactusEnvironment Environment { get; init; } = FactusEnvironment.Sandbox;

    /// <summary>URL base explícita (pruebas con el simulado). Si es nula se usa la del <see cref="Environment"/>.</summary>
    public Uri? BaseUrl { get; init; }

    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }

    /// <summary>Correo del usuario de Factus.</summary>
    public required string Username { get; init; }

    public required string Password { get; init; }

    /// <summary>Tiempo máximo por petición. Factus valida una factura en ≈2,5 s (preguntas frecuentes); más ítems tardan más.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Margen para renovar el token antes de su vencimiento declarado (<c>expires_in</c>).</summary>
    public TimeSpan TokenRenewalMargin { get; init; } = TimeSpan.FromMinutes(2);

    public Uri EffectiveBaseUrl
    {
        get
        {
            var url = BaseUrl ?? (Environment == FactusEnvironment.Production ? ProductionUrl : SandboxUrl);
            var text = url.ToString();
            return text.EndsWith('/') ? url : new Uri(text + "/");
        }
    }

    /// <summary>Identifica el juego de credenciales sin exponerlo (para invalidar el token si cambian).</summary>
    internal string CredentialKey =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('\n', EffectiveBaseUrl, ClientId, ClientSecret, Username, Password))));

    public override string ToString() => $"Factus {Environment} ({EffectiveBaseUrl}) usuario=***";
}

/// <summary>
/// Entrega las opciones de Factus vigentes (las lee y descifra quien lo implemente; el módulo Billing lo conectará).
/// Devolver <c>null</c> significa "Factus no configurado": el cliente responde <see cref="FactusOutcome.CredentialsError"/>.
/// </summary>
public interface IFactusOptionsProvider
{
    ValueTask<FactusOptions?> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Proveedor fijo (pruebas y herramientas). En producción se usa el que lee la configuración cifrada.</summary>
public sealed class StaticFactusOptionsProvider(FactusOptions? options) : IFactusOptionsProvider
{
    public ValueTask<FactusOptions?> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(options);
}
