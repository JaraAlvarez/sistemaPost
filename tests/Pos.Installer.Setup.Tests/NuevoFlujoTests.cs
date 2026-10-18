using Pos.Installer.Setup.Logica;

namespace Pos.Installer.Setup.Tests;

/// <summary>Instalador 1.2.1: no pide datos; el modo es la única caja salvo la caja adicional o al recuperar el principal.</summary>
public class ModoPorLicenciaTests
{
    [Theory]
    [InlineData(ModoEquipo.Unica, false, ModoEquipo.Unica)]
    [InlineData(ModoEquipo.Principal, false, ModoEquipo.Unica)]
    [InlineData(ModoEquipo.Adicional, false, ModoEquipo.Adicional)]
    [InlineData(ModoEquipo.Principal, true, ModoEquipo.Principal)]
    [InlineData(ModoEquipo.Unica, true, ModoEquipo.Unica)]
    public void La_pagina_solo_decide_la_caja_adicional_y_el_principal_al_recuperar(ModoEquipo pedido, bool conBackup, ModoEquipo esperado) =>
        ModoPorLicencia.Elegir(pedido, conBackup).ShouldBe(esperado);
}

/// <summary>La ventana cabe en la pantalla con el escalado de Windows (125 %, 150 %): si no, se achica con zoom.</summary>
public class TamanoVentanaTests
{
    [Fact]
    public void Con_espacio_queda_en_960x600_por_la_escala()
    {
        TamanoVentana.Calcular(1.25, 2560, 1400).ShouldBe((1200, 750, 1.0));
        TamanoVentana.Calcular(1.0, 1920, 1040).ShouldBe((960, 600, 1.0));
    }

    [Fact]
    public void En_una_pantalla_pequena_al_125_se_achica_para_que_se_vean_los_botones()
    {
        // 1366×768 al 125 %: área útil de unos 1366×720 (barra de tareas). Antes 1200×750 y "Continuar" quedaba fuera de la pantalla.
        var (ancho, alto, zoom) = TamanoVentana.Calcular(1.25, 1366, 720);
        alto.ShouldBeLessThanOrEqualTo(720 - TamanoVentana.Margen);
        ancho.ShouldBeLessThanOrEqualTo(1366);
        zoom.ShouldBeLessThan(1.0);
        ((double)ancho / alto).ShouldBe(960d / 600, 0.01);
    }

    [Fact]
    public void Nunca_baja_del_zoom_minimo() => TamanoVentana.Calcular(1.5, 800, 400).Zoom.ShouldBe(TamanoVentana.ZoomMinimo);
}
