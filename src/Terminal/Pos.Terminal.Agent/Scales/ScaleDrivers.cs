using System.Globalization;
using System.Text.RegularExpressions;

namespace Pos.Terminal.Agent.Scales;

/// <summary>Cómo se pide el peso: la caja lo pide (REQUEST, p. ej. "W") o la báscula lo envía sola todo el tiempo (CONTINUOUS).</summary>
public enum ScaleReadMode
{
    Request,
    Continuous,
}

/// <summary>Paridad del puerto serie (texto de la configuración: NONE, EVEN u ODD).</summary>
public enum ScaleParity
{
    None,
    Even,
    Odd,
}

/// <summary>
/// Lo que entendió el driver de UNA trama de la báscula. <see cref="WeightKg"/> es nulo cuando la báscula solo informa su estado
/// (p. ej. Toledo 8217 en movimiento o con sobrepeso responde con un byte de estado y sin peso).
/// </summary>
public sealed record ScaleReading(
    decimal? WeightKg,
    bool Stable,
    bool Overload = false,
    bool UnderZero = false,
    bool Net = false,
    bool CenterOfZero = false,
    string? Problem = null);

/// <summary>Valores de fábrica del puerto serie para una marca o protocolo (el técnico puede cambiarlos).</summary>
public sealed record ScaleSerialDefaults(int BaudRate, int DataBits, ScaleParity Parity, int StopBits, ScaleReadMode Mode, string? RequestCommand);

/// <summary>
/// Driver de báscula (doc 03: periféricos como interfaz + driver por marca). Un driver solo sabe leer las tramas de su protocolo; el
/// puerto, los tiempos y la tara los maneja <see cref="ScaleService"/>. Un modelo nuevo que hable un protocolo conocido no necesita
/// código: basta su configuración (catálogo de la Fase 7-C parte D).
/// </summary>
public interface IScaleDriver
{
    /// <summary>Código del driver en la configuración (TOLEDO_8217, CAS, GENERIC).</summary>
    string Code { get; }

    /// <summary>Nombre para las pantallas.</summary>
    string Name { get; }

    ScaleSerialDefaults Defaults { get; }

    /// <summary>
    /// La báscula dice si el peso está estable. Si no (formato genérico), el agente lo decide: el mismo peso durante medio segundo.
    /// </summary>
    bool ReportsStability { get; }

    /// <summary>
    /// En modo continuo la primera trama después de vaciar el búfer puede venir cortada; si el protocolo no tiene un encabezado que lo
    /// delate, se descarta.
    /// </summary>
    bool SkipFirstContinuousFrame { get; }

    /// <summary>
    /// Carácter con el que empieza cada trama (Toledo 8217: STX). En modo continuo el lector se sincroniza con él: una trama sin este
    /// inicio llegó cortada (p. ej. "0.700" de "10.700") y se descarta. Nulo si el protocolo no tiene un inicio fijo.
    /// </summary>
    char? FrameStart { get; }

    /// <summary>
    /// Interpreta una trama ya separada (sin CR/LF). Devuelve <c>null</c> si no es una trama válida de este protocolo (basura,
    /// trama cortada o eco): quien lee sigue esperando la siguiente.
    /// </summary>
    /// <param name="frame">Bytes de la trama como texto Latin-1 (un carácter por byte).</param>
    /// <param name="impliedDecimals">Decimales implícitos cuando el peso llega sin punto (p. ej. "00700" con 3 → 0,700 kg).</param>
    ScaleReading? Parse(string frame, int impliedDecimals);
}

/// <summary>Drivers disponibles en el agente.</summary>
public static class ScaleDrivers
{
    public const string Toledo8217Code = "TOLEDO_8217";
    public const string CasCode = "CAS";
    public const string GenericCode = "GENERIC";

    /// <summary>Báscula simulada (solo en desarrollo o con <c>Agent:EnableScaleSimulator</c>): habla Toledo 8217 dentro del agente.</summary>
    public const string SimulatedCode = "SIMULATED";

    public static IReadOnlyList<IScaleDriver> All { get; } = [new Toledo8217Driver(), new CasDriver(), new GenericAsciiDriver()];

    /// <summary>Driver por código (acepta "Toledo8217", "toledo-8217", "SIMULADO"…). El simulado usa el driver Toledo 8217.</summary>
    public static IScaleDriver? Find(string? code) => Normalize(code) switch
    {
        Toledo8217Code or SimulatedCode => All[0],
        CasCode => All[1],
        GenericCode => All[2],
        _ => null,
    };

