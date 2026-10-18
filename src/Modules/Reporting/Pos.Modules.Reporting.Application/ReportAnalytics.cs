using System.Globalization;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Reporting.Application;

// ------------------------------------------------------------------------------------------------ ABC / Pareto

public enum AbcClass
{
    A,
    B,
    C,
}

/// <summary>Cortes del ABC en porcentaje: A hasta <see cref="A"/>, B los siguientes <see cref="B"/> puntos, C el resto.</summary>
public sealed record AbcCutoffs(decimal A, decimal B)
{
    public static AbcCutoffs Default { get; } = new(80m, 15m);

    /// <summary>A entre 1 y 99; B entre 0 y lo que falte para 100.</summary>
    public string? Validate() =>
        A is < 1m or > 99m ? "el corte A debe estar entre 1 y 99."
        : B < 0m || A + B > 100m ? "el corte B debe ser positivo y A + B no puede pasar de 100."
        : null;
}

/// <summary>Resultado de un producto: participación sobre la base, % acumulado (incluido él) y clase.</summary>
public sealed record AbcItem(decimal Share, decimal Cumulative, AbcClass Class);

/// <summary>
/// Clasificación ABC (Pareto). Se recorre de mayor a menor valor: un producto es A si el acumulado ANTES de él es menor que el corte A
/// (así el que cruza el 80 % queda en A), B si es menor que A + B, y C el resto. Un valor ≤ 0 va a C con participación 0.
/// La base es la suma de los valores positivos; el SQL la manda calculada con una función de ventana para que siga correcta aunque el
/// reporte se corte por el límite de filas.
/// </summary>
public static class AbcClassifier
{
    public static IReadOnlyList<AbcItem> Classify(IReadOnlyList<decimal> values, AbcCutoffs cutoffs, decimal? baseTotal = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(cutoffs);
        var total = baseTotal ?? values.Where(v => v > 0m).Sum();
        var order = Enumerable.Range(0, values.Count).OrderByDescending(i => values[i]).ThenBy(i => i).ToArray();
        var result = new AbcItem[values.Count];
        var cumulative = 0m;
        foreach (var i in order)
        {
            var value = values[i];
            if (value <= 0m || total <= 0m)
            {
                result[i] = new AbcItem(0m, Math.Min(cumulative, 100m), AbcClass.C);
                continue;
            }

            var share = value / total * 100m;
            var @class = cumulative < cutoffs.A ? AbcClass.A : cumulative < cutoffs.A + cutoffs.B ? AbcClass.B : AbcClass.C;
            cumulative += share;
            result[i] = new AbcItem(share, Math.Min(cumulative, 100m), @class);
        }

        return result;
    }
}

// ------------------------------------------------------------------------------------------------ Comparativos

/// <summary>Cómo se toma "el año pasado" (pregunta 2 de la propuesta).</summary>
public enum LastYearRule
{
    /// <summary>364 días antes: lunes contra lunes (comparativo por día).</summary>
    SameWeekday364,

    /// <summary>Mismas fechas con un año menos; el 29 de febrero pasa al 28 (comparativos por categoría y cajero).</summary>
    SameDates,
}

