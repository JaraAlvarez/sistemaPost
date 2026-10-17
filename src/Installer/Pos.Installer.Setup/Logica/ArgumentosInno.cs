namespace Pos.Installer.Setup.Logica;

/// <summary>Cómo se usará el equipo (las tres tarjetas de "Este equipo").</summary>
public enum ModoEquipo
{
    /// <summary>Caja Única: este equipo guarda los datos y cobra.</summary>
    Unica,

    /// <summary>Multicaja: equipo principal (servidor de la tienda).</summary>
    Principal,

    /// <summary>Multicaja: caja adicional que se conecta al equipo principal.</summary>
    Adicional,
}

/// <summary>Línea de comandos del paquete Inno en silencio (parámetros de installer/BusinessPost.iss, Fase 13-B).</summary>
public static class ArgumentosInno
{
    public static string Instalar(
        ModoEquipo modo, string registro, string? servidor = null, string? huella = null, string? backup = null, string? archivoCodigo = null, bool cambiarModo = false)
    {
        var partes = new List<string>
        {
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/SP-", $"/LOG={Comillas(registro)}", "/PREMIUM=1",
            $"/MODO={modo.ToString().ToLowerInvariant()}",
        };
        if (modo == ModoEquipo.Adicional)
        {
            partes.Add($"/SERVIDOR={Comillas(servidor ?? string.Empty)}");
            partes.Add($"/HUELLA={Comillas(huella ?? string.Empty)}");
        }
        else if (!string.IsNullOrWhiteSpace(backup))
        {
            partes.Add($"/BACKUP={Comillas(backup)}");
            partes.Add($"/RECUPERACION={Comillas(archivoCodigo ?? string.Empty)}");
        }

        if (cambiarModo)
        {
            // El usuario confirmó cambiar el modo de un equipo ya instalado (sin esto, el paquete se cancela).
            partes.Add("/CAMBIARMODO=si");
        }

        return string.Join(' ', partes);
    }

    public static string Desinstalar(bool borrarDatos, string registro) =>
        $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG={Comillas(registro)} /BORRARDATOS={(borrarDatos ? "si" : "no")}";

    /// <summary>Entre comillas dobles; Inno no admite comillas dentro de un valor, así que se quitan.</summary>
    public static string Comillas(string valor) => $"\"{(valor ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal)}\"";
}

/// <summary>
/// Códigos de salida del paquete Inno (installer/BusinessPost.iss, GetCustomSetupExitCode, IN7) y el mensaje que ve el cliente. Antes, si la
/// base de datos fallaba, el paquete terminaba "con éxito" y el premium decía "instalado".
/// </summary>
public static class SalidaPaquete
{
    /// <summary>La base de datos o la recuperación fallaron; los datos y la versión anterior quedaron como estaban.</summary>
    public const int BaseDeDatos = 20;

    /// <summary>La migración falló y no se pudo volver a los datos de antes: restaurar con soporte (el servidor no se arrancó).</summary>
    public const int RestaurarConSoporte = 21;

    /// <summary>No se pudo activar la versión nueva (app\current).</summary>
    public const int VersionActiva = 22;

    public static string Mensaje(int salida, string? pasoConError) => salida switch
    {
        RestaurarConSoporte =>
            "La actualización de la base de datos falló y no se pudo volver automáticamente a los datos de antes. No uses BusinessPost " +
            "hasta que soporte restaure el backup previo (quedó en la carpeta de backups de BusinessPost). Comunícate con soporte y comparte el registro.",
        BaseDeDatos when pasoConError is { } paso && paso.StartsWith("Recuperación", StringComparison.OrdinalIgnoreCase) =>
            "No se pudo recuperar el backup (revisa el archivo y el código de recuperación). BusinessPost no quedó listo: vuelve a intentarlo " +
            "con el backup y el código correctos, o comunícate con soporte.",
        BaseDeDatos =>
            "No se pudo preparar la base de datos. BusinessPost sigue con la versión anterior y tus datos quedaron como estaban. Comunícate " +
            "con soporte y comparte el registro.",
        VersionActiva =>
            "No se pudo activar la versión nueva de BusinessPost. Reinicia el computador y vuelve a ejecutar el instalador; si sigue igual, " +
            "comunícate con soporte.",
        _ when pasoConError is { } paso => $"La instalación no pudo terminar el paso \"{paso}\".",
        _ => $"La instalación no pudo terminar (código {salida}).",
    };
}

/// <summary>
/// Modo con el que YA está instalado el equipo (misma regla que installer/BusinessPost.iss, DetectPreviousMode, IN10): primero el modo que
/// recordó Inno en la clave de desinstalación (valor <c>Inno Setup CodeFile: Modo</c>: unica, principal o adicional); si no está
/// (instalaciones anteriores a ese cambio), se deduce de la configuración: updater.json en modo "Terminal" = caja adicional; server.json con
/// edición MULTI = equipo principal, SINGLE = única caja. <c>null</c> si es nuevo.
/// </summary>
public static class ModoInstalado
{
    /// <summary>Valor que deja <c>SetPreviousData(..., 'Modo', ...)</c> de Inno en la clave de desinstalación.</summary>
    public const string ValorRegistro = "Inno Setup CodeFile: Modo";

    public static ModoEquipo? Detectar(string? guardado, string? updaterJson, string? serverJson) =>
        guardado?.Trim().ToLowerInvariant() switch
        {
            "unica" => ModoEquipo.Unica,
            "principal" => ModoEquipo.Principal,
            "adicional" => ModoEquipo.Adicional,
            _ => Detectar(updaterJson, serverJson),
        };

    /// <summary>Solo por la configuración (instalaciones que todavía no recuerdan el modo).</summary>
    public static ModoEquipo? Detectar(string? updaterJson, string? serverJson)
    {
        if (updaterJson is not null && updaterJson.Contains("\"Terminal\"", StringComparison.Ordinal))
        {
            return ModoEquipo.Adicional;
        }

        if (serverJson is null)
        {
            return null;
        }

        return serverJson.Contains("\"MULTI\"", StringComparison.Ordinal) ? ModoEquipo.Principal : ModoEquipo.Unica;
    }
}
