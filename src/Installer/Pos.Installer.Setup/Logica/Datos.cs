using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pos.Installer.Setup.Logica;

/// <summary>Dígito de verificación del NIT (DIAN, módulo 11). Igual que <c>Pos.SharedKernel.Fiscal.Nit</c>; el instalador lo calcula solo.</summary>
public static class DigitoVerificacion
{
    private static readonly int[] Pesos = [3, 7, 13, 17, 19, 23, 29, 37, 41, 43, 47, 53, 59, 67, 71];

    /// <summary><c>null</c> si el número no tiene entre 5 y 15 dígitos.</summary>
    public static int? Calcular(string? numero)
    {
        var limpio = new string((numero ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (limpio.Length is < 5 or > 15 || limpio.Length != (numero ?? string.Empty).Count(c => !char.IsWhiteSpace(c) && c != '.'))
        {
            return null;
        }

        var suma = 0;
        for (var i = 0; i < limpio.Length; i++)
        {
            suma += (limpio[limpio.Length - 1 - i] - '0') * Pesos[i];
        }

        var resto = suma % 11;
        return resto > 1 ? 11 - resto : resto;
    }
}

/// <summary>Municipio de Colombia para el buscador del paso "Tu negocio".</summary>
public sealed record Municipio(string Codigo, string Nombre, string Departamento);

/// <summary>
/// Municipios y departamentos (DANE DIVIPOLA) leídos del MISMO script con el que el POS los carga en su base de datos
/// (<c>R__ref__seed_colombia.sql</c>, incrustado al compilar): antes de instalar todavía no hay servidor al que preguntar.
/// </summary>
public static partial class Municipios
{
    public static IReadOnlyList<Municipio> Leer(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var departamentos = Departamento().Matches(sql).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        return
        [
            .. MunicipioFila().Matches(sql)
                .Select(m => new Municipio(m.Groups[1].Value, m.Groups[3].Value.Replace("''", "'", StringComparison.Ordinal),
                    departamentos.GetValueOrDefault(m.Groups[2].Value, string.Empty)))
                .OrderBy(m => m.Codigo, StringComparer.Ordinal),
        ];
    }

    /// <summary>Busca sin distinguir tildes ni mayúsculas; primero los que empiezan con el texto y las capitales (código terminado en 001).</summary>
    public static IReadOnlyList<Municipio> Buscar(IReadOnlyList<Municipio> todos, string? texto, int maximo = 30)
    {
        ArgumentNullException.ThrowIfNull(todos);
        var buscado = Normalizar(texto);
        if (buscado.Length < 2)
        {
            return [];
        }

        return
        [
            .. todos
                .Select(m => (Municipio: m, Nombre: Normalizar(m.Nombre), Departamento: Normalizar(m.Departamento)))
                .Where(x => x.Nombre.Contains(buscado, StringComparison.Ordinal) || $"{x.Nombre} {x.Departamento}".Contains(buscado, StringComparison.Ordinal))
                .OrderBy(x => x.Nombre.StartsWith(buscado, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(x => x.Municipio.Codigo.EndsWith("001", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(x => x.Nombre, StringComparer.Ordinal)
                .Take(maximo)
                .Select(x => x.Municipio),
        ];
    }

    public static string Normalizar(string? texto)
    {
        var descompuesto = (texto ?? string.Empty).Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var sinTildes = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            sinTildes.Append(c);
        }

        return sinTildes.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\('(\d{2})',\s*'CO',\s*'([^']+)'\)", RegexOptions.CultureInvariant)]
    private static partial Regex Departamento();

    [GeneratedRegex(@"\('(\d{5})',\s*'(\d{2})',\s*'((?:[^']|'')+)',\s*'[A-Z_]+'\)", RegexOptions.CultureInvariant)]
    private static partial Regex MunicipioFila();
}