public sealed record PeriodRange(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;

    public override string ToString() =>
        $"{From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} a {To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Periodos de un comparativo. <see cref="PreviousShiftDays"/> y <see cref="LastYearShiftDays"/>: días que se suman a una fecha del periodo
/// comparado para llevarla a su día del periodo actual (comparativo por día).
/// </summary>
public sealed record ComparisonPeriods(PeriodRange Current, PeriodRange Previous, PeriodRange LastYear, int PreviousShiftDays, int LastYearShiftDays)
{
    public const int WeekAlignedYearDays = 364;

    /// <summary>Periodo anterior = los mismos N días justo antes; año pasado según la regla.</summary>
    public static ComparisonPeriods For(DateOnly from, DateOnly to, LastYearRule rule)
    {
        if (from > to)
        {
            throw new ArgumentException("La fecha inicial es posterior a la final.", nameof(from));
        }

        var current = new PeriodRange(from, to);
        var days = current.Days;
        var previous = new PeriodRange(from.AddDays(-days), from.AddDays(-1));
        var lastYear = rule == LastYearRule.SameWeekday364
            ? new PeriodRange(from.AddDays(-WeekAlignedYearDays), to.AddDays(-WeekAlignedYearDays))
            : new PeriodRange(from.AddYears(-1), to.AddYears(-1));
        var lastYearShift = rule == LastYearRule.SameWeekday364 ? WeekAlignedYearDays : from.DayNumber - lastYear.From.DayNumber;
        return new ComparisonPeriods(current, previous, lastYear, days, lastYearShift);
    }
}

/// <summary>Variaciones y razones de los comparativos (redondeadas a 2 decimales; vacías cuando no se pueden calcular).</summary>
public static class PeriodComparison
{
    /// <summary>Variación % = (actual − anterior) ÷ |anterior| × 100. Con anterior en 0 queda vacía.</summary>
    public static decimal? Variation(decimal? current, decimal? previous) =>
        current is not { } c || previous is not { } p || p == 0m ? null : Round((c - p) / Math.Abs(p) * 100m);

    /// <summary>Diferencia en puntos porcentuales (para el margen).</summary>
    public static decimal? PointChange(decimal? current, decimal? previous) =>
        current is { } c && previous is { } p ? Round(c - p) : null;

    public static decimal? Average(decimal? total, decimal? count) =>
        total is { } t && count is { } n && n != 0m ? Round(t / n) : null;

    /// <summary>Margen % = (base sin impuestos − costo) ÷ base × 100.</summary>
    public static decimal? Margin(decimal? netBase, decimal? cost) =>
        netBase is { } b && b != 0m ? Round((b - (cost ?? 0m)) / b * 100m) : null;

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

// ------------------------------------------------------------------------------------------------ Cálculos del catálogo

internal static class CalculationValues
{
    public static decimal? ToDecimal(object? value) => value switch
    {
        null => null,
        decimal d => d,
        long l => l,
        int i => i,
        short s => s,
        _ => null,
    };

    public static int IndexOf(IReadOnlyList<ReportColumn> columns, string key)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Key == key)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"La columna '{key}' no está en el reporte.");
    }
}

/// <summary>
/// ABC sobre una columna de valor: llena participación, % acumulado y clase. Los cortes llegan como parámetros (<c>cutA</c>, <c>cutB</c>);
/// la base, en una columna oculta que el SQL calcula con una función de ventana.
/// </summary>
internal sealed class AbcCalculation(string valueKey, string baseKey) : IReportCalculation
{
    public const string ShareKey = "abc_share";
    public const string CumulativeKey = "abc_cumulative";
    public const string ClassKey = "abc_class";

    public Result<ReportPreparation> Prepare(ReportCalculationContext context)
    {
        var cutoffs = Cutoffs(context);
        return cutoffs.Validate() is { } message ? ReportingErrors.InvalidParameter("cutA", message) : ReportPreparation.None;
    }

    public void Apply(ReportCalculationContext context, IReadOnlyList<ReportColumn> columns, IReadOnlyList<object?[]> rows)
    {
        var value = CalculationValues.IndexOf(columns, valueKey);
        var share = CalculationValues.IndexOf(columns, ShareKey);
        var cumulative = CalculationValues.IndexOf(columns, CumulativeKey);
        var @class = CalculationValues.IndexOf(columns, ClassKey);
        var baseIndex = CalculationValues.IndexOf(columns, baseKey);
        var baseTotal = rows.Count > 0 ? CalculationValues.ToDecimal(rows[0][baseIndex]) : null;
        var items = AbcClassifier.Classify([.. rows.Select(r => CalculationValues.ToDecimal(r[value]) ?? 0m)], Cutoffs(context), baseTotal);
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i][share] = Math.Round(items[i].Share, 2, MidpointRounding.AwayFromZero);
            rows[i][cumulative] = Math.Round(items[i].Cumulative, 2, MidpointRounding.AwayFromZero);
            rows[i][@class] = items[i].Class.ToString();
        }
    }

    public void CompleteTotals(ReportCalculationContext context, IReadOnlyList<ReportColumn> columns, object?[] totals)
    {
        // La base es una sola cifra repetida en cada fila: su "suma" no significa nada.
        totals[CalculationValues.IndexOf(columns, baseKey)] = null;
    }

    private static AbcCutoffs Cutoffs(ReportCalculationContext context) => new(
        Convert.ToDecimal(context.Value("cut_a") ?? AbcCutoffs.Default.A, CultureInfo.InvariantCulture),
        Convert.ToDecimal(context.Value("cut_b") ?? AbcCutoffs.Default.B, CultureInfo.InvariantCulture));
}

