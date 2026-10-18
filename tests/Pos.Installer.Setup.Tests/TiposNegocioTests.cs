using Pos.Installer.Setup.Logica;

namespace Pos.Installer.Setup.Tests;

/// <summary>Tipo de negocio (Fase 7-C parte A): el instalador 1.2.1 ya no lo pregunta y crea la tienda como SUPERMERCADO.</summary>
public class TiposNegocioTests
{
    [Theory]
    [InlineData("PHARMACY", "PHARMACY")]
    [InlineData(" fresh_market ", "FRESH_MARKET")]
    [InlineData("hardware", "HARDWARE")]
    [InlineData(null, "SUPERMARKET")]
    [InlineData("", "SUPERMARKET")]
    [InlineData("PANADERIA", "SUPERMARKET")]
    public void Normaliza_el_tipo_elegido(string? elegido, string esperado) => TiposNegocio.Normalizar(elegido).ShouldBe(esperado);

    [Fact]
    public void Son_los_seis_tipos_aprobados()
    {
        TiposNegocio.Todos.ShouldBe(["SUPERMARKET", "FRESH_MARKET", "PHARMACY", "APPAREL", "HARDWARE", "OTHER"]);
        TiposNegocio.Todos.ShouldBe(Pos.Licensing.Contracts.BusinessProfileCodes.All);
    }

    [Fact]
    public void El_instalador_no_pide_datos_y_crea_la_tienda_como_supermercado_con_datos_provisionales()
    {
        string Recurso(string nombre)
        {
            using var recurso = typeof(TiposNegocio).Assembly.GetManifestResourceStream(nombre)!;
            return new StreamReader(recurso).ReadToEnd();
        }

        var html = Recurso("instalador.html");
        var js = Recurso("instalador.js");
        // Solo Bienvenida (Instalar), Instalando y ¡Listo! (más los enlaces de caja adicional y recuperar): ningún campo de licencia ni cuenta.
        foreach (var pantalla in new[] { "data-s=\"licencia\"", "data-s=\"cuenta\"", "data-s=\"tipo\"", "data-s=\"negocio\"", "data-s=\"equipo\"" })
        {
            html.ShouldNotContain(pantalla);
        }

        html.ShouldContain("id=\"lnkAdicional\"");
        html.ShouldContain("id=\"lnkRecuperar\"");
        js.ShouldContain("tipoNegocio: \"SUPERMARKET\"");
        // Los mismos valores provisionales que reconoce el servidor (Company.ProvisionalNit y Company.ProvisionalAddress).
        js.ShouldContain("numero: \"0\", dv: \"0\"");
        js.ShouldContain("direccion: \"Por completar\"");
    }
}
