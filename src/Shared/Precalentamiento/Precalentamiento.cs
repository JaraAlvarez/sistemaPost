using System.Diagnostics;
using System.Runtime.Loader;

namespace Pos.Shared;

/// <summary>
/// Modo <c>--precalentar</c> (D17C-02): carga todos los ensamblados de la carpeta del programa y termina, sin abrir puertos ni tocar la
/// base de datos. Windows Defender revisa cada .exe y .dll nuevo la primera vez que se carga (en pruebas: más de 4 minutos para una
/// versión recién compilada); el actualizador corre este modo al descomprimir una versión, mientras la tienda sigue vendiendo con la
/// anterior, para que el arranque real después de cambiar de versión sea rápido y no supere el tiempo que Windows da a un servicio.
/// </summary>
internal static class Precalentamiento
{
    public const string Argumento = "--precalentar";

    public static bool Pedido(string[] args) => args.Contains(Argumento, StringComparer.OrdinalIgnoreCase);

    public static int Ejecutar()
    {
        var reloj = Stopwatch.StartNew();
        int cargados = 0, leidos = 0;
        foreach (var archivo in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            try
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(archivo);
                cargados++;
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                // Biblioteca nativa o ya cargada con otra identidad: basta con leerla para que el antivirus la revise.
                try
                {
                    using var lectura = File.OpenRead(archivo);
                    lectura.CopyTo(Stream.Null);
                    leidos++;
                }
                catch (IOException)
                {
                    // Mejor esfuerzo.
                }
            }
        }

        Console.WriteLine($"Precalentado: {cargados} ensamblados cargados y {leidos} bibliotecas leídas en {reloj.Elapsed.TotalSeconds:0.0} s.");
        return 0;
    }
}