    public static string Normalize(string? code) => (code ?? string.Empty).Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant() switch
    {
        "TOLEDO8217" or "TOLEDO" or "8217" => Toledo8217Code,
        "SIMULADO" or "SIMULADA" => SimulatedCode,
        "GENERICO" or "GENERICA" or "OTRA" => GenericCode,
        var v => v,
    };

    internal static decimal? ParseNumber(string text, int impliedDecimals)
    {
        var clean = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        if (!decimal.TryParse(clean, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        if (!clean.Contains('.', StringComparison.Ordinal) && impliedDecimals > 0)
        {
            value /= (decimal)Math.Pow(10, impliedDecimals);
        }

        return value;
    }

    /// <summary>Convierte a kilogramos según la unidad de la trama (kg por defecto).</summary>
    internal static decimal ToKg(decimal value, string? unit) => (unit ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "G" => value / 1000m,
        "LB" => value * 0.45359237m,
        "OZ" => value * 0.028349523125m,
        "T" => value * 1000m,
        _ => value,
    };
}

/// <summary>
/// Protocolo <b>Mettler Toledo 8217</b> (lo emulan muchas básculas de mostrador: CAS, Torrey, Moretti, Dibal…).
/// <para>Fuente: manual "8217 Host Communications" de Mettler Toledo, reproducido en
/// https://zipstore.ru/auxpage_data_protocol_8217_mettler_toledo_1c/ (consultado 2026-10-17), y el capítulo de comunicación serial del
/// manual de servicio de Mettler Toledo BC (https://www.mt.com/dam/ind/IND_PDF/30126862_R02_BC_Scale_SM_EN.pdf).</para>
/// <list type="bullet">
/// <item>Puerto: RS-232, <b>7 bits, paridad par, 1 bit de parada</b>; 1200, 2400, <b>9600</b> o 19200 baudios. Comandos en MAYÚSCULA y
/// al menos 200 ms entre comandos.</item>
/// <item>La caja envía <c>W</c> (0x57). Respuesta con peso: <c>STX WW.WWW CR</c> (kg bruto), <c>STX WW.WWWN CR</c> (kg neto),
/// <c>STX WW.WW CR</c> / <c>STX WW.WWN CR</c> (libras). Algunas emulaciones envían los dígitos sin punto (<c>STX 00700 CR</c>): se usan los
/// decimales implícitos configurados.</item>
/// <item>Si el peso está en movimiento, es negativo o supera la capacidad, responde <c>STX ? byte-de-estado CR</c>. Bits del estado:
/// 0 = en movimiento, 1 = sobre la capacidad, 2 = bajo cero, 3 = fuera del rango de cero, 4 = centro de cero, 5 = neto,
/// 6 = 1 normal / 0 comando inválido, 7 = paridad.</item>
/// <item>Otros comandos (no usados aquí): <c>Z</c> cero, <c>T</c> tara, <c>C</c> borrar tara. El agente hace la tara por software.</item>
/// </list>
/// El comando <c>S</c> de algunas emulaciones NCR/SASI (escáner-báscula) no es parte del 8217: si hace falta será otro driver.
/// </summary>
public sealed partial class Toledo8217Driver : IScaleDriver
{
    public string Code => ScaleDrivers.Toledo8217Code;

    public string Name => "Toledo 8217 (Mettler Toledo y compatibles)";

    public ScaleSerialDefaults Defaults { get; } = new(9600, 7, ScaleParity.Even, 1, ScaleReadMode.Request, "W");

    public bool ReportsStability => true;

    public bool SkipFirstContinuousFrame => false;

    /// <summary>STX: en modo continuo solo valen las tramas que empiezan con él (una trama cortada no lo trae).</summary>
    public char? FrameStart => '\x02';

    public ScaleReading? Parse(string frame, int impliedDecimals)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // Con 7 bits el octavo es la paridad: se descarta. Se quitan STX y cualquier carácter de control al inicio.
        var text = new string([.. frame.Select(c => (char)(c & 0x7F))]).TrimStart('\x02', '\x00', ' ').TrimEnd(' ', '\x03');
        if (text.Length >= 2 && text[0] == '?')
        {
            return FromStatus(text[1]);
        }

        var match = WeightPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var digits = match.Groups["w"].Value;
        var net = match.Groups["n"].Success;
        var point = digits.IndexOf('.', StringComparison.Ordinal);
        decimal? weight;
        if (point >= 0 && digits.Length - point - 1 == 2)
        {
            // WW.WW: libras (según el manual, 2 decimales = lb; 3 decimales = kg).
            weight = ScaleDrivers.ParseNumber(digits, 0) is { } lb ? decimal.Round(ScaleDrivers.ToKg(lb, "LB"), 3) : null;
        }
        else
        {
            weight = ScaleDrivers.ParseNumber(digits, impliedDecimals);
        }

        return weight is null ? null : new ScaleReading(weight, Stable: true, Net: net, CenterOfZero: weight == 0m);
    }

    /// <summary>Byte de estado del 8217 (después de "?").</summary>
    public static ScaleReading FromStatus(char status)
    {
        var bits = status & 0x7F;
        if ((bits & 0x40) == 0)
        {
            return new ScaleReading(null, Stable: false, Problem: "La báscula no entendió la petición: revise el protocolo y el comando configurados.");
        }

        return new ScaleReading(
            null,
            Stable: (bits & 0x01) == 0,
            Overload: (bits & 0x02) != 0,
            UnderZero: (bits & 0x04) != 0,
            Net: (bits & 0x20) != 0,
            CenterOfZero: (bits & 0x10) != 0);
    }

    [GeneratedRegex(@"^(?<w>\d{1,4}\.\d{2,3}|\d{4,6})(?<n>N)?$", RegexOptions.CultureInvariant)]
    private static partial Regex WeightPattern();
}

/// <summary>
/// Protocolo <b>CAS "22 bytes"</b> (indicadores y básculas CAS: CI-200A, CI-2001, PDI, NT-580A y las de mostrador que lo ofrecen como
/// formato de salida).
/// <para>Fuentes: manual CAS CI-200A, §11-3-1 "22 Bytes for CAS" (https://totalcomp.com/image/product_manual_pdf/CI-200-Series-Owners-Manual.pdf),
/// manual CAS PDI (https://scales.co.ke/download/195/cas-pdi/1971/cas-pdi-user-manual.pdf) y CAS CI-2001
/// (http://nicolscales.com/wp-content/uploads/2023/07/CAS-CI-2001-SERIES-Manual.pdf), consultados 2026-10-17.</para>
/// <list type="bullet">
/// <item>Puerto: <b>8 bits, sin paridad, 1 bit de parada</b>, ASCII; normalmente 9600 baudios.</item>
/// <item>Trama: <c>EE,PP,</c> + identificador del equipo (1 byte) + byte de lámparas (1 byte) + peso (8 bytes, con signo y punto, p. ej.
/// <c>"  +0.700"</c> o <c>"0000.700"</c>) + unidad (<c>kg</c>, <c>g</c>, <c>lb</c>, <c>t</c>) + CR LF.
/// EE: <c>ST</c> estable, <c>US</c> inestable, <c>OL</c> sobrepeso. PP: <c>GS</c> bruto, <c>NT</c> neto.</item>
/// <item>Envío: continuo, solo al estabilizarse o a pedido (la caja envía el identificador del equipo). Por defecto: continuo.</item>
/// </list>
/// Se acepta también la variante sin identificador ni lámparas (<c>"ST,GS,+0000.700kg"</c>) que usan varias básculas de mostrador.
/// El protocolo "ECR" (ENQ/ACK/DC1 con SOH…BCC…EOT) de algunas PD-II es distinto y queda para otro driver.
/// </summary>
public sealed partial class CasDriver : IScaleDriver
{
    public string Code => ScaleDrivers.CasCode;

