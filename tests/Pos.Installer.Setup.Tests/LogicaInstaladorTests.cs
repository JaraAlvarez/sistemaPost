using Pos.Installer.Setup.Logica;
using Pos.SharedKernel.Fiscal;

namespace Pos.Installer.Setup.Tests;

/// <summary>Avance real leído del registro de Inno (las marcas que escribe installer/BusinessPost.iss).</summary>
public class RegistroInnoTests
{
    [Fact]
    public void Sigue_la_copia_las_etapas_y_el_resultado_con_el_formato_de_Inno()
    {
        var lector = new RegistroInno();
        string[] registro =
        [
            "2026-10-17 09:20:01.123   Log opened. (Time zone: UTC-05:00)",
            "2026-10-17 09:20:01.200   Instalador premium: modo principal",
            "2026-10-17 09:20:05.000   Progreso: 0",
            "2026-10-17 09:20:09.000   -- File entry --",
            "2026-10-17 09:20:12.000   Progreso: 47",
        ];
        foreach (var linea in registro)
        {
            lector.Procesar(linea);
        }

        lector.Etapa.ShouldBe(Etapa.CopiandoProgramas);
        lector.PorcentajeCopia.ShouldBe(47);
        lector.PorcentajeGeneral.ShouldBe(47 * 35 / 100);

        lector.Procesar("2026-10-17 09:22:47.123   Paso: Revisando los archivos con Windows Defender (la primera vez puede tardar varios minutos)…").ShouldBeTrue();
        lector.Etapa.ShouldBe(Etapa.RevisandoDefender);
        lector.PorcentajeCopia.ShouldBe(100);

        lector.Procesar("2026-10-17 09:28:00.000   Paso: Creando la base de datos…");
        lector.Etapa.ShouldBe(Etapa.CreandoBaseDeDatos);
        lector.Procesar("2026-10-17 09:29:00.000   Paso: Registrando los servicios de BusinessPost…");
        lector.Etapa.ShouldBe(Etapa.PreparandoCaja);
        lector.Procesar("2026-10-17 09:29:30.000   Paso: Iniciando BusinessPost…");
        lector.Procesar("2026-10-17 09:29:31.000   Paso: Esperando a que BusinessPost responda…");
        lector.Etapa.ShouldBe(Etapa.IniciandoBusinessPost);
        lector.Resultado.ShouldBe(ResultadoInno.EnCurso);

        lector.Procesar("2026-10-17 09:30:10.000   Resultado: OK");
        lector.Resultado.ShouldBe(ResultadoInno.Correcto);
        lector.Error.ShouldBeNull();
    }

    [Fact]
    public void El_avance_nunca_retrocede_ni_se_sale_de_rango()
    {
        var lector = new RegistroInno();
        lector.Procesar("Progreso: 80");
        lector.Procesar("Progreso: 30");
        lector.PorcentajeCopia.ShouldBe(80);
        lector.Procesar("Progreso: 400");
        lector.PorcentajeCopia.ShouldBe(100);

        lector.Procesar("Paso: Registrando los servicios de BusinessPost…");
        lector.Procesar("Paso: Creando la base de datos…");
        lector.Etapa.ShouldBe(Etapa.PreparandoCaja);

        // Después de la copia ya no cuenta otro "Progreso" (Inno lo vuelve a llamar al registrar el desinstalador).
        lector.Procesar("Progreso: 5").ShouldBeFalse();
        lector.PorcentajeGeneral.ShouldBe(72);
    }

    [Fact]
    public void Guarda_el_primer_error_y_no_lo_tapa_un_resultado_posterior()
    {
        var lector = new RegistroInno();
        lector.Procesar("2026-10-17 09:28:00.000   Paso: Creando la base de datos…");
        lector.Procesar("2026-10-17 09:28:40.000   Resultado: ERROR Base de datos (código 1)");
        lector.Procesar("2026-10-17 09:28:41.000   Resultado: ERROR Servicio BusinessPost-Server (código 5)");
        lector.Procesar("2026-10-17 09:29:00.000   Resultado: OK");

        lector.Resultado.ShouldBe(ResultadoInno.Error);
        lector.Error.ShouldBe("Base de datos (código 1)");
    }

