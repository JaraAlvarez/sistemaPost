using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Pos.LoadTest;

/// <summary>Tiempos por operación y comparación con las metas de la Fase 14 (§2 de la propuesta).</summary>
public sealed class Metrics
{
    /// <summary>Metas del percentil 95 en milisegundos.</summary>
    public static readonly IReadOnlyDictionary<string, double> Targets = new Dictionary<string, double>
    {
        ["escanear"] = 100,
        ["buscar"] = 200,
        ["cobrar"] = 300,
        ["cerrar-jornada"] = 2000,
        ["reporte-mes-xlsx"] = 5000,
    };

    private readonly ConcurrentDictionary<string, ConcurrentBag<(double Ms, bool Ok)>> _samples = new(StringComparer.Ordinal);

    public void Add(string operation, double milliseconds, bool ok) => _samples.GetOrAdd(operation, _ => []).Add((milliseconds, ok));

    public string Summary(TimeSpan elapsed, int sales)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Duración {elapsed:hh\\:mm\\:ss} · ventas completadas {sales} · {sales / Math.Max(1, elapsed.TotalMinutes):0.0} ventas/min");
        text.AppendLine("Operación           Cant.    Errores    p50 ms   p95 ms   p99 ms   máx ms   Meta p95");
        foreach (var (operation, samples) in _samples.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var ok = samples.Where(s => s.Ok).Select(s => s.Ms).Order().ToArray();
            var errors = samples.Count(s => !s.Ok);
            var target = Targets.TryGetValue(operation, out var t) ? t : (double?)null;
            var p95 = Percentile(ok, 0.95);
            var verdict = target is null ? "—" : p95 <= target ? $"≤ {target:0} ✓" : $"≤ {target:0} ✗";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{operation,-18} {samples.Count,6} {errors,10} {Percentile(ok, 0.5),9:0} {p95,8:0} {Percentile(ok, 0.99),8:0} {(ok.Length == 0 ? 0 : ok[^1]),8:0}   {verdict}");
        }

        return text.ToString();
    }

    public async Task WriteCsvAsync(string path, CancellationToken ct)
    {
        var csv = new StringBuilder("operacion;cantidad;errores;p50_ms;p95_ms;p99_ms;max_ms;meta_p95_ms\n");
        foreach (var (operation, samples) in _samples.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var ok = samples.Where(s => s.Ok).Select(s => s.Ms).Order().ToArray();
            csv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{operation};{samples.Count};{samples.Count(s => !s.Ok)};{Percentile(ok, 0.5):0};{Percentile(ok, 0.95):0};{Percentile(ok, 0.99):0};{(ok.Length == 0 ? 0 : ok[^1]):0};{(Targets.TryGetValue(operation, out var t) ? t.ToString(CultureInfo.InvariantCulture) : string.Empty)}"));
        }

        await File.WriteAllTextAsync(path, csv.ToString(), Encoding.UTF8, ct);
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
}
