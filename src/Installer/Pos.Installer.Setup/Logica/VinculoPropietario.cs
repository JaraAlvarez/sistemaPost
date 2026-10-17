using System.Buffers.Text;
using System.Text.Json;
using Pos.Licensing.Contracts;

namespace Pos.Installer.Setup.Logica;

/// <summary>
/// Enlace firmado del propietario que entregó la nube (correo y contraseña del portal o "Continuar con Google"), ATADO a la clave de
/// licencia con la que se pidió. Si el dueño vuelve atrás y escribe otra clave, el enlace ya no sirve: hay que validar la cuenta de nuevo
/// (antes se instalaba la clave B con el enlace obtenido con la clave A). Antes de instalar también se revisa, sin conexión, que el
/// enlace sea de la empresa que se configura (el NIT) y que no haya vencido; el POS lo vuelve a verificar con la firma.
/// </summary>
public sealed class VinculoPropietario
{
    /// <summary>Código para la página: volver a "Tu cuenta" y validar de nuevo.</summary>
    public const string CodigoInvalido = "INSTALADOR.CUENTA_INVALIDA";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public VinculoPropietario(string enlace, string correo, string clave, bool deGoogle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enlace);
        Enlace = enlace;
        Correo = correo;
        Clave = NormalizarClave(clave);
        DeGoogle = deGoogle;
    }

    public string Enlace { get; }

    /// <summary>Correo VALIDADO por la nube (el usuario del Propietario).</summary>
    public string Correo { get; }

    /// <summary>Clave de licencia (normalizada) con la que se obtuvo el enlace.</summary>
    public string Clave { get; }

    /// <summary>Con Google no hay contraseña del portal: el dueño elige una contraseña local.</summary>
    public bool DeGoogle { get; }

    /// <summary>La clave sin guiones ni espacios y en mayúsculas (<c>pos-abcde-…</c> = <c>POSABCDE…</c>).</summary>
    public static string NormalizarClave(string? clave) =>
        new([.. (clave ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]);

    /// <summary>
    /// ¿Se puede instalar con este enlace? <c>null</c> si sí; si no, el mensaje para la página. Revisa la clave, el NIT de la empresa
    /// que se configura (si el enlace se puede leer) y el vencimiento.
    /// </summary>
    public string? Revisar(string? clave, string? nitNegocio, DateTimeOffset ahora)
    {
        if (NormalizarClave(clave) != Clave)
        {
            return "Cambiaste la clave de licencia después de validar tu cuenta. Vuelve a \"Tu cuenta\" y valídala de nuevo con la clave nueva.";
        }

        if (LeerSinVerificar(Enlace) is not { } datos)
        {
            // Lo verifica el POS con la firma; aquí solo se revisa lo que se puede leer.
            return null;
        }

        var nitEnlace = datos.OrganizationNit.Split('-')[0].Trim();
        var nit = (nitNegocio ?? string.Empty).Split('-')[0].Trim();
        if (nit.Length > 0 && !string.Equals(nitEnlace, nit, StringComparison.Ordinal))
        {
            return "La cuenta del portal que validaste es de otra empresa (otro NIT). Vuelve a \"Tu cuenta\" y entra con la cuenta del dueño de esta licencia.";
        }

        if (ahora > datos.ExpiresAt)
        {
            return "La validación de tu cuenta venció. Vuelve a \"Tu cuenta\" y valídala de nuevo.";
        }

        return null;
    }

    /// <summary>Lee el contenido del JWS SIN verificar la firma (solo para revisar el NIT y el vencimiento antes de instalar).</summary>
    public static OwnerLinkClaims? LeerSinVerificar(string? enlace)
    {
        var partes = enlace?.Split('.');
        if (partes is not [_, var contenido, _])
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OwnerLinkClaims>(Base64Url.DecodeFromChars(contenido), Json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NotSupportedException)
        {
            return null;
        }
    }
}
