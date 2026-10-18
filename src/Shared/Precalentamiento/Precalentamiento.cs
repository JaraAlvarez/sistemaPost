using System.Diagnostics;
using System.Runtime.Loader;

namespace Pos.Shared;

/// <summary>
/// Modo <c>--precalentar</c> (D17C-02): lee y carga los ensamblados del programa y termina, sin abrir puertos ni tocar la base de datos.
/// Windows Defender revisa cada .exe y .dll nuevo la primera vez que se abre (en pruebas: más de 4 minutos para una versión recién
/// compilada); el instalador y el actualizador corren este modo antes del cambio de versión para que el arranque real sea rápido y no
/// supere el tiempo que Windows da a un servicio.
/// Instalador rápido: primero se LEEN en paralelo (un lector por procesador) solo los archivos que el programa puede cargar: los de su
/// carpeta y los del runtime compartido que resolvió el host (TRUSTED_PLATFORM_ASSEMBLIES, app\&lt;versión&gt;\dotnet); después se cargan
/// los ensamblados propios, también en paralelo.
/// </summary>
internal static class Precalentamiento
{
    public const string Argumento = "--precalentar";

    public static bool Pedido(string[] args) => args.Contains(Argumento, StringComparer.OrdinalIgnoreCase);

    public static int Ejecutar()
    {
        var reloj = Stopwatch.StartNew();
        var propios = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").ToList();
        var archivos = new HashSet<string>(propios, StringComparer.OrdinalIgnoreCase);
        archivos.UnionWith(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.exe"));
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            archivos.UnionWith(tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        var paralelo = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        int leidos = 0, cargados = 0;
        Parallel.ForEach(archivos, paralelo, archivo =>
        {
            try
            {
                using var lectura = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                lectura.CopyTo(Stream.Null);
                Interlocked.Increment(ref leidos);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mejor esfuerzo (por ejemplo, un archivo de la lista que no existe en un programa de un solo archivo).
            }
        });

        Parallel.ForEach(propios, paralelo, archivo =>
        {
            try
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(archivo);
                Interlocked.Increment(ref cargados);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                // Biblioteca nativa o ya cargada con otra identidad: ya se leyó arriba, que es lo que revisa el antivirus.
            }
        });

        Console.WriteLine($"Precalentado: {leidos} archivos leídos y {cargados} ensamblados cargados en {reloj.Elapsed.TotalSeconds:0.0} s.");
        return 0;
    }
}
