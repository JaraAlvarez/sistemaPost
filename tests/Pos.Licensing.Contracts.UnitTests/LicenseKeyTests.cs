namespace Pos.Licensing.Contracts.UnitTests;

public class LicenseKeyTests
{
    [Fact]
    public void Genera_claves_con_el_formato_y_el_alfabeto_sin_simbolos_ambiguos()
    {
        for (var i = 0; i < 200; i++)
        {
            var key = LicenseKey.Generate();

            key.Length.ShouldBe(LicenseKey.CanonicalLength);
            key.ShouldMatch("^POS(-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{5}){4}$");
            key.ShouldNotContain('0');
            key[3..].ShouldNotContain('O');
            key.ShouldNotContain('1');
            key.ShouldNotContain('I');
            LicenseKey.IsValid(key).ShouldBeTrue();
        }
    }

    [Fact]
    public void Dos_claves_generadas_son_distintas() =>
        Enumerable.Range(0, 500).Select(_ => LicenseKey.Generate()).Distinct(StringComparer.Ordinal).Count().ShouldBe(500);

    [Theory]
    [InlineData(" pos-%s ")]
    [InlineData("%c")]
    [InlineData("POS%c")]
    [InlineData("%s")]
    public void Normaliza_lo_que_escribe_el_usuario(string template)
    {
        var key = LicenseKey.Generate();
        var compact = key.Replace("-", string.Empty, StringComparison.Ordinal)[3..];
        var input = template.Replace("%s", key[4..].ToLowerInvariant(), StringComparison.Ordinal).Replace("%c", compact, StringComparison.Ordinal);

        LicenseKey.TryNormalize(input, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(key);
    }

    [Fact]
    public void El_digito_de_control_detecta_cualquier_simbolo_cambiado()
    {
        var key = LicenseKey.Generate();
        var positions = Enumerable.Range(0, key.Length).Where(i => key[i] != '-' && i > 3);
        foreach (var position in positions)
        {
            foreach (var replacement in LicenseKey.Alphabet.Where(c => c != key[position]))
            {
                var altered = string.Concat(key.AsSpan(0, position), replacement.ToString(), key.AsSpan(position + 1));
                LicenseKey.IsValid(altered).ShouldBeFalse($"No detectó el cambio de la posición {position} por {replacement}");
            }
        }
    }

    [Fact]
    public void El_digito_de_control_detecta_la_transposicion_de_dos_simbolos_vecinos_salvo_2_y_Z()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var symbols = LicenseKey.Generate().Replace("-", string.Empty, StringComparison.Ordinal)[3..].ToCharArray();
            for (var i = 0; i < symbols.Length - 1; i++)
            {
                // Límite conocido de Luhn mod N: no detecta el intercambio de los símbolos de valor 0 y N-1 ('2' y 'Z').
                if (symbols[i] == symbols[i + 1] || (symbols[i] is '2' or 'Z' && symbols[i + 1] is '2' or 'Z'))
                {
                    continue;
                }

                var swapped = (char[])symbols.Clone();
                (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
                LicenseKey.IsValid(new string(swapped)).ShouldBeFalse();
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("POS-ABCDE")]
    [InlineData("POS-00000-00000-00000-00000")]
    [InlineData("POS-OOOOO-IIIII-11111-00000")]
    [InlineData("POS-ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2")]
    public void Rechaza_textos_que_no_son_claves(string? input) => LicenseKey.TryNormalize(input, out _).ShouldBeFalse();

    [Fact]
    public void Rechaza_textos_enormes() => LicenseKey.IsValid(new string('A', 500)).ShouldBeFalse();

    [Fact]
    public void El_prefijo_visible_son_los_primeros_nueve_caracteres()
    {
        var key = LicenseKey.Generate();

        LicenseKey.VisiblePrefix(key).ShouldBe(key[..9]);
        Should.Throw<ArgumentException>(() => LicenseKey.VisiblePrefix("POS-ABC"));
        Should.Throw<ArgumentNullException>(() => LicenseKey.VisiblePrefix(null!));
    }
}
