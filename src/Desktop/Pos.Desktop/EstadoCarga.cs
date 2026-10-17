namespace Pos.Desktop;

/// <summary>Mensajes de la pantalla de carga según el tiempo que lleva esperando a que BusinessPost responda.</summary>
internal enum FaseCarga
{
    /// <summary>"Iniciando BusinessPost…".</summary>
    Iniciando,

    /// <summary>Pasados 20 s: "La primera vez puede tardar unos minutos mientras Windows revisa los archivos…".</summary>
    Lento,

    /// <summary>Pasados 5 min: error amable con "Reintentar" y "Cómo resolverlo" (sigue comprobando por detrás).</summary>
    Error,
}

internal static class EstadoCarga
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan AvisoLento = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Limite = TimeSpan.FromMinutes(5);

    public static FaseCarga Fase(TimeSpan transcurrido) =>
        transcurrido >= Limite ? FaseCarga.Error
        : transcurrido >= AvisoLento ? FaseCarga.Lento
        : FaseCarga.Iniciando;

    /// <summary>Nombre que entiende la pantalla de carga (carga.html).</summary>
    public static string Nombre(FaseCarga fase) => fase switch
    {
        FaseCarga.Lento => "lento",
        FaseCarga.Error => "error",
        _ => "iniciando",
    };
}
