using System.Drawing;
using Pos.Desktop;

namespace Pos.Desktop.Tests;

public class ReglasNavegacionTests
{
    private static readonly Uri App = new("http://localhost:5480/");
    private static readonly Uri Caja = new("https://192.168.1.10:5443/");

    [Theory]
    [InlineData("http://localhost:5480/")]
    [InlineData("http://localhost:5480/admin/reportes?desde=2026-10-01")]
    [InlineData("http://LOCALHOST:5480/instalacion")]
    [InlineData("https://carga.businesspost.invalid/")]
    [InlineData("about:blank")]
    [InlineData("blob:http://localhost:5480/8a7c1e0c-2f8f-4b8e-9d6f-0a7f6a3b2c1d")]
    [InlineData(null)]
    public void Permite_la_propia_aplicacion(string? destino) =>
        ReglasNavegacion.Decidir(App, [], destino).ShouldBe(DecisionNavegacion.Permitir);

    [Theory]
    [InlineData("https://businesspost.tutiendanueva.com/pagar?codigo=ABC")]
    [InlineData("https://tutiendanueva.com/")]
    [InlineData("http://localhost:5490/status")]
    [InlineData("https://localhost:5480/")]
    [InlineData("http://127.0.0.1:5480/")]
    [InlineData("mailto:soporte@ejemplo.com")]
    public void Otros_sitios_se_abren_en_el_navegador(string destino) =>
        ReglasNavegacion.Decidir(App, [], destino).ShouldBe(DecisionNavegacion.AbrirEnNavegador);

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("edge://settings")]
    [InlineData("javascript:alert(1)")]
    [InlineData("about:settings")]
    [InlineData("blob:https://otro.com/123")]
    [InlineData("data:text/html,<h1>hola</h1>")]
    [InlineData("ms-settings:privacy")]
    [InlineData("http://usuario:clave@otro.com/")]
    [InlineData("no es una dirección")]
    public void Bloquea_esquemas_locales_o_desconocidos(string destino) =>
        ReglasNavegacion.Decidir(App, [], destino).ShouldBe(DecisionNavegacion.Bloquear);

    [Fact]
    public void Caja_adicional_solo_permite_el_equipo_principal()
    {
        ReglasNavegacion.Decidir(Caja, [], "https://192.168.1.10:5443/caja").ShouldBe(DecisionNavegacion.Permitir);
        ReglasNavegacion.Decidir(Caja, [], "http://192.168.1.10:5480/").ShouldBe(DecisionNavegacion.AbrirEnNavegador);
        ReglasNavegacion.Decidir(Caja, [], "http://localhost:5480/").ShouldBe(DecisionNavegacion.AbrirEnNavegador);
    }

    [Fact]
    public void Origenes_extra_configurados_se_abren_en_la_ventana()
    {
        Uri[] extra = [new("https://businesspost.tutiendanueva.com/")];

        ReglasNavegacion.Decidir(App, extra, "https://businesspost.tutiendanueva.com/portal").ShouldBe(DecisionNavegacion.Permitir);
        ReglasNavegacion.Decidir(App, extra, "https://tutiendanueva.com/").ShouldBe(DecisionNavegacion.AbrirEnNavegador);
    }

    [Theory]
    [InlineData("https://carga.businesspost.invalid/", true)]
    [InlineData("https://carga.businesspost.invalid/inter-latin.woff2", true)]
    [InlineData("http://localhost:5480/", false)]
    [InlineData(null, false)]
    public void Reconoce_la_pantalla_de_carga(string? destino, bool esperado) => ReglasNavegacion.EsCarga(destino).ShouldBe(esperado);

    [Theory]
    [InlineData(0, "iniciando")]
    [InlineData(19, "iniciando")]
    [InlineData(20, "lento")]
    [InlineData(299, "lento")]
    [InlineData(300, "error")]
    public void Fases_de_la_pantalla_de_carga(int segundos, string esperada) =>
        EstadoCarga.Nombre(EstadoCarga.Fase(TimeSpan.FromSeconds(segundos))).ShouldBe(esperada);

    [Fact]
    public void Ventana_guardada_visible_se_conserva()
    {
        var pantalla = new Rectangle(0, 0, 1920, 1040);

        PreferenciasVentana.Ajustar(new Rectangle(100, 50, 1200, 800), [pantalla], pantalla).ShouldBe(new Rectangle(100, 50, 1200, 800));
    }

    [Fact]
    public void Ventana_en_un_monitor_desconectado_se_centra_en_la_pantalla_principal()
    {
        var pantalla = new Rectangle(0, 0, 1920, 1040);

        PreferenciasVentana.Ajustar(new Rectangle(2500, 100, 1200, 800), [pantalla], pantalla).ShouldBe(new Rectangle(360, 120, 1200, 800));
    }

    [Fact]
    public void Ventana_demasiado_pequena_usa_el_tamano_minimo()
    {
        var pantalla = new Rectangle(0, 0, 1366, 728);

        var resultado = PreferenciasVentana.Ajustar(new Rectangle(10, 10, 200, 100), [pantalla], pantalla);

        resultado.Size.ShouldBe(new Size(PreferenciasVentana.AnchoMinimo, PreferenciasVentana.AltoMinimo));
    }
}
