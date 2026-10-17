using Pos.Licensing.Contracts;

namespace Pos.Installer.Setup.Logica;

/// <summary>Estado de la espera de "Continuar con Google" (lo que ve la página).</summary>
public enum EstadoEspera
{
    /// <summary>El dueño todavía no autoriza en el navegador.</summary>
    Esperando,

    /// <summary>Autorizada: llegó el enlace firmado (<see cref="EsperaAutorizacion.Enlace"/>).</summary>
    Autorizada,

    /// <summary>El dueño la canceló en el portal.</summary>
    Rechazada,

    /// <summary>Se acabaron los minutos de la solicitud.</summary>
    Vencida,

    /// <summary>El usuario pulsó "Cancelar" en el instalador (o cerró la ventana).</summary>
    Cancelada,

    /// <summary>La nube respondió algo que no se arregla esperando (solicitud usada, clave inválida…).</summary>
    Error,
}

/// <summary>Lo que respondió UNA consulta a la nube.</summary>
public sealed record ResultadoConsulta(string? Estado, int? Intervalo = null, OwnerLinkResponse? Enlace = null, string? Codigo = null, string? Mensaje = null)
{
    /// <summary>Sin conexión o tiempo agotado: se reintenta.</summary>
    public static ResultadoConsulta SinConexion { get; } = new(Estado: null);

    public static ResultadoConsulta Con(OwnerLinkPollResponse respuesta)
    {
        ArgumentNullException.ThrowIfNull(respuesta);
        return new(respuesta.Status, respuesta.Interval, respuesta.Link);
    }

    /// <summary>Error de la nube; sin código estable (p. ej. un 502 del proxy) se trata como falta de conexión y se reintenta.</summary>
    public static ResultadoConsulta Fallo(string? codigo, string? mensaje) => codigo is null ? SinConexion : new(null, null, null, codigo, mensaje);
}

/// <summary>
/// Máquina de estados de la espera de "Continuar con Google" (como el cliente de OAuth 2.0 Device Authorization, RFC 8628): consulta
/// cada <see cref="Intervalo"/>; <c>slow_down</c> (429) suma 5 s; sin conexión reintenta hasta que vence; PENDING sigue; APPROVED,
/// DENIED, EXPIRED o un error definitivo terminan. Pura (sin red ni reloj propios) para probarla.
/// </summary>
public sealed class EsperaAutorizacion
{
    /// <summary>Suma de un <c>slow_down</c> (igual que la nube).</summary>
    public static readonly TimeSpan PasoLento = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan IntervaloMinimo = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan IntervaloMaximo = TimeSpan.FromSeconds(30);

    /// <summary>Margen para no consultar justo al vencer (la nube respondería EXPIRED de todos modos).</summary>
    private static readonly TimeSpan Margen = TimeSpan.FromSeconds(1);

    public EsperaAutorizacion(DateTimeOffset inicio, TimeSpan vigencia, TimeSpan intervalo)
    {
        Vence = inicio + (vigencia > TimeSpan.Zero ? vigencia : TimeSpan.FromMinutes(10));
        Intervalo = Acotar(intervalo);
    }

    public EstadoEspera Estado { get; private set; } = EstadoEspera.Esperando;

    public TimeSpan Intervalo { get; private set; }

    public DateTimeOffset Vence { get; }

    public int FallasDeConexion { get; private set; }

    public OwnerLinkResponse? Enlace { get; private set; }

    /// <summary>Mensaje para la página cuando termina sin autorizar.</summary>
    public string? Mensaje { get; private set; }

    public bool Terminada => Estado != EstadoEspera.Esperando;

    /// <summary>Cuánto esperar antes de la próxima consulta (nunca después de vencer).</summary>
    public TimeSpan EsperaAntesDeConsultar(DateTimeOffset ahora)
    {
        var restante = Vence - ahora - Margen;
        return restante <= TimeSpan.Zero ? TimeSpan.Zero : restante < Intervalo ? restante : Intervalo;
    }

    /// <summary>¿Ya venció? (se revisa antes de cada consulta).</summary>
    public bool RevisarVencimiento(DateTimeOffset ahora)
    {
        if (!Terminada && ahora >= Vence - Margen)
        {
            Terminar(EstadoEspera.Vencida, FallasDeConexion > 0
                ? "No hubo conexión con BusinessPost en internet mientras esperábamos tu autorización (¿la red pide iniciar sesión en una página?). Revisa el internet y pulsa \"Reintentar\"."
                : "Pasaron los minutos para autorizar. Pulsa \"Reintentar\".");
        }

        return Estado == EstadoEspera.Vencida;
    }

    public void Cancelar()
    {
        if (!Terminada)
        {
            Terminar(EstadoEspera.Cancelada, null);
        }
    }

