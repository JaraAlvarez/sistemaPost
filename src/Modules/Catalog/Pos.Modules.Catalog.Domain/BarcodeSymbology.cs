namespace Pos.Modules.Catalog.Domain;

/// <summary>Simbología con la que se dibuja un código en una etiqueta.</summary>
public enum BarcodeSymbol
{
    Ean13,
    Ean8,
    Code128,
}

/// <summary>
/// Código listo para dibujar: <c>Modules</c> trae un elemento por módulo (true = barra negra, false = espacio), sin las zonas en blanco
/// de los lados; <c>Text</c> es lo que se imprime debajo (los dígitos o el texto codificado).
/// </summary>
public sealed record BarcodeDrawing(BarcodeSymbol Symbol, string Text, IReadOnlyList<bool> Modules)
{
    /// <summary>Módulos en blanco que se dejan a cada lado para que el lector lo reconozca (EAN: 11 a la izquierda; CODE128: 10).</summary>
    public int QuietZone => Symbol == BarcodeSymbol.Code128 ? 10 : 11;

    /// <summary>Ancho total en módulos incluidas las zonas en blanco.</summary>
    public int TotalModules => Modules.Count + (2 * QuietZone);
}

/// <summary>
/// Codificación de barras para las etiquetas de góndola y los adhesivos (EAN-13, EAN-8 y CODE128), dibujadas como barras vectoriales en el PDF
/// sin bibliotecas externas. Pura y determinista: el mismo texto produce siempre los mismos módulos.
/// </summary>
public static class BarcodeSymbology
{
    private static readonly string[] LeftOdd =
        ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];

    private static readonly string[] Ean13Parity =
        ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

    /// <summary>Anchos (barra, espacio, barra…) de los 107 símbolos de CODE128: 0–102 datos, 103–105 inicio A/B/C y 106 parada.</summary>
    private static readonly string[] Code128Table =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    /// <summary>Anchos de cada símbolo CODE128 (para verificarlos o leerlos de vuelta en las pruebas).</summary>
    public static IReadOnlyList<string> Code128Widths => Code128Table;

    public const int StartB = 104;
    public const int StartC = 105;
    public const int CodeB = 100;
    public const int CodeC = 99;
    public const int Stop = 106;

    /// <summary>
    /// Elige la simbología: EAN-13 (también los internos 29 y los UPC-A normalizados a 13 dígitos) y EAN-8 cuando el dígito de control es
    /// válido; cualquier otro texto (SKU, códigos alfanuméricos) va en CODE128. Devuelve null si el texto no se puede codificar.
    /// </summary>
    public static BarcodeDrawing? Encode(string? code)
    {
        var text = (code ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.All(char.IsAsciiDigit))
        {
            if (text.Length == 12 && Barcodes.HasValidCheckDigit("0" + text))
            {
                text = "0" + text;
            }

            if (text.Length == 13 && Barcodes.HasValidCheckDigit(text))
            {
                return new BarcodeDrawing(BarcodeSymbol.Ean13, text, Ean13(text));
            }

            if (text.Length == 8 && Barcodes.HasValidCheckDigit(text))
            {
                return new BarcodeDrawing(BarcodeSymbol.Ean8, text, Ean8(text));
            }
        }

        return Code128Values(text) is { } values ? new BarcodeDrawing(BarcodeSymbol.Code128, text, Code128Modules(values)) : null;
    }

    /// <summary>EAN-13 (13 dígitos con el de control): guarda 101, 6 dígitos L/G según el primero, guarda 01010, 6 dígitos R, guarda 101 = 95 módulos.</summary>
    public static IReadOnlyList<bool> Ean13(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length != 13 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("EAN-13 exige 13 dígitos.", nameof(digits));
        }

        var parity = Ean13Parity[digits[0] - '0'];
        var bits = new System.Text.StringBuilder(95);
        bits.Append("101");
        for (var i = 1; i <= 6; i++)
        {
            var d = digits[i] - '0';
            bits.Append(parity[i - 1] == 'L' ? LeftOdd[d] : LeftEven(d));
        }

        bits.Append("01010");
        for (var i = 7; i <= 12; i++)
        {
            bits.Append(Right(digits[i] - '0'));
        }

        bits.Append("101");
        return ToModules(bits.ToString());
    }

    /// <summary>EAN-8: guarda 101, 4 dígitos L, guarda 01010, 4 dígitos R, guarda 101 = 67 módulos.</summary>
    public static IReadOnlyList<bool> Ean8(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length != 8 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("EAN-8 exige 8 dígitos.", nameof(digits));
        }

        var bits = new System.Text.StringBuilder(67);
        bits.Append("101");
        for (var i = 0; i < 4; i++)
        {
            bits.Append(LeftOdd[digits[i] - '0']);
        }

        bits.Append("01010");
        for (var i = 4; i < 8; i++)
        {
            bits.Append(Right(digits[i] - '0'));
        }

        bits.Append("101");
        return ToModules(bits.ToString());
    }

    /// <summary>
    /// Valores CODE128 (inicio, datos, dígito de control y parada). Los tramos de 4 o más dígitos en número par van en el subconjunto C (dos
    /// dígitos por símbolo); lo demás en el B (ASCII 32–126). Null si hay caracteres fuera del ASCII imprimible.
    /// </summary>
    public static IReadOnlyList<int>? Code128Values(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Any(c => c is < ' ' or > '~'))
        {
            return null;
        }

        var values = new List<int>(text.Length + 4);
        var position = 0;
        char? set = null;
        while (position < text.Length)
        {
            var run = DigitRun(text, position);
            var useC = run >= 4 || (run >= 2 && run == text.Length - position && set == 'C');
            if (useC)
            {
                var pairs = run / 2;
                if (set != 'C')
                {
                    values.Add(set is null ? StartC : CodeC);
                    set = 'C';
                }

                for (var p = 0; p < pairs; p++)
                {
                    values.Add(((text[position] - '0') * 10) + (text[position + 1] - '0'));
                    position += 2;
                }
            }
            else
            {
                if (set != 'B')
                {
                    values.Add(set is null ? StartB : CodeB);
                    set = 'B';
                }

                values.Add(text[position] - 32);
                position++;
            }
        }

        var checksum = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            checksum += values[i] * i;
        }

        values.Add(checksum % 103);
        values.Add(Stop);
        return values;
    }

    /// <summary>Módulos de una secuencia de valores CODE128 (cada símbolo 11 módulos; la parada 13).</summary>
    public static IReadOnlyList<bool> Code128Modules(IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var modules = new List<bool>((values.Count * 11) + 2);
        foreach (var value in values)
        {
            var widths = Code128Table[value];
            for (var i = 0; i < widths.Length; i++)
            {
                modules.AddRange(Enumerable.Repeat(i % 2 == 0, widths[i] - '0'));
            }
        }

        return modules;
    }

    private static int DigitRun(string text, int start)
    {
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return end - start;
    }

    private static string Right(int digit) => new([.. LeftOdd[digit].Select(c => c == '0' ? '1' : '0')]);

    private static string LeftEven(int digit) => new([.. Right(digit).Reverse()]);

    private static bool[] ToModules(string bits) => [.. bits.Select(c => c == '1')];
}
