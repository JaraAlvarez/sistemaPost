using System.Text;
using Pos.Terminal.Agent.Scales;

namespace Pos.Terminal.Agent.Tests;

/// <summary>Protocolos de báscula (Toledo 8217, CAS 22 bytes y genérico), separación de tramas, lector con tiempo de espera y simulador.</summary>
public class ScaleDriverTests
{
    private static readonly Toledo8217Driver Toledo = new();
    private static readonly CasDriver Cas = new();
    private static readonly GenericAsciiDriver Generic = new();

    private static string Latin1(params byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static ScaleTarget Target(string driver, ScaleReadMode? mode = null, int timeoutMs = 300)
    {
        var target = ScaleSettingsResolver.TryResolve(
            new ScaleSettings { Driver = driver, Port = "COM3", ReadMode = mode?.ToString().ToUpperInvariant(), TimeoutMs = timeoutMs }, simulatorAllowed: true, out var error);
        error.ShouldBeEmpty();
        return target!;
    }

    // ------------------------------------------------------------------------------------------ Toledo 8217

    [Theory]
    [InlineData("\u000200.700", 0.700)]
    [InlineData("\u000212.345", 12.345)]
    [InlineData("00.700", 0.700)]
    [InlineData("\u000200700", 0.700)]
    public void Toledo_lee_el_peso_en_kg_estable(string frame, decimal kg)
    {
        var reading = Toledo.Parse(frame, impliedDecimals: 3).ShouldNotBeNull();

        reading.WeightKg.ShouldBe(kg);
        reading.Stable.ShouldBeTrue();
        reading.Overload.ShouldBeFalse();
        reading.Net.ShouldBeFalse();
    }

    [Fact]
    public void Toledo_peso_neto_cero_y_libras()
    {
        Toledo.Parse("\u000201.250N", 3)!.Net.ShouldBeTrue();
        Toledo.Parse("\u000200.000", 3)!.CenterOfZero.ShouldBeTrue();

        // WW.WW = libras (manual 8217): 2,00 lb = 0,907 kg.
        Toledo.Parse("\u000202.00", 3)!.WeightKg.ShouldBe(0.907m);
    }

    [Fact]
    public void Toledo_con_paridad_par_en_el_octavo_bit_se_lee_igual()
    {
        // '0' = 0x30 (dos unos: paridad 0); '7' = 0x37 (cinco unos: paridad 1 → 0xB7).
        Toledo.Parse(Latin1(0x02, 0x30, 0x30, 0x2E, 0xB7, 0x30, 0x30), 3)!.WeightKg.ShouldBe(0.700m);
    }

    [Theory]
    [InlineData(0x41, false, false, false)] // en movimiento
    [InlineData(0x42, true, true, false)]   // sobre la capacidad
    [InlineData(0x44, true, false, true)]   // bajo cero
    [InlineData(0x43, false, true, false)]  // movimiento y sobrepeso
    public void Toledo_byte_de_estado(int status, bool stable, bool overload, bool underZero)
    {
        var reading = Toledo.Parse(Latin1(0x02, (byte)'?', (byte)status), 3).ShouldNotBeNull();

        reading.WeightKg.ShouldBeNull();
        (reading.Stable, reading.Overload, reading.UnderZero, reading.Problem).ShouldBe((stable, overload, underZero, (string?)null));
    }

    [Fact]
    public void Toledo_estado_sin_el_bit_6_es_comando_invalido()
    {
        Toledo.Parse(Latin1(0x02, (byte)'?', 0x00 | 0x01), 3)!.Problem.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\u0002")]
    [InlineData("7")]
    [InlineData("70")]
    [InlineData("HOLA")]
    [InlineData("ST,GS,+0000.700kg")]
    [InlineData("0.7.00")]
    public void Toledo_ignora_basura_y_tramas_cortadas(string frame) => Toledo.Parse(frame, 3).ShouldBeNull();

    // ------------------------------------------------------------------------------------------ CAS

    [Theory]
    [InlineData("ST,GS,+0000.700kg", 0.700, true, false)]
    [InlineData("ST,GS,  0.700kg", 0.700, true, false)]
    [InlineData("US,GS,+0001.250kg", 1.250, false, false)]
    [InlineData("ST,NT,+0000.500kg", 0.500, true, true)]
    [InlineData("ST,GS,   700 g", 0.700, true, false)]
    [InlineData("st,gs,+0000.700KG", 0.700, true, false)]
    public void Cas_lee_estable_inestable_y_neto(string frame, decimal kg, bool stable, bool net)
    {
        var reading = Cas.Parse(frame, 3).ShouldNotBeNull();

        reading.WeightKg.ShouldBe(kg);
        reading.Stable.ShouldBe(stable);
        reading.Net.ShouldBe(net);
    }

    [Fact]
    public void Cas_con_identificador_y_byte_de_lamparas()
    {
        // 22 bytes: "ST,GS," + id '1' + lámparas (bit 7 = 1) + 8 bytes de peso + "kg".
        var frame = "ST,GS,1" + (char)0xC1 + "  +0.700" + "kg";

        Cas.Parse(frame, 3)!.WeightKg.ShouldBe(0.700m);
    }

    [Fact]
    public void Cas_cero_negativo_y_sobrepeso()
    {
        Cas.Parse("ST,GS,+0000.000kg", 3)!.CenterOfZero.ShouldBeTrue();

        var negative = Cas.Parse("ST,GS,-0000.120kg", 3)!;
        negative.WeightKg.ShouldBe(-0.120m);
        negative.UnderZero.ShouldBeTrue();

        var overload = Cas.Parse("OL,GS,        kg", 3)!;
        overload.Overload.ShouldBeTrue();
        overload.Stable.ShouldBeFalse();
    }

    [Theory]
    [InlineData("ST,GS,10" + "0000.700" + "kg", 0.700)]   // id '1' y lámparas '0': antes se leía 100000,7 kg
    [InlineData("ST,GS,1\u00C1" + "  +0.700" + "  kg", 0.700)] // con los bytes vacíos antes de la unidad
    [InlineData("ST,GS,1\u00C1," + "  +1.250" + "kg", 1.250)]  // con coma después de las lámparas
    [InlineData("ST,GS,\u0001\u00C1" + "-  0.120" + "kg", -0.120)]
    [InlineData("ST,GS,7A" + "   2.500" + "kg", 2.500)]
    public void Cas_22_bytes_lee_el_peso_por_posicion_sin_pegar_identificador_ni_lamparas(string frame, decimal kg) =>
        Cas.Parse(frame, 3)!.WeightKg.ShouldBe(kg);

    [Fact]
    public void Cas_variante_corta_con_signo_sigue_leyendo_el_cuerpo_entero()
    {
        Cas.Parse("ST,GS,-00000.120kg", 3)!.WeightKg.ShouldBe(-0.120m);
        Cas.Parse("ST,GS,     0.700kg", 3)!.WeightKg.ShouldBe(0.700m);
        Cas.Parse("ST,GS,1\u00C1+0.7x00kg", 3).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("GS,+0000.700kg")]
    [InlineData(",+0000.700kg")]
    [InlineData("ST,GS,")]
    [InlineData("XX,GS,+0000.700kg")]
    [InlineData("\u000200.700")]
    public void Cas_ignora_basura_y_tramas_cortadas(string frame) => Cas.Parse(frame, 3).ShouldBeNull();

    // ------------------------------------------------------------------------------------------ Genérico

    [Theory]
    [InlineData("  0.700 kg", 0.700)]
    [InlineData("N.W.  1.250kg", 1.250)]
    [InlineData("00000700", 0.700)]
    [InlineData("+0700 g", 0.700)]
    [InlineData("1.5 lb", 0.680)]
    public void Generico_toma_el_ultimo_numero_y_su_unidad(string frame, decimal kg) => Generic.Parse(frame, 3)!.WeightKg.ShouldBe(kg);

    [Theory]
    [InlineData("NET   0.000 kg")]
    [InlineData("N.W.  1.250kg")]
    [InlineData("PESO NETO 0.500 kg")]
    [InlineData("NT0.500kg")]
    [InlineData("NETO 0.500 kg")]
    [InlineData("NET0.500kg")]
    [InlineData("0.500NET kg")]
    [InlineData("0.500kg NT")]
    [InlineData("ST,NT,+0000.500kg")]
    public void Generico_reconoce_el_peso_neto_de_la_tara_fisica(string frame) => Generic.Parse(frame, 3)!.Net.ShouldBeTrue();

    [Theory]
    [InlineData("  0.500 kg")]
    [InlineData("G.W.  0.500kg")]
    [InlineData("PESO BRUTO 0.500 kg")]
    [InlineData("UNIT 0.500 kg")]
    [InlineData("NETWORK 0.500 kg")]
    public void Generico_sin_marca_de_neto_es_bruto(string frame) => Generic.Parse(frame, 3)!.Net.ShouldBeFalse();

    [Fact]
    public void Generico_reconoce_US_y_OL_y_rechaza_texto_sin_numero()
    {
        Generic.Parse("US 0.700kg", 3)!.Stable.ShouldBeFalse();
        Generic.Parse("OL", 3)!.Overload.ShouldBeTrue();
        Generic.Parse("-0.050 kg", 3)!.UnderZero.ShouldBeTrue();
        Generic.Parse("HOLA", 3).ShouldBeNull();
        Generic.Parse("   ", 3).ShouldBeNull();
        Generic.ReportsStability.ShouldBeFalse();
    }

    // ------------------------------------------------------------------------------------------ Tramas y lector

    [Fact]
    public void Separa_tramas_por_CR_o_LF_y_descarta_las_vacias()
    {
        var buffer = new ScaleFrameBuffer();
        buffer.Append("\r\nST,GS,+0000.7"u8);
        buffer.TryTakeFrame(out _).ShouldBeFalse();

        buffer.Append("00kg\r\nUS,GS"u8);
        buffer.TryTakeFrame(out var frame).ShouldBeTrue();
        frame.ShouldBe("ST,GS,+0000.700kg");
        buffer.TryTakeFrame(out _).ShouldBeFalse();
    }

    [Fact]
    public void Ruido_sin_fin_de_linea_no_crece_sin_limite()
    {
        var buffer = new ScaleFrameBuffer();
        buffer.Append(new byte[10_000]);
        buffer.Append("\rST,GS,+0000.700kg\r"u8);

        buffer.TryTakeFrame(out var frame).ShouldBeTrue();
        frame.ShouldBe("ST,GS,+0000.700kg");
    }

    [Fact]
    public void Toledo_por_pedido_envia_W_y_lee_la_respuesta()
    {
        var device = new SimulatedScale();
        device.Set(0.700m);
        using var connection = new SimulatedScaleConnection(device, SimulatedProtocol.Toledo8217, continuous: false);

        var reading = ScaleProtocolReader.Read(connection, Toledo, Target(ScaleDrivers.Toledo8217Code));

        reading.WeightKg.ShouldBe(0.700m);
        reading.Stable.ShouldBeTrue();
        connection.Received.ShouldBe("W"u8.ToArray());
    }

    [Fact]
    public void Toledo_simulado_en_movimiento_negativo_y_sobrepeso()
    {
        var device = new SimulatedScale();
        using var connection = new SimulatedScaleConnection(device, SimulatedProtocol.Toledo8217, continuous: false);
        var target = Target(ScaleDrivers.Toledo8217Code);

        device.Set(1.2m, stable: false);
        ScaleProtocolReader.Read(connection, Toledo, target).Stable.ShouldBeFalse();

        device.Set(-0.1m);
        ScaleProtocolReader.Read(connection, Toledo, target).UnderZero.ShouldBeTrue();

        device.Set(40m, overload: true);
        ScaleProtocolReader.Read(connection, Toledo, target).Overload.ShouldBeTrue();
    }

    [Fact]
    public void Cas_continuo_salta_la_basura_y_toma_la_primera_trama_valida()
    {
        var device = new SimulatedScale();
        device.Set(2.345m);
        using var connection = new SimulatedScaleConnection(device, SimulatedProtocol.Cas, continuous: true)
        {
            Noise = "0.700kg\r\n\u0001\u0002ruido\r\n"u8.ToArray(),
        };

        var reading = ScaleProtocolReader.Read(connection, Cas, Target(ScaleDrivers.CasCode));

        reading.WeightKg.ShouldBe(2.345m);
        connection.Received.ShouldBeEmpty();
    }

    [Fact]
    public void Toledo_continuo_se_sincroniza_con_STX_y_descarta_la_trama_cortada()
    {
        // "0.700\r" es la cola de "STX 10.700 CR" (el búfer se vació a mitad de trama): antes se leía 0,700 kg.
        var device = new SimulatedScale();
        device.Set(10.700m);
        using var connection = new SimulatedScaleConnection(device, SimulatedProtocol.Toledo8217, continuous: true)
        {
            Noise = "0.700\r"u8.ToArray(),
        };

        var reading = ScaleProtocolReader.Read(connection, Toledo, Target(ScaleDrivers.Toledo8217Code, ScaleReadMode.Continuous));

        reading.WeightKg.ShouldBe(10.700m);
        connection.Received.ShouldBeEmpty();
    }

    [Fact]
    public void Toledo_continuo_toma_desde_el_ultimo_STX_aunque_llegue_con_el_bit_de_paridad()
    {
        var device = new SimulatedScale();
        device.Set(10.700m);
        using var connection = new SimulatedScaleConnection(device, SimulatedProtocol.Toledo8217, continuous: true)
        {
            Noise = [.. "5.1"u8, 0x82, .. "02.500"u8, 0x0D],
        };

        ScaleProtocolReader.Read(connection, Toledo, Target(ScaleDrivers.Toledo8217Code, ScaleReadMode.Continuous)).WeightKg.ShouldBe(2.500m);
    }

    [Fact]
    public void Un_peso_fuera_de_rango_es_una_trama_con_error_y_no_revienta()
    {
        // Genérico con decimales mal configurados o basura con muchos dígitos.
        var reading = Generic.Parse("99999999999999999999 kg", 3).ShouldNotBeNull();

        var weight = ScaleService.Build(reading, stable: true, tareKg: 0m, zeroBandKg: 0.02m, DateTimeOffset.UnixEpoch);

        weight.Status.ShouldBe(ScaleWeightDto.Error);
        (weight.WeightKg, weight.Grams, weight.Stable).ShouldBe(((decimal?)null, (int?)null, false));
        ScaleService.Build(new ScaleReading(ScaleService.MaxReadableKg, Stable: true), true, 0m, 0.02m, DateTimeOffset.UnixEpoch).Grams.ShouldBe(99_999_000);
    }

    [Theory]
    [InlineData(0.002, 0.020, ScaleWeightDto.ZeroWeight)]
    [InlineData(-0.005, 0.020, ScaleWeightDto.ZeroWeight)]
    [InlineData(-0.030, 0.020, ScaleWeightDto.NegativeWeight)]
    [InlineData(0.020, 0.020, ScaleWeightDto.ZeroWeight)]
    [InlineData(0.021, 0.020, ScaleWeightDto.Ok)]
    [InlineData(0.001, 0, ScaleWeightDto.Ok)]
    [InlineData(0, 0, ScaleWeightDto.ZeroWeight)]
    public void Banda_de_cero(decimal kg, decimal band, string expected) =>
        ScaleService.Build(new ScaleReading(kg, Stable: true, UnderZero: kg < 0m), true, 0m, band, DateTimeOffset.UnixEpoch).Status.ShouldBe(expected);

    [Fact]
    public void Toledo_continuo_con_velocidad_o_paridad_equivocada_dice_que_respondio_en_otro_formato()
    {
        // Con la velocidad equivocada llegan bytes sin STX (y a veces sin fin de línea): antes decía "no respondió".
        using var connection = new NoiseConnection([0x6B, 0x33, 0x0D, 0x7F, 0x66, 0x0A, 0x31]);

        var error = Should.Throw<TimeoutException>(() => ScaleProtocolReader.Read(
            connection, Toledo, Target(ScaleDrivers.Toledo8217Code, ScaleReadMode.Continuous, timeoutMs: 200)));

        error.Message.ShouldContain("respondió, pero no en el formato");
        error.Message.ShouldContain("velocidad");
    }

    [Fact]
    public void Basura_sin_ningun_fin_de_linea_tambien_cuenta_como_respuesta()
    {
        using var connection = new NoiseConnection([0x00, 0x7F, 0x55, 0x55]);

        Should.Throw<TimeoutException>(() => ScaleProtocolReader.Read(connection, Cas, Target(ScaleDrivers.CasCode, timeoutMs: 200)))
            .Message.ShouldContain("respondió, pero no en el formato");
    }

    /// <summary>Puerto que solo entrega ruido (velocidad o paridad equivocadas).</summary>
    private sealed class NoiseConnection(byte[] noise) : IScaleConnection
    {
        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            Thread.Sleep(20);
            noise.CopyTo(buffer, 0);
            return noise.Length;
        }

        public void DiscardInput()
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Si_la_bascula_no_responde_avisa_al_cumplir_el_tiempo()
    {
        var device = new SimulatedScale();
        device.Set(1m, silent: true);
        using var connection = new SimulatedScaleConnection(device, SimulatedProtocol.Toledo8217, continuous: false);

        var error = Should.Throw<TimeoutException>(() => ScaleProtocolReader.Read(connection, Toledo, Target(ScaleDrivers.Toledo8217Code, timeoutMs: 200)));

        error.Message.ShouldContain("no respondió en 200 ms");
    }

    [Fact]
    public void Si_solo_llega_basura_dice_que_revise_el_protocolo()
    {
        var device = new SimulatedScale();
        device.Set(1m);
        using var connection = new SimulatedScaleConnection(device, SimulatedProtocol.Cas, continuous: false);

        // Una báscula CAS leída con el driver Toledo: responde, pero en otro formato.
        var error = Should.Throw<TimeoutException>(() => ScaleProtocolReader.Read(
            connection, Toledo, Target(ScaleDrivers.Toledo8217Code, timeoutMs: 200)));

        error.Message.ShouldContain("no en el formato");
    }

    // ------------------------------------------------------------------------------------------ Configuración

    [Fact]
    public void Valores_de_fabrica_por_driver()
    {
        var toledo = Target(ScaleDrivers.Toledo8217Code);
        (toledo.BaudRate, toledo.DataBits, toledo.Parity, toledo.StopBits, toledo.Mode).ShouldBe((9600, 7, ScaleParity.Even, 1, ScaleReadMode.Request));
        toledo.RequestBytes.ShouldBe("W"u8.ToArray());
        toledo.Description.ShouldBe("Toledo 8217 (Mettler Toledo y compatibles) · COM3 9600 7E1");

        var cas = Target("cas");
        (cas.BaudRate, cas.DataBits, cas.Parity, cas.StopBits, cas.Mode).ShouldBe((9600, 8, ScaleParity.None, 1, ScaleReadMode.Continuous));
    }

    [Theory]
    [InlineData("MARCA", "COM3", "Marca o protocolo")]
    [InlineData("CAS", "", "Indique el puerto")]
    [InlineData("CAS", "LPT1", "Indique el puerto")]
    [InlineData("SIMULATED", null, "solo está disponible")]
    public void Rechaza_configuraciones_invalidas(string driver, string? port, string message)
    {
        ScaleSettingsResolver.TryResolve(new ScaleSettings { Driver = driver, Port = port }, simulatorAllowed: false, out var error).ShouldBeNull();
        error.ShouldContain(message);
    }

    [Fact]
    public void Peso_minimo_por_defecto_20_g_y_configurable()
    {
        Target(ScaleDrivers.CasCode).ZeroBandKg.ShouldBe(0.020m);
        ScaleSettingsResolver.TryResolve(new ScaleSettings { Driver = "CAS", Port = "COM1", MinWeightGrams = 0 }, false, out _)!.ZeroBandKg.ShouldBe(0m);
        ScaleSettingsResolver.TryResolve(new ScaleSettings { Driver = "CAS", Port = "COM1", MinWeightGrams = -1 }, false, out var error).ShouldBeNull();
        error.ShouldContain("peso mínimo");
    }

    [Fact]
    public void Rechaza_puerto_serie_y_comando_invalidos()
    {
        ScaleSettingsResolver.TryResolve(new ScaleSettings { Driver = "CAS", Port = "COM1", BaudRate = 1234 }, false, out var e1).ShouldBeNull();
        e1.ShouldContain("Puerto serie inválido");
        ScaleSettingsResolver.TryResolve(new ScaleSettings { Driver = "CAS", Port = "COM1", ReadMode = "REQUEST", RequestCommand = "" }, false, out var e2).ShouldBeNull();
        e2.ShouldContain("Comando de lectura");
        ScaleSettingsResolver.TryResolve(new ScaleSettings { Driver = "CAS", Port = "COM1", TimeoutMs = 50 }, false, out var e3).ShouldBeNull();
        e3.ShouldContain("tiempo de espera");
    }

    [Fact]
    public void Comandos_con_escapes()
    {
        ScaleSettingsResolver.Unescape(@"\x05").ShouldBe(new byte[] { 0x05 });
        ScaleSettingsResolver.Unescape(@"W\r\n").ShouldBe("W\r\n"u8.ToArray());
        ScaleSettingsResolver.Unescape(@"\q").ShouldBeNull();
        ScaleSettingsResolver.Escape([0x05, (byte)'W', 0x0D]).ShouldBe(@"\x05W\r");
    }
}
