using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace Pos.Terminal.Agent.Scales;

/// <summary>Camino de los bytes hasta la báscula (puerto serie real o simulador). Se usa desde un solo hilo a la vez.</summary>
public interface IScaleConnection : IDisposable
{
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Lee lo que haya llegado; espera hasta <paramref name="timeoutMs"/>. Devuelve 0 si no llegó nada a tiempo.</summary>
    int Read(byte[] buffer, int timeoutMs);

    /// <summary>Descarta lo que esté en el búfer de entrada (tramas viejas del modo continuo).</summary>
    void DiscardInput();
}

public interface IScaleConnectionFactory
{
    IScaleConnection Open(ScaleTarget target);
}

/// <summary>Abre el puerto serie de la báscula, o el simulador si la configuración es SIMULATED.</summary>
public sealed class ScaleConnectionFactory(SimulatedScale simulator) : IScaleConnectionFactory
{
    public IScaleConnection Open(ScaleTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Simulated
            ? new SimulatedScaleConnection(simulator, SimulatedProtocol.Toledo8217, continuous: false)
            : new SerialScaleConnection(target);
    }
}

/// <summary>Puerto serie o USB-serie (<c>COMx</c>) con la velocidad, bits, paridad y parada de la báscula; sin control de flujo.</summary>
public sealed class SerialScaleConnection : IScaleConnection
{
    private readonly SerialPort _port;

    public SerialScaleConnection(ScaleTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _port = new SerialPort(target.Port, target.BaudRate, ToParity(target.Parity), target.DataBits, target.StopBits == 2 ? StopBits.Two : StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = target.TimeoutMs,
            WriteTimeout = target.TimeoutMs,
            Encoding = Encoding.Latin1,

            // Muchas básculas y cables USB-serie necesitan DTR/RTS activos para transmitir.
            DtrEnable = true,
            RtsEnable = true,
        };
        _port.Open();
    }

    public void Write(ReadOnlySpan<byte> data) => _port.Write(data.ToArray(), 0, data.Length);

    public int Read(byte[] buffer, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _port.ReadTimeout = Math.Max(1, timeoutMs);
        try
        {
            return _port.Read(buffer, 0, buffer.Length);
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    public void DiscardInput() => _port.DiscardInBuffer();

    public void Dispose() => _port.Dispose();

    private static Parity ToParity(ScaleParity parity) => parity switch
    {
        ScaleParity.Even => Parity.Even,
        ScaleParity.Odd => Parity.Odd,
        _ => Parity.None,
    };
}

/// <summary>Separa las tramas que llegan por el puerto: terminan en CR o LF. Una trama sin fin demasiado larga es basura y se descarta.</summary>
public sealed class ScaleFrameBuffer
{
    public const int MaxFrameLength = 128;

    private readonly StringBuilder _pending = new();

    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _pending.Append((char)b);
        }

        if (_pending.Length > MaxFrameLength * 4)
        {
            // Ruido sin fin de línea (velocidad o paridad equivocadas): se conserva solo la cola.
            _pending.Remove(0, _pending.Length - MaxFrameLength);
        }
    }

    /// <summary>Saca la siguiente trama completa (sin CR/LF y no vacía).</summary>
    public bool TryTakeFrame(out string frame)
    {
        while (true)
        {
            var text = _pending.ToString();
            var end = text.IndexOfAny(['\r', '\n']);
            if (end < 0)
            {
                frame = string.Empty;
                return false;
            }

            _pending.Remove(0, end + 1);
            var candidate = text[..end];
            if (candidate.Length is > 0 and <= MaxFrameLength && candidate.Any(c => c > ' '))
            {
                frame = candidate;
                return true;
            }
        }
    }
}

/// <summary>Lee UNA trama válida de la báscula: pide el peso (modo REQUEST) o espera la siguiente trama (modo CONTINUOUS).</summary>
public static class ScaleProtocolReader
{
    /// <exception cref="TimeoutException">La báscula no envió una trama válida a tiempo.</exception>
    public static ScaleReading Read(IScaleConnection connection, IScaleDriver driver, ScaleTarget target)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(target);

        connection.DiscardInput();
        var skipFirst = false;
        char? sync = null;
        if (target.Mode == ScaleReadMode.Request && target.RequestBytes.Length > 0)
        {
            connection.Write(target.RequestBytes);
        }
        else if (driver.FrameStart is { } start)
        {
            // Continuo con inicio de trama conocido (Toledo: STX): se sincroniza con él y se descartan las tramas cortadas.
            sync = start;
        }
        else
        {
            skipFirst = driver.SkipFirstContinuousFrame;
        }

        var frames = new ScaleFrameBuffer();
        var chunk = new byte[256];
        var clock = Stopwatch.StartNew();
        var garbage = 0;
        var received = 0;
        while (true)
        {
            var remaining = target.TimeoutMs - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0)
            {
                break;
            }

            var read = connection.Read(chunk, remaining);
            if (read <= 0)
            {
                continue;
            }

            received += read;
            frames.Append(chunk.AsSpan(0, read));
            while (frames.TryTakeFrame(out var frame))
            {
                if (skipFirst)
                {
                    skipFirst = false;
                    continue;
                }

                var candidate = frame;
                if (sync is { } start)
                {
                    var at = FrameStartIndex(frame, start);
                    if (at < 0)
                    {
                        // Trama cortada (llegó sin su inicio): se espera la siguiente completa. Si nunca llega una con STX (velocidad o
                        // paridad equivocadas), al vencer el tiempo cuenta como "respondió, pero no en el formato".
                        garbage++;
                        continue;
                    }

                    candidate = frame[at..];
                }

                if (driver.Parse(candidate, target.ImpliedDecimals) is { } reading)
                {
                    return reading;
                }

                garbage++;
            }
        }

        // Llegaron bytes (aunque sin ninguna trama completa, como pasa con la velocidad o la paridad equivocadas): la báscula responde.
        throw new TimeoutException(garbage > 0 || received > 0
            ? $"La báscula respondió, pero no en el formato {driver.Name}: revise el protocolo, la velocidad y la paridad."
            : $"La báscula no respondió en {target.TimeoutMs} ms: revise el cable, el puerto ({target.Port}) y que esté encendida.");
    }

    /// <summary>Última posición del inicio de trama (sin el bit de paridad: con 7E1 leído como 8 bits STX puede llegar como 0x82).</summary>
    private static int FrameStartIndex(string frame, char start)
    {
        for (var i = frame.Length - 1; i >= 0; i--)
        {
            if ((frame[i] & 0x7F) == start)
            {
                return i;
            }
        }

        return -1;
    }
}
