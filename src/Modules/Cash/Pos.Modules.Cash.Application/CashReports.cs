using System.Globalization;
using System.Text;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Cash.Application;

/// <summary>
/// Reportes X (parcial) y Z (cierre) de una jornada: datos y texto de 80 mm (42 columnas). El Z lleva el sello de la
/// auditoría (D6-08) para detectar una reescritura posterior de la bitácora.
/// </summary>
public static class CashReports
{
    public const int Width = 42;

    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    public static CashReportDto Build(
        string kind, CashSession session, SessionHeader header, IReadOnlyList<CashMovementDto> movements, IReadOnlyList<CashMethodTotalDto> totals,
        DateTimeOffset printedAt, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(clock);
        var byType = movements.Where(m => m.MovementType != "NO_SALE_DRAWER_OPEN")
            .GroupBy(m => m.MovementType)
            .Select(g => new CashMovementTypeTotalDto(g.Key, g.Count(), g.Sum(m => m.Amount)))
            .OrderBy(t => t.MovementType, StringComparer.Ordinal)
            .ToList();
        var withdrawals = movements.Where(m => m.MovementType == "CASH_OUT_WITHDRAWAL")
            .Select(m => new CashWithdrawalDto(m.OccurredAt, m.Amount, m.Reason, m.AuthorizedByName)).ToList();
        var noSale = movements.Count(m => m.MovementType == "NO_SALE_DRAWER_OPEN");
        var expected = totals.Sum(t => t.Expected ?? 0m);
        var closed = session.Status == CashSessionStatus.Closed;
        var report = new CashReportDto(
            kind, session.Id, session.Number, header.CompanyName, header.BranchName, header.TerminalCode, header.CashierName, session.BusinessDate,
            session.OpenedAt, session.ClosedAt, printedAt, session.OpeningFloat, byType, totals, expected, closed ? session.CountedTotal : null,
            closed ? session.Difference : null, session.DifferenceNote, noSale, withdrawals, session.ClosedBySupervisor, session.ZSealNo, session.ZSealCode,
            string.Empty);
        return report with { Text = Text(report, clock) };
    }

    public static string Text(CashReportDto r, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(clock);
        var text = new StringBuilder();
        Center(text, r.CompanyName);
        Center(text, r.BranchName);
        Center(text, r.Kind == "Z" ? "REPORTE Z - CIERRE DE CAJA" : "REPORTE X - PARCIAL");
        Line(text, '=');
        Pair(text, "Jornada", r.Number);
        Pair(text, "Caja", r.TerminalCode);
        Pair(text, "Cajero", r.CashierName);
        Pair(text, "Fecha de negocio", r.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Pair(text, "Apertura", Time(r.OpenedAt, clock));
        if (r.ClosedAt is { } closedAt)
        {
            Pair(text, "Cierre", Time(closedAt, clock));
        }

        Pair(text, "Impreso", Time(r.PrintedAt, clock));
        Line(text, '-');
        Pair(text, "Fondo inicial", Money(r.OpeningFloat));
        foreach (var t in r.MovementsByType.Where(t => t.MovementType != "OPENING_FLOAT"))
        {
            Pair(text, $"{Label(t.MovementType)} ({t.Count})", Money(t.Total));
        }

        Line(text, '-');
        text.Append(Fit("MEDIO", 16)).Append(Right("ESPERADO", 13)).Append(Right("CONTADO", 13)).Append('\n');
        foreach (var t in r.Totals)
        {
            text.Append(Fit(t.Name, 16)).Append(Right(t.Expected is { } e ? Money(e) : "***", 13))
                .Append(Right(t.Counted is { } c ? Money(c) : "-", 13)).Append('\n');
            if (t.Difference is { } d && d != 0m)
            {
                Pair(text, "  Diferencia", Money(d));
            }
        }

        Line(text, '-');
        Pair(text, "TOTAL ESPERADO", r.Totals.All(t => t.Expected is not null) ? Money(r.ExpectedTotal) : "***");
        if (r.CountedTotal is { } counted)
        {
            Pair(text, "TOTAL CONTADO", Money(counted));
            Pair(text, "DIFERENCIA", Money(r.Difference ?? 0m));
        }

        if (r.DifferenceNote is { } note)
        {
            Wrap(text, $"Obs.: {note}");
        }

        Pair(text, "Aperturas sin venta", r.NoSaleOpenings.ToString(CultureInfo.InvariantCulture));
        foreach (var w in r.Withdrawals)
        {
            Pair(text, $"Retiro {Time(w.OccurredAt, clock)[11..]}", Money(w.Amount));
            Wrap(text, $"  Autorizó: {w.AuthorizedByName ?? "-"}. {w.Reason}");
        }

        if (r.ClosedBySupervisor)
        {
            Center(text, "CIERRE POR SUPERVISOR");
        }

        if (r.Kind == "Z")
        {
            Line(text, '=');
            Center(text, r.SealNo is { } sealNo ? $"SELLO #{sealNo.ToString(CultureInfo.InvariantCulture)} · {r.SealCode}" : "SELLO PENDIENTE");
            Center(text, "Verificación: verify-audit --seal N --code");
        }

        Line(text, '=');
        return text.ToString();
    }

    public static string Label(string movementType) => movementType switch
    {
        "OPENING_FLOAT" => "Fondo inicial",
        "SALE" => "Ventas",
        "SALE_VOID" => "Anulaciones",
        "CUSTOMER_REFUND" => "Devoluciones",
        "CASH_IN" => "Ingresos",
        "CASH_OUT_WITHDRAWAL" => "Retiros",
        "EXPENSE" => "Gastos",
        "SUPPLIER_PAYMENT" => "Pagos a proveedor",
        "CORRECTION" => "Correcciones",
        _ => movementType,
    };

    private static string Money(decimal value) => "$" + value.ToString("#,##0.##", Colombia);

    private static string Time(DateTimeOffset instant, IClock clock) =>
        clock.ToBusinessTime(instant).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Fit(string text, int width) => text.Length > width ? text[..width] : text.PadRight(width);

    private static string Right(string text, int width) => text.Length > width ? text[..width] : text.PadLeft(width);

    private static void Line(StringBuilder text, char c) => text.Append(new string(c, Width)).Append('\n');

    private static void Center(StringBuilder text, string value)
    {
        var fitted = value.Length > Width ? value[..Width] : value;
        text.Append(new string(' ', (Width - fitted.Length) / 2)).Append(fitted).Append('\n');
    }

    private static void Pair(StringBuilder text, string label, string value)
    {
        var room = Width - value.Length - 1;
        text.Append(Fit(label, Math.Max(room, 1))).Append(' ').Append(value).Append('\n');
    }

    private static void Wrap(StringBuilder text, string value)
    {
        for (var i = 0; i < value.Length; i += Width)
        {
            text.Append(value.AsSpan(i, Math.Min(Width, value.Length - i))).Append('\n');
        }
    }
}
