using System.Text.RegularExpressions;
using Pos.Installer.Setup.Logica;

namespace Pos.Installer.Setup.Tests;

/// <summary>
/// IN1: revisa el texto de los scripts del instalador (installer/*.iss y installer/scripts/*.ps1). Una edición con reemplazos que no
/// respetaban las barras invertidas dejó <c>'{sys}WindowsPowerShell\v1.0powershell.exe'</c> (con "\v" convertido en un carácter de
/// control): el desinstalador no podía ejecutar PowerShell y no detenía los servicios. Inno lo compila sin quejarse, por eso esta prueba.
/// </summary>
public partial class PaqueteInnoTests
{
    private static string Raiz()
    {
        for (var carpeta = new DirectoryInfo(AppContext.BaseDirectory); carpeta is not null; carpeta = carpeta.Parent)
        {
            if (File.Exists(Path.Combine(carpeta.FullName, "installer", "BusinessPost.iss")))
            {
                return carpeta.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró installer/BusinessPost.iss subiendo desde la carpeta de la prueba.");
    }

    public static TheoryData<string> Scripts()
    {
        var instalador = Path.Combine(Raiz(), "installer");
        var datos = new TheoryData<string>();
        foreach (var archivo in Directory.EnumerateFiles(instalador, "*.iss").Concat(Directory.EnumerateFiles(instalador, "*.ps1", SearchOption.AllDirectories)))
        {
            datos.Add(Path.GetRelativePath(instalador, archivo));
        }

        return datos;
    }

    private static string[] Lineas(string relativo) => File.ReadAllLines(Path.Combine(Raiz(), "installer", relativo));

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Los_scripts_no_tienen_caracteres_de_control(string script)
    {
        var malas = Lineas(script)
            .Select((linea, i) => (Numero: i + 1, Linea: linea))
            .Where(l => l.Linea.Any(c => char.IsControl(c) && c != '\t'))
            .Select(l => $"{script}:{l.Numero}")
            .ToList();

        malas.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Las_constantes_de_Inno_van_seguidas_de_una_barra(string script)
    {
        var malas = Lineas(script)
            .Select((linea, i) => (Numero: i + 1, Linea: linea))
            .Where(l => ConstanteSinBarra().IsMatch(l.Linea) || l.Linea.Contains("{sys}WindowsPowerShell", StringComparison.OrdinalIgnoreCase))
            .Select(l => $"{script}:{l.Numero}: {l.Linea.Trim()}")
            .ToList();

        malas.ShouldBeEmpty();
    }

    [Fact]
    public void PowerShell_se_llama_siempre_con_la_ruta_completa()
    {
        var texto = File.ReadAllText(Path.Combine(Raiz(), "installer", "BusinessPost.iss"));
        var llamadas = Regex.Matches(texto, @"WindowsPowerShell[^']*'").Select(m => m.Value).ToList();

        llamadas.ShouldNotBeEmpty();
        llamadas.ShouldAllBe(l => l == @"WindowsPowerShell\v1.0\powershell.exe'");
    }

    [Fact]
    public void El_paquete_conserva_el_BOM()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Raiz(), "installer", "BusinessPost.iss"));

        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
    }

    [Theory]
    [InlineData("ExitDatabaseFailed", SalidaPaquete.BaseDeDatos)]
    [InlineData("ExitNeedsSupport", SalidaPaquete.RestaurarConSoporte)]
    [InlineData("ExitActivationFailed", SalidaPaquete.VersionActiva)]
    public void Los_codigos_de_salida_del_paquete_coinciden_con_los_del_premium(string constante, int esperado)
    {
        var texto = File.ReadAllText(Path.Combine(Raiz(), "installer", "BusinessPost.iss"));
        var valor = Regex.Match(texto, $@"^\s*{constante}\s*=\s*(\d+);", RegexOptions.Multiline);

        valor.Success.ShouldBeTrue();
        int.Parse(valor.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(esperado);
    }

    [Fact]
    public void El_paquete_devuelve_el_codigo_de_fallo_y_no_usa_Stop_Service_que_bloquea()
    {
        var texto = File.ReadAllText(Path.Combine(Raiz(), "installer", "BusinessPost.iss"));

        texto.ShouldContain("function GetCustomSetupExitCode: Integer;");
        // IN2: Stop-Service -Force espera sin límite a un servicio trabado (solo puede aparecer en comentarios).
        Lineas("BusinessPost.iss").Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.Contains("Stop-Service", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    // {sys}, {app}, {tmp}… seguidas directamente de una letra o un número: les falta la barra invertida.
    [GeneratedRegex(@"\{(sys|app|tmp|cmd|win|commonappdata|autopf|pf|commonpf32|commonpf64|commondesktop|commonprograms|syswow64)\}[A-Za-z0-9]")]
    private static partial Regex ConstanteSinBarra();
}
