using System.Globalization;

namespace Pos.SharedKernel.Measurement;

/// <summary>
/// Lectura de la báscula FIRMADA por el agente de la caja (Fase 7-C, decisión D3 de la revisión): el agente la arma con lo que leyó del
/// puerto serie, la firma con la clave de la caja (ECDSA P-256 + SHA-256; la clave privada nunca sale del agente) y la interfaz la
/// reenvía al servidor sin poder cambiarla. El texto firmado es canónico (campos separados por <c>|</c>, cultura invariante):
/// <c>BP-SCALE-1|caja|lectura|hora-ms|bruto|tara|neto|estable|neto-de-báscula|estado|vacía-ms|vacía-desde-la-aceptada</c>.
/// </summary>
/// <param name="TerminalId">Caja con la que se vinculó el agente.</param>
/// <param name="ReadingId">Identificador único de la lectura (nonce): el servidor no acepta dos veces la misma.</param>
/// <param name="At">Hora de la lectura en el reloj del agente (creciente: nunca retrocede dentro del agente).</param>
/// <param name="GrossKg">Lo que marcó la báscula.</param>
/// <param name="TareKg">Tara manual del agente (tecla Ins).</param>
/// <param name="NetKg">Bruto menos la tara manual.</param>
/// <param name="Stable">El peso estaba quieto.</param>
/// <param name="ScaleNet">La báscula ya descontó una tara (trama neta).</param>
/// <param name="Status">OK, UNSTABLE, ZERO, NEGATIVE, OVERLOAD o ERROR.</param>
/// <param name="LastEmptyAt">Última vez (reloj del agente) que el agente vio la báscula vacía (peso dentro de la banda de cero).</param>
/// <param name="EmptiedSinceLastAccepted">El agente vio la báscula vacía después de la última pesada que la caja agregó.</param>
public sealed record ScaleReadingPayload(
    Guid TerminalId,
    Guid ReadingId,
    DateTimeOffset At,
    decimal GrossKg,
    decimal TareKg,
    decimal NetKg,
    bool Stable,
    bool ScaleNet,
    string Status,
    DateTimeOffset? LastEmptyAt,
    bool EmptiedSinceLastAccepted)
{
    public const string Version = "BP-SCALE-1";

    /// <summary>Algoritmo de la firma (texto informativo para la configuración).</summary>
    public const string Algorithm = "ECDSA_P256_SHA256";

    public string Format() => string.Join(
        '|',
        Version,
        TerminalId.ToString("D", CultureInfo.InvariantCulture),
        ReadingId.ToString("D", CultureInfo.InvariantCulture),
        At.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        Kg(GrossKg),
        Kg(TareKg),
        Kg(NetKg),
        Stable ? "1" : "0",
        ScaleNet ? "1" : "0",
        Status,
        LastEmptyAt is { } empty ? empty.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : "-",
        EmptiedSinceLastAccepted ? "1" : "0");

    public static ScaleReadingPayload? TryParse(string? text)
    {
        var parts = text?.Split('|');
        if (parts is not { Length: 12 } || parts[0] != Version
            || !Guid.TryParseExact(parts[1], "D", out var terminal) || !Guid.TryParseExact(parts[2], "D", out var reading)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var at)
            || ParseKg(parts[4]) is not { } gross || ParseKg(parts[5]) is not { } tare || ParseKg(parts[6]) is not { } net
            || parts[7] is not ("0" or "1") || parts[8] is not ("0" or "1") || parts[11] is not ("0" or "1")
            || parts[9] is not ("OK" or "UNSTABLE" or "ZERO" or "NEGATIVE" or "OVERLOAD" or "ERROR"))
        {
            return null;
        }

        DateTimeOffset? lastEmpty = null;
        if (parts[10] != "-")
        {
            if (!long.TryParse(parts[10], NumberStyles.None, CultureInfo.InvariantCulture, out var empty))
            {
                return null;
            }

            lastEmpty = DateTimeOffset.FromUnixTimeMilliseconds(empty);
        }

        try
        {
            return new ScaleReadingPayload(
                terminal, reading, DateTimeOffset.FromUnixTimeMilliseconds(at), gross, tare, net, parts[7] == "1", parts[8] == "1", parts[9], lastEmpty,
                parts[11] == "1");
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
