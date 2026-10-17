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
    public static string Instalar(ModoEquipo modo, string registro, string? servidor = null, string? huella = null, string? backup = null, string? archivoCodigo = null)
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

        return string.Join(' ', partes);
    }

    public static string Desinstalar(bool borrarDatos, string registro) =>
        $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG={Comillas(registro)} /BORRARDATOS={(borrarDatos ? "si" : "no")}";

    /// <summary>Entre comillas dobles; Inno no admite comillas dentro de un valor, así que se quitan.</summary>
    public static string Comillas(string valor) => $"\"{(valor ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal)}\"";
}
