using System.Globalization;

namespace Pos.Infrastructure.Backup;

/// <summary>Un backup en un destino, para decidir si se conserva.</summary>
public sealed record RetainedBackup(string Name, DateOnly Date, string Kind, bool Verified);

/// <summary>
/// Retención abuelo-padre-hijo (D11-07): se conservan el último backup de cada uno de los últimos N días, de cada una de las últimas M
/// semanas (ISO) y de cada uno de los últimos K meses, más los P últimos "antes de actualizar". Nunca se borra el último verificado.
/// Solo cuentan los verificados para cubrir un período; los no verificados más recientes que el último verificado se conservan.
/// </summary>
public sealed record RetentionPolicy(int Daily = 7, int Weekly = 4, int Monthly = 12, int PreUpdate = 3)
{
    public const string PreUpdateKind = "PRE_UPDATE";

    public IReadOnlyList<RetainedBackup> ToDelete(IReadOnlyList<RetainedBackup> backups)
    {
        ArgumentNullException.ThrowIfNull(backups);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var ordered = backups.OrderByDescending(b => b.Date).ThenByDescending(b => b.Name, StringComparer.Ordinal).ToList();
        var verified = ordered.Where(b => b.Verified).ToList();
        if (verified.Count > 0)
        {
            keep.Add(verified[0].Name);
            var newestVerified = verified[0];
            foreach (var pending in ordered.Where(b => !b.Verified && string.CompareOrdinal(b.Name, newestVerified.Name) > 0 && b.Date >= newestVerified.Date))
            {
                keep.Add(pending.Name);
            }
        }
        else
        {
            // Sin ninguno verificado no se borra nada (no hay con qué reemplazarlos).
            return [];
        }

        KeepFirstOfEach(verified.Where(b => b.Kind != PreUpdateKind), b => b.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Daily, keep);
        KeepFirstOfEach(verified.Where(b => b.Kind != PreUpdateKind), b => $"{ISOWeek.GetYear(b.Date.ToDateTime(TimeOnly.MinValue))}-{ISOWeek.GetWeekOfYear(b.Date.ToDateTime(TimeOnly.MinValue))}", Weekly, keep);
        KeepFirstOfEach(verified.Where(b => b.Kind != PreUpdateKind), b => b.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture), Monthly, keep);
        foreach (var pre in verified.Where(b => b.Kind == PreUpdateKind).Take(PreUpdate))
        {
            keep.Add(pre.Name);
        }

        return [.. ordered.Where(b => !keep.Contains(b.Name))];
    }

    private static void KeepFirstOfEach(IEnumerable<RetainedBackup> newestFirst, Func<RetainedBackup, string> period, int count, HashSet<string> keep)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var backup in newestFirst)
        {
            if (seen.Count >= count && !seen.Contains(period(backup)))
            {
                break;
            }

            if (seen.Add(period(backup)))
            {
                keep.Add(backup.Name);
            }
        }
    }
}
