using System.Globalization;

namespace Pos.SharedKernel.Measurement;

/// <summary>
/// Lectura de la báscula FIRMADA por el agente de la caja (Fase 7-C, decisión D3 de la revisión): el agente la arma con lo que leyó del
/// puerto serie, la firma con la clave de la caja (ECDSA P-256 + SHA-256; la clave privada nunca sale del agente) y la interfaz la
/// reenvía al servidor sin poder cambiarla. El texto firmado es canónico (campos separados por <c>|</c>, cultura invariante):
/// <c>BP-SCALE-3|caja|lectura|hora-ms|arranque|número-de-arranque|secuencia|bruto|tara|neto|estable|neto-de-báscula|estado|secuencia-vacía|vacía-desde-la-aceptada</c>.
/// <para>El orden entre lecturas NO usa la hora (el reloj del equipo puede saltar): usa <see cref="BootId"/> (cambia cada vez que arranca
/// el agente), <see cref="BootCounter"/> (número de arranque que el agente guarda y solo sube: el servidor rechaza un arranque menor al
/// último aceptado, BQ3) y <see cref="Seq"/> (contador que solo crece mientras el agente está encendido). La hora solo sirve para descartar
/// lecturas demasiado viejas o del futuro frente al reloj del servidor.</para>
/// </summary>
/// <param name="TerminalId">Caja con la que se vinculó el agente.</param>
/// <param name="ReadingId">Identificador único de la lectura (nonce): el servidor no acepta dos veces la misma.</param>
/// <param name="At">Hora de la lectura en el reloj del agente.</param>
/// <param name="BootId">Arranque del agente.</param>
/// <param name="BootCounter">Número de arranque del agente (guardado en su equipo; cada arranque es mayor que el anterior).</param>
/// <param name="Seq">Número de la lectura dentro del arranque (creciente).</param>
/// <param name="GrossKg">Lo que marcó la báscula.</param>
/// <param name="TareKg">Tara manual del agente (tecla Ins).</param>
/// <param name="NetKg">Bruto menos la tara manual.</param>
/// <param name="Stable">El peso estaba quieto.</param>
/// <param name="ScaleNet">La báscula ya descontó una tara (trama neta): el "bruto" de la trama no es el bruto real.</param>
/// <param name="Status">OK, UNSTABLE, ZERO, NEGATIVE, OVERLOAD o ERROR.</param>
/// <param name="LastEmptySeq">Secuencia (en este arranque) de la última lectura con la báscula vacía: |bruto| ≤ peso mínimo.</param>
/// <param name="EmptiedSinceLastAccepted">El agente vio la báscula vacía después de la última pesada que la caja agregó.</param>
public sealed record ScaleReadingPayload(
    Guid TerminalId,
    Guid ReadingId,
    DateTimeOffset At,
    Guid BootId,
    long BootCounter,
    long Seq,
    decimal GrossKg,
    decimal TareKg,
    decimal NetKg,
    bool Stable,
    bool ScaleNet,
    string Status,
    long? LastEmptySeq,
    bool EmptiedSinceLastAccepted)
{
    public const string Version = "BP-SCALE-3";

    /// <summary>Algoritmo de la firma (texto informativo para la configuración).</summary>
    public const string Algorithm = "ECDSA_P256_SHA256";

    public string Format() => string.Join(
        '|',
        Version,
        TerminalId.ToString("D", CultureInfo.InvariantCulture),
        ReadingId.ToString("D", CultureInfo.InvariantCulture),
        At.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        BootId.ToString("D", CultureInfo.InvariantCulture),
        BootCounter.ToString(CultureInfo.InvariantCulture),
        Seq.ToString(CultureInfo.InvariantCulture),
        Kg(GrossKg),
        Kg(TareKg),
        Kg(NetKg),
        Stable ? "1" : "0",
        ScaleNet ? "1" : "0",
        Status,
        LastEmptySeq is { } empty ? empty.ToString(CultureInfo.InvariantCulture) : "-",
        EmptiedSinceLastAccepted ? "1" : "0");

    public static ScaleReadingPayload? TryParse(string? text)
    {
        var parts = text?.Split('|');
        if (parts is not { Length: 15 } || parts[0] != Version
            || !Guid.TryParseExact(parts[1], "D", out var terminal) || !Guid.TryParseExact(parts[2], "D", out var reading)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var at)
            || !Guid.TryParseExact(parts[4], "D", out var boot)
            || !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var bootCounter)
            || !long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            || ParseKg(parts[7]) is not { } gross || ParseKg(parts[8]) is not { } tare || ParseKg(parts[9]) is not { } net
            || parts[10] is not ("0" or "1") || parts[11] is not ("0" or "1") || parts[14] is not ("0" or "1")
            || parts[12] is not ("OK" or "UNSTABLE" or "ZERO" or "NEGATIVE" or "OVERLOAD" or "ERROR"))
        {
            return null;
        }

        long? lastEmpty = null;
        if (parts[13] != "-")
        {
            if (!long.TryParse(parts[13], NumberStyles.None, CultureInfo.InvariantCulture, out var empty))
            {
                return null;
            }

            lastEmpty = empty;
        }

        try
        {
            return new ScaleReadingPayload(
                terminal, reading, DateTimeOffset.FromUnixTimeMilliseconds(at), boot, bootCounter, seq, gross, tare, net, parts[10] == "1", parts[11] == "1",
                parts[12], lastEmpty, parts[14] == "1");
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string Kg(decimal value) => decimal.Round(value, 3).ToString("0.000", CultureInfo.InvariantCulture);

    private static decimal? ParseKg(string text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
}