    [Fact]
    public void Distingue_quedo_instalado_pero_sin_responder()
    {
        var lector = new RegistroInno();
        lector.Procesar("Resultado: SIN_RESPUESTA");
        lector.Resultado.ShouldBe(ResultadoInno.SinRespuesta);
    }

    [Theory]
    [InlineData("Revisando los archivos con Windows Defender", Etapa.RevisandoDefender)]
    [InlineData("Recuperando los datos desde el backup…", Etapa.CreandoBaseDeDatos)]
    [InlineData("Esperando a que BusinessPost responda…", Etapa.IniciandoBusinessPost)]
    [InlineData("Algo nuevo que no conocemos", Etapa.CopiandoProgramas)]
    public void Cada_paso_del_instalador_tiene_su_etapa(string paso, Etapa esperada) => RegistroInno.EtapaDelPaso(paso).ShouldBe(esperada);

    [Fact]
    public void Ignora_lineas_vacias_y_sin_marcas() =>
        new RegistroInno().Procesar("2026-10-17 09:20:09.000   Dest filename: C:\\Program Files\\BusinessPost\\x.dll").ShouldBeFalse();
}

/// <summary>Dígito de verificación del NIT: el del instalador debe coincidir siempre con el del POS.</summary>
public class DigitoVerificacionTests
{
    [Theory]
    [InlineData("900123456", 8)]
    [InlineData("800197268", 4)]
    [InlineData("860002964", 4)]
    [InlineData("900.123.456", 8)]
    public void Calcula_el_DV_de_NITs_conocidos(string nit, int dv) => DigitoVerificacion.Calcular(nit).ShouldBe(dv);

    [Theory]
    [InlineData("1234")]
    [InlineData("1234567890123456")]
    [InlineData("90012345A")]
    [InlineData("900123456-8")]
    [InlineData(null)]
    public void Rechaza_lo_que_no_es_un_NIT(string? nit) => DigitoVerificacion.Calcular(nit).ShouldBeNull();

    [Fact]
    public void Coincide_con_el_calculo_del_POS_en_miles_de_NITs()
    {
        var azar = new Random(13);
        for (var i = 0; i < 5000; i++)
        {
            var nit = azar.NextInt64(10_000, 999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
            DigitoVerificacion.Calcular(nit).ShouldBe(Nit.ComputeCheckDigit(nit), nit);
        }
    }
}

/// <summary>Municipios del buscador leídos del mismo script de datos del POS.</summary>
public class MunicipiosTests
{
    private static readonly IReadOnlyList<Municipio> Todos = Leer();

    private static IReadOnlyList<Municipio> Leer()
    {
        using var recurso = typeof(DigitoVerificacion).Assembly.GetManifestResourceStream("seed_colombia.sql")!;
        using var lector = new StreamReader(recurso);
        return Municipios.Leer(lector.ReadToEnd());
    }

    [Fact]
    public void Lee_todos_los_municipios_del_DANE_con_su_departamento()
    {
        Todos.Count.ShouldBeGreaterThan(1100);
        Todos.ShouldContain(m => m.Codigo == "05001" && m.Nombre == "MEDELLÍN" && m.Departamento == "ANTIOQUIA");
        Todos.ShouldContain(m => m.Codigo == "11001" && m.Departamento == "BOGOTÁ, D.C.");
        Todos.Select(m => m.Codigo).ShouldBeUnique();
    }

