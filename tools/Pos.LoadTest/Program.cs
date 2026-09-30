using System.Globalization;

namespace Pos.LoadTest;

// Uso (docs/fases/fase-14-informe.md §2):
//   Pos.LoadTest seed [--server http://localhost:5480/] [--owner dueno] [--password …] [--products 30000] [--terminals 5] [--file carga.json]
//        Prepara la tienda de prueba por la API (configuración inicial si hace falta, productos, existencias, cajas, cajeros y equipos).
//   Pos.LoadTest run [--file carga.json] [--minutes 10] [--sales 1000000] [--lines 40] [--think-ms 0] [--close] [--csv resultados.csv]
//                    [--owner dueno] [--password …]
//        Cada caja vende en paralelo hasta el tiempo o el número de ventas; imprime p50/p95/p99 por operación frente a las metas.
// Use SIEMPRE una base de datos de prueba: la herramienta crea miles de productos y ventas.
// Códigos de salida: 0 = correcto, 1 = alguna meta del p95 no se cumplió o hubo errores, 2 = uso incorrecto.

/// <summary>Entrada de la consola (sin instrucciones de nivel superior: la usan también las pruebas junto al servidor).</summary>
internal static class LoadTestProgram
{
    public static async Task<int> Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            }
        }

        int Int(string name, int fallback) => int.TryParse(options.GetValueOrDefault(name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        var server = new Uri(options.GetValueOrDefault("server", "http://localhost:5480/"));
        var owner = options.GetValueOrDefault("owner", "dueno");
        var password = options.GetValueOrDefault("password", "Sup3rmercado-Seguro");
        var file = options.GetValueOrDefault("file", "carga.json");
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            switch (args.FirstOrDefault())
            {
                case "seed":
                {
                    var store = await Seed.RunAsync(server, owner, password, Int("products", 30_000), Int("terminals", 5), cancel.Token);
                    await Seed.SaveAsync(store, file, cancel.Token);
                    Console.WriteLine($"Tienda de prueba lista: {file}");
                    return 0;
                }

                case "run":
                {
                    var store = await Seed.LoadAsync(file, cancel.Token);
                    var (metrics, sales, elapsed) = await Run.ExecuteAsync(store, new RunOptions(
                        TimeSpan.FromMinutes(Int("minutes", 10)), Int("sales", 1_000_000), Int("lines", 40), Int("think-ms", 0), options.ContainsKey("close"),
                        owner, password), cancel.Token);
                    Console.WriteLine(metrics.Summary(elapsed, sales));
                    await metrics.WriteCsvAsync(options.GetValueOrDefault("csv", "resultados.csv"), cancel.Token);
                    var summary = metrics.Summary(elapsed, sales);
                    return summary.Contains('✗', StringComparison.Ordinal) || summary.Contains("error ", StringComparison.Ordinal) ? 1 : 0;
                }

                default:
                    Console.Error.WriteLine("Comandos: seed | run");
                    return 2;
            }
        }
        catch (ApiException ex)
        {
            Console.Error.WriteLine($"La API respondió {ex.Message}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"No se pudo conectar con {server}: {ex.Message}");
            return 1;
        }
    }
}
