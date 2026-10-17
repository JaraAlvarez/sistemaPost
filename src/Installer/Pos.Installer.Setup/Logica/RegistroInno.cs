using System.Globalization;

namespace Pos.Installer.Setup.Logica;

/// <summary>Etapas que ve el cliente en "Instalando BusinessPost" (en este orden).</summary>
public enum Etapa
{
    CopiandoProgramas = 0,
    RevisandoDefender = 1,
    CreandoBaseDeDatos = 2,
    PreparandoCaja = 3,
    IniciandoBusinessPost = 4,
    ConfigurandoNegocio = 5,
}

/// <summary>Cómo terminó el paquete Inno según su registro.</summary>
public enum ResultadoInno
{
    EnCurso,
    Correcto,

    /// <summary>Quedó instalado pero el servicio todavía no responde (Defender sigue revisando, por ejemplo).</summary>
    SinRespuesta,
    Error,
}

/// <summary>
/// Lee, línea por línea, el registro (/LOG) del paquete Inno Setup que corre en silencio (Fase 13-B, D13B-08). Las marcas las escribe
/// installer/BusinessPost.iss: <c>Progreso: N</c> (copia de archivos, 0–100), <c>Paso: …</c> (cada etapa, el mismo texto del asistente
/// clásico) y <c>Resultado: OK | SIN_RESPUESTA | ERROR …</c>. Cada línea de Inno empieza con fecha y hora; se buscan las marcas en
/// cualquier posición. Lógica pura: se prueba sin instalar nada.
/// </summary>
public sealed class RegistroInno
{
    public Etapa Etapa { get; private set; } = Etapa.CopiandoProgramas;

    /// <summary>Avance de la copia de archivos (0–100).</summary>
    public int PorcentajeCopia { get; private set; }

    public string? PasoActual { get; private set; }

    public ResultadoInno Resultado { get; private set; } = ResultadoInno.EnCurso;

    /// <summary>Primer paso que falló (texto del instalador), si hubo.</summary>
    public string? Error { get; private set; }

    /// <summary>Procesa una línea del registro; devuelve <c>true</c> si cambió algo visible.</summary>
    public bool Procesar(string? linea)
    {
        if (string.IsNullOrWhiteSpace(linea))
        {
            return false;
        }

        if (Marca(linea, "Progreso: ") is { } progreso)
        {
            if (int.TryParse(progreso, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valor) && Etapa == Etapa.CopiandoProgramas)
            {
                var nuevo = Math.Clamp(valor, 0, 100);
                var cambio = nuevo != PorcentajeCopia;
                PorcentajeCopia = Math.Max(PorcentajeCopia, nuevo);
                return cambio;
            }

            return false;
        }

        if (Marca(linea, "Paso: ") is { } paso)
        {
            PasoActual = paso;
            var etapa = EtapaDelPaso(paso);
            if (etapa > Etapa)
            {
                Etapa = etapa;
                if (etapa > Etapa.CopiandoProgramas)
                {
                    PorcentajeCopia = 100;
                }
            }

            return true;
        }

        if (Marca(linea, "Resultado: ") is { } resultado)
        {
            if (resultado.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                // Se conserva el PRIMER error: es la causa; los demás suelen ser consecuencia.
                Error ??= resultado.Length > 5 ? resultado[5..].Trim() : "Paso desconocido";
                Resultado = ResultadoInno.Error;
            }
            else if (Resultado != ResultadoInno.Error)
            {
                Resultado = resultado.StartsWith("SIN_RESPUESTA", StringComparison.OrdinalIgnoreCase) ? ResultadoInno.SinRespuesta
                    : resultado.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ? ResultadoInno.Correcto
                    : Resultado;
            }

            return true;
        }

        return false;
    }

    /// <summary>Etapa que corresponde a un "Paso:" del instalador (textos de BusinessPost.iss).</summary>
    public static Etapa EtapaDelPaso(string paso)
    {
        ArgumentNullException.ThrowIfNull(paso);
        return paso switch
        {
            _ when paso.StartsWith("Revisando", StringComparison.OrdinalIgnoreCase) => Etapa.RevisandoDefender,
            _ when paso.StartsWith("Creando la base", StringComparison.OrdinalIgnoreCase)
                || paso.StartsWith("Recuperando", StringComparison.OrdinalIgnoreCase) => Etapa.CreandoBaseDeDatos,
            _ when paso.StartsWith("Registrando", StringComparison.OrdinalIgnoreCase) => Etapa.PreparandoCaja,
            _ when paso.StartsWith("Iniciando", StringComparison.OrdinalIgnoreCase)
                || paso.StartsWith("Esperando", StringComparison.OrdinalIgnoreCase) => Etapa.IniciandoBusinessPost,
            _ => Etapa.CopiandoProgramas,
        };
    }

    /// <summary>
    /// Barra general (0–100): la copia es medible (0–35 %); las demás etapas no, así que cada una ocupa un tramo fijo. El último tramo
    /// (88–100 %) es la configuración de la tienda que hace el propio instalador premium después del paquete.
    /// </summary>
    public int PorcentajeGeneral => Etapa switch
    {
        Etapa.CopiandoProgramas => PorcentajeCopia * 35 / 100,
        Etapa.RevisandoDefender => 40,
        Etapa.CreandoBaseDeDatos => 58,
        Etapa.PreparandoCaja => 72,
        Etapa.IniciandoBusinessPost => 80,
        _ => 90,
    };

    private static string? Marca(string linea, string marca)
    {
        var posicion = linea.IndexOf(marca, StringComparison.Ordinal);
        return posicion < 0 ? null : linea[(posicion + marca.Length)..].Trim();
    }
}