    public EstadoEspera Aplicar(ResultadoConsulta resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        if (Terminada)
        {
            return Estado;
        }

        if (resultado.Codigo == LicenseErrorCodes.OwnerLinkSlowDown)
        {
            Intervalo = Acotar(Intervalo + PasoLento);
            return Estado;
        }

        if (resultado.Codigo is not null)
        {
            // Demasiadas peticiones por IP: como slow_down. Servidor sin firma (mantenimiento): se reintenta. Lo demás no se arregla esperando.
            if (resultado.Codigo is "SECURITY.TOO_MANY_REQUESTS" or LicenseErrorCodes.TooManyRequests)
            {
                Intervalo = Acotar(Intervalo + PasoLento);
                return Estado;
            }

            if (resultado.Codigo == LicenseErrorCodes.SigningUnavailable)
            {
                FallasDeConexion++;
                return Estado;
            }

            Terminar(EstadoEspera.Error, resultado.Mensaje ?? "No se pudo completar el ingreso con Google. Intenta de nuevo o usa tu correo y contraseña.");
            return Estado;
        }

        if (resultado.Estado is null)
        {
            FallasDeConexion++;
            return Estado;
        }

        FallasDeConexion = 0;
        if (resultado.Intervalo is { } segundos && TimeSpan.FromSeconds(segundos) > Intervalo)
        {
            Intervalo = Acotar(TimeSpan.FromSeconds(segundos));
        }

        switch (resultado.Estado)
        {
            case OwnerLinkRequestStatuses.Pending:
                break;
            case OwnerLinkRequestStatuses.Approved when resultado.Enlace is { Token.Length: > 0 }:
                Enlace = resultado.Enlace;
                Terminar(EstadoEspera.Autorizada, null);
                break;
            case OwnerLinkRequestStatuses.Denied:
                Terminar(EstadoEspera.Rechazada, "La solicitud se canceló en el portal. Si fue un error, pulsa de nuevo \"Continuar con Google\".");
                break;
            case OwnerLinkRequestStatuses.Expired:
                Terminar(EstadoEspera.Vencida, "Pasaron los minutos para autorizar. Pulsa de nuevo \"Continuar con Google\".");
                break;
            default:
                Terminar(EstadoEspera.Error, "La respuesta del servidor no es la esperada. Intenta de nuevo o usa tu correo y contraseña.");
                break;
        }

        return Estado;
    }

    /// <summary>
    /// Consulta hasta terminar. <paramref name="consultar"/> hace UNA consulta; <paramref name="esperar"/> y <paramref name="reloj"/> se
    /// reemplazan en las pruebas. Cancelar el token = el usuario canceló (o se cerró la ventana).
    /// </summary>
    public async Task<EstadoEspera> EjecutarAsync(
        Func<CancellationToken, Task<ResultadoConsulta>> consultar,
        Func<TimeSpan, CancellationToken, Task> esperar,
        Func<DateTimeOffset> reloj,
        Action<EsperaAutorizacion>? alCambiar,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consultar);
        ArgumentNullException.ThrowIfNull(esperar);
        ArgumentNullException.ThrowIfNull(reloj);
        try
        {
            while (!Terminada)
            {
                await esperar(EsperaAntesDeConsultar(reloj()), cancellationToken);
                if (RevisarVencimiento(reloj()))
                {
                    break;
                }

                Aplicar(await consultar(cancellationToken));
                alCambiar?.Invoke(this);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cancelar();
        }

        alCambiar?.Invoke(this);
        return Estado;
    }

    private void Terminar(EstadoEspera estado, string? mensaje)
    {
        Estado = estado;
        Mensaje = mensaje;
    }

    private static TimeSpan Acotar(TimeSpan intervalo) =>
        intervalo < IntervaloMinimo ? IntervaloMinimo : intervalo > IntervaloMaximo ? IntervaloMaximo : intervalo;
}

/// <summary>Reglas del ingreso con Google en el instalador que no dependen de Windows.</summary>
public static class IngresoGoogle
{
    /// <summary>
    /// La dirección que abre el navegador debe ser HTTPS y del MISMO servidor de licencias configurado en el instalador (nunca una que
    /// mande la respuesta a otro sitio). En desarrollo se acepta http solo para <c>localhost</c>.
    /// </summary>
    public static bool EsDireccionSegura(string? direccion, Uri? servidor)
    {
        if (servidor is null || !Uri.TryCreate(direccion, UriKind.Absolute, out var url))
        {
            return false;
        }

        var esquemaValido = url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback && servidor.IsLoopback);
        return esquemaValido
            && string.Equals(url.Scheme, servidor.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(url.IdnHost, servidor.IdnHost, StringComparison.OrdinalIgnoreCase)
            && url.Port == servidor.Port
            && string.IsNullOrEmpty(url.UserInfo)
            && url.AbsolutePath.StartsWith(servidor.AbsolutePath.TrimEnd('/') + "/cuenta/vincular-instalador", StringComparison.Ordinal);
    }

    /// <summary>
    /// Contraseña LOCAL del Propietario cuando entra con Google (no hay contraseña del portal que reutilizar): la usará en este equipo,
    /// también sin internet. Misma política del portal: 12 o más caracteres, con letras y números. <c>null</c> si sirve.
    /// </summary>
    public static string? ValidarContrasenaLocal(string? contrasena, string? confirmacion, string? correo)
    {
        if (string.IsNullOrEmpty(contrasena) || contrasena.Length < 12)
        {
            return "La contraseña debe tener al menos 12 caracteres.";
        }

        if (contrasena.Length > 128)
        {
            return "La contraseña es demasiado larga (máximo 128 caracteres).";
        }

        if (!contrasena.Any(char.IsLetter) || !contrasena.Any(char.IsDigit))
        {
            return "Usa letras y números en la contraseña.";
        }

        var usuario = (correo ?? string.Empty).Split('@')[0];
        if (contrasena.Distinct().Count() <= 3 || (usuario.Length >= 4 && contrasena.Contains(usuario, StringComparison.OrdinalIgnoreCase)))
        {
            return "Esa contraseña es muy fácil de adivinar. Usa una frase o mezcla letras y números.";
        }

        return contrasena == confirmacion ? null : "Las dos contraseñas no coinciden.";
    }
}