/// <summary>
/// Comparativo contra el periodo anterior y el año pasado. <see cref="Prepare"/> calcula los periodos (argumentos <c>@prev_from</c>,
/// <c>@prev_to</c>, <c>@ly_from</c>, <c>@ly_to</c>, <c>@prev_shift</c>, <c>@ly_shift</c>); el SQL trae por periodo venta neta, tiquetes,
/// total vendido, base sin impuestos y costo; aquí se calculan ticket promedio, margen y las variaciones (también de los totales).
/// </summary>
internal sealed class ComparisonCalculation(LastYearRule rule) : IReportCalculation
{
    public static readonly string[] Periods = ["cur", "prev", "ly"];

    public LastYearRule Rule => rule;

    public Result<ReportPreparation> Prepare(ReportCalculationContext context)
    {
        if (context.Value("from") is not DateOnly from || context.Value("to") is not DateOnly to)
        {
            return ReportingErrors.InvalidParameter("from", "el comparativo necesita las fechas desde y hasta.");
        }

        var periods = ComparisonPeriods.For(from, to, rule);
        return new ReportPreparation(
            [
                new("prev_from", SqlArgumentType.Date, periods.Previous.From),
                new("prev_to", SqlArgumentType.Date, periods.Previous.To),
                new("ly_from", SqlArgumentType.Date, periods.LastYear.From),
                new("ly_to", SqlArgumentType.Date, periods.LastYear.To),
                new("prev_shift", SqlArgumentType.WholeNumber, periods.PreviousShiftDays),
                new("ly_shift", SqlArgumentType.WholeNumber, periods.LastYearShiftDays),
            ],
            [
                new("Periodo anterior", periods.Previous.ToString()),
                new(rule == LastYearRule.SameWeekday364 ? "Año pasado (364 días antes, mismo día de la semana)" : "Año pasado (mismas fechas)",
                    periods.LastYear.ToString()),
            ]);
    }

    public void Apply(ReportCalculationContext context, IReadOnlyList<ReportColumn> columns, IReadOnlyList<object?[]> rows)
    {
        foreach (var row in rows)
        {
            Fill(columns, row);
        }
    }

    public void CompleteTotals(ReportCalculationContext context, IReadOnlyList<ReportColumn> columns, object?[] totals) => Fill(columns, totals);

    /// <summary>Calcula promedio, margen y variaciones a partir de las cifras de cada periodo (sirve igual para una fila y para los totales).</summary>
    internal static void Fill(IReadOnlyList<ReportColumn> columns, object?[] values)
    {
        decimal? Get(string key) => CalculationValues.ToDecimal(values[CalculationValues.IndexOf(columns, key)]);
        void Set(string key, decimal? value) => values[CalculationValues.IndexOf(columns, key)] = value;

        foreach (var period in Periods)
        {
            Set($"{period}_avg", PeriodComparison.Average(Get($"{period}_total"), Get($"{period}_tickets")));
            Set($"{period}_margin", PeriodComparison.Margin(Get($"{period}_base"), Get($"{period}_cost")));
        }

        foreach (var metric in new[] { "net", "tickets", "avg" })
        {
            Set($"{metric}_vs_prev", PeriodComparison.Variation(Get($"cur_{metric}"), Get($"prev_{metric}")));
            Set($"{metric}_vs_ly", PeriodComparison.Variation(Get($"cur_{metric}"), Get($"ly_{metric}")));
        }

        Set("margin_vs_prev", PeriodComparison.PointChange(Get("cur_margin"), Get("prev_margin")));
        Set("margin_vs_ly", PeriodComparison.PointChange(Get("cur_margin"), Get("ly_margin")));
    }
}
