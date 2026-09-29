using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Pos.Licensing.Contracts;

/// <summary>
/// Clave de licencia legible (L-06): <c>POS-XXXXX-XXXXX-XXXXX-XXXXX</c>. Alfabeto de 32 símbolos sin 0/O ni 1/I (se dicta por
/// teléfono sin confusiones); 19 símbolos aleatorios (95 bits) y un dígito de control Luhn mod 32 al final, que detecta
/// cualquier símbolo mal copiado y la transposición de dos símbolos vecinos (salvo el par 2↔Z, límite conocido de Luhn
/// mod N). La nube solo guarda su hash y el prefijo visible.
/// </summary>
public static class LicenseKey
{
    public const string Prefix = "POS";

    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public const int GroupCount = 4;

    public const int GroupLength = 5;

    /// <summary>Símbolos sin contar el prefijo ni los guiones (el último es el dígito de control).</summary>
    public const int SymbolCount = GroupCount * GroupLength;

    /// <summary>Longitud de la forma canónica: <c>POS-XXXXX-XXXXX-XXXXX-XXXXX</c>.</summary>
    public const int CanonicalLength = 3 + (GroupCount * (GroupLength + 1));

    /// <summary>Largo del prefijo visible que se guarda y se muestra (<c>POS-XXXXX</c>).</summary>
    public const int VisiblePrefixLength = 9;

    /// <summary>Genera una clave nueva con un generador criptográfico.</summary>
    public static string Generate()
    {
        Span<char> symbols = stackalloc char[SymbolCount];
        for (var i = 0; i < SymbolCount - 1; i++)
        {
            symbols[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        symbols[^1] = ComputeCheckSymbol(symbols[..^1]);
        return Format(symbols);
    }

    /// <summary>
    /// Normaliza lo que escribió el usuario (minúsculas, espacios, sin guiones o sin el prefijo) a la forma canónica y
    /// verifica el alfabeto y el dígito de control.
    /// </summary>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 60)
        {
            return false;
        }

        var compact = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c is '-' or ' ' or '\t')
            {
                continue;
            }

            compact.Append(char.ToUpperInvariant(c));
        }

        var text = compact.ToString();
        if (text.StartsWith(Prefix, StringComparison.Ordinal) && text.Length == SymbolCount + Prefix.Length)
        {
            text = text[Prefix.Length..];
        }

        if (text.Length != SymbolCount || text.Any(c => !Alphabet.Contains(c, StringComparison.Ordinal)))
        {
            return false;
        }

        if (ComputeCheckSymbol(text.AsSpan(0, SymbolCount - 1)) != text[^1])
        {
            return false;
        }

        key = Format(text);
        return true;
    }

    public static bool IsValid(string? input) => TryNormalize(input, out _);

    /// <summary>Prefijo visible de una clave canónica (p. ej. <c>POS-7K3QD</c>): identifica la clave sin revelarla.</summary>
    public static string VisiblePrefix(string canonicalKey)
    {
        ArgumentNullException.ThrowIfNull(canonicalKey);
        if (canonicalKey.Length != CanonicalLength)
        {
            throw new ArgumentException("La clave no está en su forma canónica.", nameof(canonicalKey));
        }

        return canonicalKey[..VisiblePrefixLength];
    }

    /// <summary>Dígito de control Luhn mod N (N = 32) de los símbolos dados.</summary>
    internal static char ComputeCheckSymbol(ReadOnlySpan<char> symbols)
    {
        var n = Alphabet.Length;
        var factor = 2;
        var sum = 0;
        for (var i = symbols.Length - 1; i >= 0; i--)
        {
            var addend = factor * Alphabet.IndexOf(symbols[i], StringComparison.Ordinal);
            factor = factor == 2 ? 1 : 2;
            sum += (addend / n) + (addend % n);
        }

        return Alphabet[(n - (sum % n)) % n];
    }

    private static string Format(ReadOnlySpan<char> symbols)
    {
        var builder = new StringBuilder(CanonicalLength).Append(Prefix);
        for (var group = 0; group < GroupCount; group++)
        {
            builder.Append('-').Append(symbols.Slice(group * GroupLength, GroupLength));
        }

        return builder.ToString();
    }
}