    public string Name => "CAS (formato de 22 bytes: ST/US/OL)";

    public ScaleSerialDefaults Defaults { get; } = new(9600, 8, ScaleParity.None, 1, ScaleReadMode.Continuous, null);

    public bool ReportsStability => true;

    public bool SkipFirstContinuousFrame => false;

    /// <summary>La trama CAS empieza con "ST/US/OL," (encabezado anclado): una trama cortada no coincide y se descarta.</summary>
    public char? FrameStart => null;

    /// <summary>
    /// Campo de peso ya separado: signo opcional, espacios de relleno, dígitos y punto opcional (p. ej. "  +0.700", "0000.700", "-  0.120").
    /// Nunca se busca "el último número" de la trama: así el identificador o el byte de lámparas no se pegan al peso.
    /// </summary>
    internal static decimal? ParseWeightField(string field, int impliedDecimals)
    {
        var text = field.Trim(' ', '\0');
        return WeightFieldPattern().IsMatch(text) ? ScaleDrivers.ParseNumber(text, impliedDecimals) : null;
    }

    public ScaleReading? Parse(string frame, int impliedDecimals)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var match = FramePattern().Match(frame.Trim('\x02', '\x03', ' ', '\0'));
        if (!match.Success)
        {
            return null;
        }

        var header = match.Groups["h"].Value.ToUpperInvariant();
        var rest = match.Groups["rest"].Value;
        var unitMatch = UnitPattern().Match(rest);
        var unit = unitMatch.Success ? unitMatch.Groups["u"].Value : null;
        var body = (unitMatch.Success ? rest[..unitMatch.Index] : rest).TrimEnd(' ', '\0');
        var decimals = unit is null ? impliedDecimals : 0;

