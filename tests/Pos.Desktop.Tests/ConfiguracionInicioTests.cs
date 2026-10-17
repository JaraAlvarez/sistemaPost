using Pos.Desktop;

namespace Pos.Desktop.Tests;

public class ConfiguracionInicioTests
{
    private const string Huella = "AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12";

    private static Func<string, string?> Archivos(Dictionary<string, string> archivos) =>
        nombre => archivos.TryGetValue(nombre, out var texto) ? texto : null;

    [Fact]
    public void Sin_configuracion_abre_localhost_5480()
    {
        var destino = ConfiguracionInicio.Resolver([], _ => null);

        destino.Direccion.AbsoluteUri.ShouldBe("http://localhost:5480/");
        destino.HuellaCertificado.ShouldBeNull();
        destino.Salud.AbsoluteUri.ShouldBe("http://localhost:5480/health/live");
    }

    [Fact]
    public void Equipo_principal_usa_el_puerto_de_server_json()
    {
        var destino = ConfiguracionInicio.Resolver([], Archivos(new() { ["server.json"] = """{ "Pos": { "Server": { "Port": 5481 } } }""" }));

        destino.Direccion.AbsoluteUri.ShouldBe("http://localhost:5481/");
    }

    [Fact]
    public void Caja_adicional_usa_la_direccion_y_la_huella_del_equipo_principal()
    {
        var updater = $$"""{ "Pos": { "Updates": { "Mode": "Terminal", "ServerUrl": "https://192.168.1.10:5443/", "ServerCertificateThumbprint": "{{Huella}}" } } }""";

        var destino = ConfiguracionInicio.Resolver([], Archivos(new() { ["updater.json"] = updater }));

        destino.Direccion.AbsoluteUri.ShouldBe("https://192.168.1.10:5443/");
        destino.HuellaCertificado.ShouldBe(Huella);
        destino.Salud.AbsoluteUri.ShouldBe("https://192.168.1.10:5443/health/live");
    }

    [Fact]
    public void Updater_json_del_equipo_principal_no_cambia_la_direccion()
    {
        var updater = """{ "Pos": { "Updates": { "Mode": "Server", "ServerUrl": "https://192.168.1.10:5443/" } } }""";

        var destino = ConfiguracionInicio.Resolver([], Archivos(new() { ["updater.json"] = updater }));

        destino.Direccion.AbsoluteUri.ShouldBe("http://localhost:5480/");
    }

    [Fact]
    public void Las_claves_no_distinguen_mayusculas_y_se_aceptan_comentarios()
    {
        var updater = """
            // escrito por el instalador
            { "pos": { "updates": { "mode": "terminal", "serverUrl": "https://10.0.0.5:5443/", } } }
            """;

        var destino = ConfiguracionInicio.Resolver([], Archivos(new() { ["updater.json"] = updater }));

        destino.Direccion.AbsoluteUri.ShouldBe("https://10.0.0.5:5443/");
    }

    [Fact]
    public void Desktop_json_tiene_prioridad_sobre_updater_json()
    {
        var destino = ConfiguracionInicio.Resolver([], Archivos(new()
        {
            ["desktop.json"] = """{ "StartUrl": "http://localhost:6000/", "AllowedOrigins": ["https://businesspost.tutiendanueva.com/", "no-es-url"] }""",
            ["updater.json"] = """{ "Pos": { "Updates": { "Mode": "Terminal", "ServerUrl": "https://192.168.1.10:5443/" } } }""",
        }));

        destino.Direccion.AbsoluteUri.ShouldBe("http://localhost:6000/");
        destino.OrigenesExtra.Select(o => o.AbsoluteUri).ShouldBe(["https://businesspost.tutiendanueva.com/"]);
    }

    [Theory]
    [InlineData("--url", "http://127.0.0.1:7600/")]
    [InlineData("--URL", "http://127.0.0.1:7600")]
    public void La_linea_de_comandos_tiene_prioridad(string argumento, string url)
    {
        var destino = ConfiguracionInicio.Resolver([argumento, url], Archivos(new() { ["desktop.json"] = """{ "StartUrl": "http://localhost:6000/" }""" }));

        destino.Direccion.AbsoluteUri.ShouldBe("http://127.0.0.1:7600/");
        destino.Fuente.ShouldBe("línea de comandos");
    }

    [Fact]
    public void Acepta_url_con_igual_y_huella_con_dos_puntos()
    {
        var conDosPuntos = string.Join(':', Huella.Chunk(2).Select(c => new string(c))).ToLowerInvariant();

        var destino = ConfiguracionInicio.Resolver(["--url=https://192.168.1.10:5443/", "--huella", conDosPuntos], _ => null);

        destino.Direccion.AbsoluteUri.ShouldBe("https://192.168.1.10:5443/");
        destino.HuellaCertificado.ShouldBe(Huella);
    }

    [Theory]
    [InlineData("file:///C:/Windows/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("localhost:5480")]
    [InlineData("http://usuario:clave@localhost:5480/")]
    [InlineData("")]
    public void Ignora_direcciones_no_validas(string url)
    {
        var destino = ConfiguracionInicio.Resolver(["--url", url], _ => null);

        destino.Direccion.AbsoluteUri.ShouldBe("http://localhost:5480/");
    }

    [Theory]
    [InlineData("{ no es json")]
    [InlineData("""{ "Pos": { "Server": { "Port": 70000 } } }""")]
    [InlineData("""{ "Pos": { "Server": { "Port": "abc" } } }""")]
    public void Configuracion_danada_vuelve_al_valor_por_defecto(string json)
    {
        var destino = ConfiguracionInicio.Resolver([], Archivos(new() { ["server.json"] = json }));

        destino.Direccion.ShouldBe(ConfiguracionInicio.PorDefecto);
    }

    [Theory]
    [InlineData("corta")]
    [InlineData("ZZ12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12")]
    [InlineData(null)]
    public void Huella_invalida_se_descarta(string? huella) => ConfiguracionInicio.Huella(huella).ShouldBeNull();
}