    [Fact]
    public void Busca_sin_tildes_ni_mayusculas_y_pone_primero_lo_que_empieza_igual()
    {
        var medellin = Municipios.Buscar(Todos, "medellin");
        medellin[0].Codigo.ShouldBe("05001");

        Municipios.Buscar(Todos, "bogota").ShouldContain(m => m.Codigo == "11001");
        Municipios.Buscar(Todos, "SAN", 10).Count.ShouldBe(10);
        Municipios.Buscar(Todos, "m").ShouldBeEmpty();
    }
}

/// <summary>Línea de comandos del paquete clásico en silencio (parámetros de installer/BusinessPost.iss).</summary>
public class ArgumentosInnoTests
{
    [Fact]
    public void Instalar_equipo_principal_en_silencio_con_registro()
    {
        var linea = ArgumentosInno.Instalar(ModoEquipo.Principal, @"C:\Temp\instalacion.log");
        linea.ShouldContain("/VERYSILENT");
        linea.ShouldContain("/SUPPRESSMSGBOXES");
        linea.ShouldContain("/LOG=\"C:\\Temp\\instalacion.log\"");
        linea.ShouldContain("/PREMIUM=1");
        linea.ShouldContain("/MODO=principal");
        linea.ShouldNotContain("/SERVIDOR");
    }

    [Fact]
    public void Caja_adicional_lleva_el_equipo_principal_y_la_recuperacion_lleva_el_archivo_del_codigo_no_el_codigo()
    {
        ArgumentosInno.Instalar(ModoEquipo.Adicional, "r.log", "https://192.168.1.10:5443/", "ABC")
            .ShouldContain("/SERVIDOR=\"https://192.168.1.10:5443/\" /HUELLA=\"ABC\"");

        var recuperar = ArgumentosInno.Instalar(ModoEquipo.Unica, "r.log", backup: @"D:\copia.posbak", archivoCodigo: @"C:\Temp\codigo.txt");
        recuperar.ShouldContain("/BACKUP=\"D:\\copia.posbak\"");
        recuperar.ShouldContain("/RECUPERACION=\"C:\\Temp\\codigo.txt\"");
    }

    [Fact]
    public void Las_comillas_de_un_valor_no_rompen_la_linea_y_desinstalar_pasa_si_se_borran_los_datos()
    {
        ArgumentosInno.Comillas("a\"b").ShouldBe("\"ab\"");
        ArgumentosInno.Desinstalar(true, "d.log").ShouldContain("/BORRARDATOS=si");
        ArgumentosInno.Desinstalar(false, "d.log").ShouldContain("/BORRARDATOS=no");
    }
}

/// <summary>‑‑capturar (capturas del manual de instalación): solo funciona en maqueta, nunca durante una instalación de verdad.</summary>
public class ArgumentosCapturaTests
{
    [Fact]
    public void Solo_en_maqueta_y_con_un_archivo_png()
    {
        var captura = ArgumentosCaptura.Leer(["--maqueta", "--pantalla", "licencia", "--capturar", "salida/03-licencia.png", "--esperar", "4000"]);
        captura.ShouldNotBeNull();
        captura.Archivo.ShouldBe(Path.GetFullPath("salida/03-licencia.png"));
        captura.EsperaMs.ShouldBe(4000);

        ArgumentosCaptura.Leer(["--capturar", "x.png"]).ShouldBeNull();
        ArgumentosCaptura.Leer(["--desinstalar", "--capturar", "x.png"]).ShouldBeNull();
        ArgumentosCaptura.Leer(["--maqueta", "--capturar", "x.exe"]).ShouldBeNull();
        ArgumentosCaptura.Leer(["--maqueta", "--capturar"]).ShouldBeNull();
        ArgumentosCaptura.Leer(["--maqueta"]).ShouldBeNull();
    }

    [Fact]
    public void La_espera_tiene_un_valor_por_defecto_y_un_maximo()
    {
        ArgumentosCaptura.Leer(["--maqueta", "--capturar", "x.png"])!.EsperaMs.ShouldBe(ArgumentosCaptura.EsperaPorDefecto);
        ArgumentosCaptura.Leer(["--maqueta", "--capturar", "x.png", "--esperar", "999999"])!.EsperaMs.ShouldBe(ArgumentosCaptura.EsperaMaxima);
        ArgumentosCaptura.Leer(["--maqueta", "--capturar", "x.png", "--esperar", "-5"])!.EsperaMs.ShouldBe(0);
        ArgumentosCaptura.Leer(["--maqueta", "--capturar", "x.png", "--esperar", "abc"])!.EsperaMs.ShouldBe(ArgumentosCaptura.EsperaPorDefecto);
    }
}