        // Trama completa de 22 bytes (manual CI-200A §11-3-1): después de "EE,PP," vienen el identificador del equipo (1 byte), el byte
        // de lámparas (1 byte), una coma opcional según el modelo y el peso en 8 bytes. Se reconoce porque el cuerpo no es un peso por sí
        // solo (bytes de control o de lámparas) o porque es más largo que el campo de peso y no empieza por signo o espacio.
        // Variante corta sin identificador ("ST,GS,+0000.700kg"): el cuerpo entero es el peso.
        var shortWeight = ParseWeightField(body, decimals);
        var full = body.Length >= 10 && (shortWeight is null || body[0] is not (' ' or '+' or '-'));
        decimal? value = full ? ParseWeightField(body[2..].TrimStart(','), decimals) : shortWeight;
        decimal? weight = value is { } v ? decimal.Round(ScaleDrivers.ToKg(v, unit), 3) : null;

        if (header == "OL")
        {
            return new ScaleReading(weight, Stable: false, Overload: true);
        }

        if (weight is null)
        {
            return null;
        }

        var net = string.Equals(match.Groups["p"].Value, "NT", StringComparison.OrdinalIgnoreCase);
        return new ScaleReading(weight, Stable: header == "ST", UnderZero: weight < 0m, Net: net, CenterOfZero: weight == 0m);
    }

    [GeneratedRegex(@"^(?<h>ST|US|OL)\s*,\s*(?<p>GS|NT|TR)?,?(?<rest>.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FramePattern();

    [GeneratedRegex(@"(?<u>kg|lb|oz|g|t)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnitPattern();

    [GeneratedRegex(@"^[-+]?\s*\d+(?:[.,]\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex WeightFieldPattern();
}

/// <summary>
/// Formato <b>genérico</b> para configuración manual: toma el último número de la trama y su unidad (kg por defecto). Sirve para
/// básculas que envían solo el peso (p. ej. CAS "10 bytes": 8 bytes de peso + CR LF). Si la trama trae "ST"/"US"/"OL" se respetan;
/// si no, el agente decide la estabilidad (mismo peso durante medio segundo).
/// </summary>
public sealed partial class GenericAsciiDriver : IScaleDriver
{
    public string Code => ScaleDrivers.GenericCode;

    public string Name => "Otra báscula (formato genérico: número y unidad)";

    public ScaleSerialDefaults Defaults { get; } = new(9600, 8, ScaleParity.None, 1, ScaleReadMode.Continuous, null);

    public bool ReportsStability => false;

    public bool SkipFirstContinuousFrame => true;

    public char? FrameStart => null;

    public ScaleReading? Parse(string frame, int impliedDecimals)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var text = new string([.. frame.Where(c => c >= ' ' && c < '\x7F')]).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var overload = OverloadPattern().IsMatch(text);
        var match = WeightPattern().Match(text);
        if (!match.Success)
        {
            return overload ? new ScaleReading(null, Stable: false, Overload: true) : null;
        }

        // Los decimales implícitos solo aplican si la trama no trae unidad ("00000700" → 0,700 kg; "700 g" son 700 gramos).
        var unit = match.Groups["u"].Success ? match.Groups["u"].Value : null;
        if (ScaleDrivers.ParseNumber(match.Groups["w"].Value, unit is null ? impliedDecimals : 0) is not { } value)
        {
            return null;
        }

        var weight = decimal.Round(ScaleDrivers.ToKg(value, unit), 3);
        var unstable = UnstablePattern().IsMatch(text);
        return new ScaleReading(weight, Stable: !unstable, Overload: overload, UnderZero: weight < 0m, CenterOfZero: weight == 0m);
    }

    /// <summary>Último número (con signo opcional) y su unidad al final de la trama.</summary>
    [GeneratedRegex(@"(?<w>[-+]?\s*\d+(?:[.,]\d+)?)\s*(?<u>kg|lb|oz|g)?\s*[^0-9]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WeightPattern();

    [GeneratedRegex(@"^\s*US\b|\bMOTION\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnstablePattern();

    [GeneratedRegex(@"^\s*OL\b|\bOVER", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OverloadPattern();
}
