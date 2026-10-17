using Pos.Installer.Setup.Logica;

namespace Pos.Installer.Setup.Tests;

/// <summary>Paso "¿Qué tipo de negocio tienes?" (Fase 7-C parte A): el instalador solo envía códigos que el servidor acepta.</summary>
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
    public void La_pagina_del_instalador_ofrece_una_tarjeta_por_tipo()
    {
        using var recurso = typeof(TiposNegocio).Assembly.GetManifestResourceStream("instalador.html")!;
        var html = new StreamReader(recurso).ReadToEnd();
        foreach (var tipo in TiposNegocio.Todos)
        {
            html.ShouldContain($"data-tipo=\"{tipo}\"");
        }

        html.ShouldContain("¿Qué tipo de negocio tienes?");
    }
}
