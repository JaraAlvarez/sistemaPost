using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using Pos.Installer.Setup.Logica;
using Pos.Installer.Setup.Servicios;
using Pos.Licensing.Contracts;

namespace Pos.Installer.Setup.Tests;

/// <summary>Responde en orden las respuestas dadas (la última se repite) y cuenta las peticiones por ruta.</summary>
internal sealed class ServidorSimulado(params Func<HttpRequestMessage, HttpResponseMessage>[] respuestas) : HttpMessageHandler
{
    private int _n;

    public List<string> Rutas { get; } = [];

    public static HttpResponseMessage Json(HttpStatusCode estado, string cuerpo) =>
        new(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Rutas.Add(request.RequestUri!.AbsolutePath);
        var i = Math.Min(_n++, respuestas.Length - 1);
        return Task.FromResult(respuestas[i](request));
    }
}

/// <summary>A3: el final de la instalación espera a que la base de datos esté lista y tolera los 503 de la migración.</summary>
public class ServidorLocalTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> SinEspera = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task Configurado_reintenta_mientras_el_servidor_migra_y_no_lanza_excepciones()
    {
        var servidor = new ServidorSimulado(
            _ => ServidorSimulado.Json(HttpStatusCode.ServiceUnavailable, """{"code":"SYSTEM.DATABASE_UNAVAILABLE","detail":"Migrando"}"""),
            _ => throw new HttpRequestException("Conexión rechazada"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>no es json</html>") },
            _ => ServidorSimulado.Json(HttpStatusCode.OK, """{"isCompleted":true}"""));
        using var local = new ServidorLocal(servidor, SinEspera);

        var respuesta = await local.ConfiguradoAsync(TestContext.Current.CancellationToken);

        respuesta.Ok.ShouldBeTrue(respuesta.Mensaje);
        respuesta.Dato.ShouldBeTrue();
        servidor.Rutas.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Configurado_devuelve_un_mensaje_si_la_base_nunca_queda_lista()
    {
        var servidor = new ServidorSimulado(_ => ServidorSimulado.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        using var local = new ServidorLocal(servidor, SinEspera);

        var respuesta = await local.ConfiguradoAsync(TestContext.Current.CancellationToken, intentos: 3);

        respuesta.Ok.ShouldBeFalse();
        respuesta.Mensaje!.ShouldContain("preparando la base de datos");
        servidor.Rutas.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Configurado_con_una_tienda_nueva_responde_falso()
    {
        using var local = new ServidorLocal(new ServidorSimulado(_ => ServidorSimulado.Json(HttpStatusCode.OK, """{"isCompleted":false}""")), SinEspera);
        var respuesta = await local.ConfiguradoAsync(TestContext.Current.CancellationToken);
        (respuesta.Ok, respuesta.Dato).ShouldBe((true, false));
    }

    [Fact]
    public async Task Espera_health_ready_y_avisa_cuando_el_proceso_vive_pero_la_base_no_esta_lista()
    {
        var servidor = new ServidorSimulado(
            _ => throw new HttpRequestException("Aún no arranca"),
            r => new HttpResponseMessage(r.RequestUri!.AbsolutePath.EndsWith("live", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable),
            r => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var local = new ServidorLocal(servidor, SinEspera);
        var avisos = 0;

        var resultado = await local.EsperarListoAsync(TimeSpan.FromMinutes(5), () => avisos++, null, TestContext.Current.CancellationToken);

        resultado.ShouldBe(EsperaServidor.Listo);
        avisos.ShouldBe(1, "\"Preparando la base de datos…\" se avisa una vez.");
        servidor.Rutas.ShouldContain("/health/ready");
    }

    [Fact]
    public async Task Si_la_base_no_queda_lista_a_tiempo_lo_distingue_de_un_servidor_que_no_responde()
    {
        var ahora = DateTimeOffset.UnixEpoch;
        Func<TimeSpan, CancellationToken, Task> avanzar = (t, _) =>
        {
            ahora += t;
            return Task.CompletedTask;
        };

        using (var vivo = new ServidorLocal(new ServidorSimulado(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), avanzar))
        {
            (await vivo.EsperarListoAsync(TimeSpan.FromSeconds(30), null, () => ahora, TestContext.Current.CancellationToken)).ShouldBe(EsperaServidor.SinBaseDeDatos);
        }

        using var muerto = new ServidorLocal(new ServidorSimulado(_ => throw new HttpRequestException("No")), avanzar);
        (await muerto.EsperarListoAsync(TimeSpan.FromSeconds(30), null, () => ahora, TestContext.Current.CancellationToken)).ShouldBe(EsperaServidor.SinRespuesta);
    }
}

/// <summary>B9: lectura incremental del registro de Inno sin saltar bytes ni partir letras con tilde.</summary>
public class LectorIncrementalTests
{
    [Fact]
    public void Una_letra_con_tilde_partida_entre_dos_lecturas_se_conserva()
    {
        var bytes = Encoding.UTF8.GetBytes("Paso: Creando la base de datos…\nPaso: Iniciando BusinessPost\n");
        var corte = Array.IndexOf(bytes, (byte)0xE2) + 1; // en medio de "…" (E2 80 A6)
        using var flujo = new MemoryStream();
        var lector = new LectorIncremental();

        flujo.Write(bytes, 0, corte);
        lector.Leer(flujo).ShouldBeEmpty("Todavía no hay una línea completa.");
        lector.Posicion.ShouldBe(corte);

        flujo.Write(bytes, corte, bytes.Length - corte);
        lector.Leer(flujo).ShouldBe(["Paso: Creando la base de datos…", "Paso: Iniciando BusinessPost"]);
        lector.Posicion.ShouldBe(bytes.Length);
    }

    [Fact]
    public void Lee_por_partes_sin_perder_ni_repetir_lineas_y_quita_el_retorno_de_carro()
    {
        using var flujo = new MemoryStream();
        var lector = new LectorIncremental();
        var todas = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            var linea = Encoding.UTF8.GetBytes($"Progreso: {i} ñandú\r\n");
            flujo.Write(linea, 0, linea.Length / 2);
            todas.AddRange(lector.Leer(flujo));
            flujo.Write(linea, linea.Length / 2, linea.Length - (linea.Length / 2));
            todas.AddRange(lector.Leer(flujo));
        }

        todas.ShouldBe(Enumerable.Range(0, 50).Select(i => $"Progreso: {i} ñandú"));
    }
}

/// <summary>B11 y B12: el backup que creó el migrador y las rutas de la copia temporal del desinstalador.</summary>
public class RutasYSalidaTests
{
    [Fact]
    public void Toma_la_ruta_exacta_que_informo_el_migrador()
    {
        const string salida = "Iniciando…\r\nBackup MANUAL verificado: D:\\USB\\BusinessPost-20261017-1030-MANUAL.posbak (12.3 MB, 120 objetos, sello #44).\r\n";
        SalidaMigrador.RutaBackup(salida).ShouldBe(@"D:\USB\BusinessPost-20261017-1030-MANUAL.posbak");
        SalidaMigrador.RutaBackup("No se pudo conectar").ShouldBeNull();
    }

    [Fact]
    public void Sin_ruta_legible_toma_solo_el_backup_nuevo_de_la_carpeta()
    {
        var carpeta = Directory.CreateTempSubdirectory("backup-final-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(carpeta, "viejo.posbak"), "x");
            var antes = SalidaMigrador.Backups(carpeta);
            SalidaMigrador.BackupNuevo(carpeta, antes).ShouldBeNull();
            File.WriteAllText(Path.Combine(carpeta, "nuevo.posbak"), "x");
            SalidaMigrador.BackupNuevo(carpeta, antes).ShouldBe(Path.Combine(carpeta, "nuevo.posbak"));
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\BusinessPost\instalador\BusinessPost-Setup.exe", @"C:\Program Files\BusinessPost", true)]
    [InlineData(@"C:\Program Files\BusinessPost\instalador\BusinessPost-Setup.exe", @"C:\Program Files\BusinessPost\", true)]
    [InlineData(@"c:\program files\businesspost\x.exe", @"C:\Program Files\BusinessPost", true)]
    [InlineData(@"C:\Program Files\BusinessPost2\x.exe", @"C:\Program Files\BusinessPost", false)]
    [InlineData(@"C:\Program Files\BusinessPost-Viejo\x.exe", @"C:\Program Files\BusinessPost", false)]
    [InlineData(null, @"C:\Program Files\BusinessPost", false)]
    public void Esta_dentro_exige_el_separador(string? ruta, string carpeta, bool esperado) =>
        Rutas.EstaDentro(ruta, carpeta).ShouldBe(esperado);
}

/// <summary>I5/I6: el modo con el que ya estaba instalado el equipo, su confirmación al cambiarlo y los avisos del paquete.</summary>
public class ModoYAvisosTests
{
    [Theory]
    [InlineData("""{ "Pos": { "Updates": { "Mode": "Terminal" } } }""", null, ModoEquipo.Adicional)]
    [InlineData(null, """{ "Pos": { "Database": { "Edition": "MULTI" } } }""", ModoEquipo.Principal)]
    [InlineData(null, """{ "Pos": { "Database": { "Edition": "SINGLE" } } }""", ModoEquipo.Unica)]
    [InlineData(null, null, null)]
    public void Detecta_el_modo_instalado_como_el_paquete(string? updater, string? server, ModoEquipo? esperado) =>
        ModoInstalado.Detectar(updater, server).ShouldBe(esperado);

    [Theory]
    [InlineData("principal", """{ "Pos": { "Database": { "Edition": "SINGLE" } } }""", ModoEquipo.Principal)]
    [InlineData("adicional", null, ModoEquipo.Adicional)]
    [InlineData("UNICA", """{ "Pos": { "Database": { "Edition": "MULTI" } } }""", ModoEquipo.Unica)]
    [InlineData("", """{ "Pos": { "Database": { "Edition": "MULTI" } } }""", ModoEquipo.Principal)]
    [InlineData("otro", null, null)]
    public void IN10_primero_el_modo_que_guardo_Inno_y_despues_la_configuracion(string? guardado, string? server, ModoEquipo? esperado) =>
        ModoInstalado.Detectar(guardado, null, server).ShouldBe(esperado);

    [Fact]
    public void IL5_migrar_o_restaurar_es_un_paso_que_no_se_corta()
    {
        var lector = new RegistroInno();
        lector.TocoElEquipo.ShouldBeFalse();
        lector.Procesar("2026-10-17 09:20:01.123   Paso: Deteniendo BusinessPost para actualizar sus archivos…");
        (lector.TocoElEquipo, lector.EnPasoCritico).ShouldBe((true, false));
        lector.Procesar("2026-10-17 09:21:01.123   Paso: Revisando los archivos con Windows Defender (la primera vez puede tardar varios minutos)…");
        lector.EnPasoCritico.ShouldBeFalse();
        lector.Procesar("2026-10-17 09:22:01.123   Paso: Creando la base de datos…");
        lector.EnPasoCritico.ShouldBeTrue();
        lector.Procesar("2026-10-17 09:23:01.123   Resultado: ERROR Base de datos (código 3)");
        lector.EnPasoCritico.ShouldBeTrue("P1: con el paquete vivo se espera igual, sin mirar el resultado (está volviendo atrás).");
    }

    [Fact]
    public void P2_en_una_caja_activar_la_version_nueva_ya_no_se_corta()
    {
        var lector = new RegistroInno();
        lector.Procesar("2026-10-17 09:21:01.123   Paso: Revisando los archivos con Windows Defender (la primera vez puede tardar varios minutos)…");
        lector.EnPasoCritico.ShouldBeFalse();
        lector.Procesar("2026-10-17 09:22:01.123   Paso: Activando la versión nueva de BusinessPost…");
        lector.EnPasoCritico.ShouldBeTrue();
    }

    [Fact]
    public void IR5_los_servicios_detenidos_por_el_paquete_se_reanudan_en_orden()
    {
        string[] registro =
        [
            "2026-10-17 09:20:01.123   Paso: Deteniendo BusinessPost para actualizar sus archivos…",
            "2026-10-17 09:20:01.200   Deteniendo BusinessPost-Updater",
            "2026-10-17 09:20:02.200   Deteniendo BusinessPost-Server",
            "2026-10-17 09:20:03.200   Deteniendo BusinessPost-DB",
            "2026-10-17 09:20:03.300   Deteniendo OtroServicio",
        ];

        RegistroInno.ServiciosDetenidos(registro).ShouldBe(["BusinessPost-DB", "BusinessPost-Server", "BusinessPost-Updater"]);
    }

    [Fact]
    public void IN7_el_codigo_de_salida_del_paquete_da_un_mensaje_claro()
    {
        SalidaPaquete.Mensaje(SalidaPaquete.RestaurarConSoporte, "Base de datos").ShouldContain("soporte restaure el backup previo");
        SalidaPaquete.Mensaje(SalidaPaquete.BaseDeDatos, "Base de datos").ShouldContain("sigue con la versión anterior");
        SalidaPaquete.Mensaje(SalidaPaquete.BaseDeDatos, "Recuperación desde el backup").ShouldContain("No se pudo recuperar el backup");
        SalidaPaquete.Mensaje(SalidaPaquete.VersionActiva, null).ShouldContain("vuelve a ejecutar el instalador");
        SalidaPaquete.Mensaje(SalidaPaquete.VersionActiva, null).ShouldContain("PRE_UPDATE");
        SalidaPaquete.Mensaje(5, null).ShouldContain("código 5");
    }

    [Fact]
    public void Cambiar_el_modo_va_en_los_parametros_solo_si_se_confirmo()
    {
        ArgumentosInno.Instalar(ModoEquipo.Principal, @"C:\t\r.log").ShouldNotContain("/CAMBIARMODO");
        ArgumentosInno.Instalar(ModoEquipo.Principal, @"C:\t\r.log", cambiarModo: true).ShouldContain("/CAMBIARMODO=si");
    }

    [Fact]
    public void Los_avisos_del_paquete_se_guardan_sin_repetir_y_las_advertencias_tecnicas_no()
    {
        var lector = new RegistroInno();
        lector.Procesar("2026-10-17 09:20:01.123   Aviso: La red de este equipo está marcada como \"Pública\".");
        lector.Procesar("2026-10-17 09:20:02.123   Aviso: La red de este equipo está marcada como \"Pública\".");
        lector.Procesar("2026-10-17 09:20:03.123   Advertencia: no se pudo configurar la recuperación de BusinessPost-Server.");
        lector.Avisos.ShouldBe(["La red de este equipo está marcada como \"Pública\"."]);
    }
}
