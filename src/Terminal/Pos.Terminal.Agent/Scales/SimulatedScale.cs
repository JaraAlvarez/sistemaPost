using System.Globalization;
using System.Text;

namespace Pos.Terminal.Agent.Scales;

/// <summary>Protocolo que habla el simulador.</summary>
public enum SimulatedProtocol
{
    Toledo8217,
    Cas,
}

/// <summary>
/// Báscula simulada (para probar sin equipo): el peso que "tiene encima", si se está moviendo y si hay sobrepeso. En el agente se
/// cambia con <c>POST /scale/simulator</c> (solo en desarrollo); en las pruebas, directamente. No instala puertos virtuales ni toca
/// la configuración del equipo: todo pasa dentro del proceso.
/// </summary>
public sealed class SimulatedScale
{
    private readonly Lock _gate = new();
    private decimal _weightKg;
    private bool _stable = true;
    private bool _overload;
    private bool _silent;

    public decimal WeightKg
    {
        get { lock (_gate) { return _weightKg; } }
    }

    public bool Stable
    {
        get { lock (_gate) { return _stable; } }
    }

    public bool Overload
    {
        get { lock (_gate) { return _overload; } }
    }

    /// <summary>La báscula no responde (cable suelto o apagada): sirve para probar los tiempos de espera.</summary>
    public bool Silent
    {
        get { lock (_gate) { return _silent; } }
    }

    public void Set(decimal weightKg, bool stable = true, bool overload = false, bool silent = false)
    {
        lock (_gate)
        {
            _weightKg = decimal.Round(weightKg, 3);
            _stable = stable;
            _overload = overload;
            _silent = silent;
        }
    }

    /// <summary>Respuesta Toledo 8217 a "W": <c>STX 00.700 CR</c>, o <c>STX ? estado CR</c> si se mueve, es negativo o hay sobrepeso.</summary>
    public byte[] ToledoFrame()
    {
        lock (_gate)
        {
            var status = 0x40;
            if (!_stable)
            {
                status |= 0x01;
            }

            if (_overload)
            {
                status |= 0x02;
            }

            if (_weightKg < 0m)
            {
                status |= 0x04;
            }

            if (status != 0x40)
            {
                return [0x02, (byte)'?', (byte)status, 0x0D];
            }

            if (_weightKg == 0m)
            {
                status |= 0x10;
            }

            return Encoding.ASCII.GetBytes("\x02" + _weightKg.ToString("00.000", CultureInfo.InvariantCulture) + "\r");
        }
    }

    /// <summary>Trama CAS de 22 bytes sin identificador: <c>ST,GS,+0000.700kg CR LF</c> (US si se mueve, OL si hay sobrepeso).</summary>
    public byte[] CasFrame()
    {
        lock (_gate)
        {
            var header = _overload ? "OL" : _stable ? "ST" : "US";
            var sign = _weightKg < 0m ? "-" : "+";
            var data = Math.Abs(_weightKg).ToString("0000.000", CultureInfo.InvariantCulture);
            return Encoding.ASCII.GetBytes($"{header},GS,{sign}{data}kg\r\n");
        }
    }
}

/// <summary>
/// "Puerto" en memoria que se comporta como la báscula: en Toledo 8217 responde a cada "W"; en CAS continuo entrega una trama por lectura.
/// Las pruebas pueden inyectar bytes crudos (basura, tramas cortadas) con <see cref="Inject"/>.
/// </summary>
public sealed class SimulatedScaleConnection(SimulatedScale device, SimulatedProtocol protocol, bool continuous) : IScaleConnection
{
    private readonly Queue<byte> _pending = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly Lock _gate = new();

    /// <summary>Bytes que la báscula recibió (para verificar el comando enviado).</summary>
    public List<byte> Received { get; } = [];

    /// <summary>Ruido que llega antes de cada respuesta (tramas cortadas o basura del cable), para las pruebas.</summary>
    public byte[]? Noise { get; set; }

    public void Inject(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            foreach (var b in data)
            {
                _pending.Enqueue(b);
            }
        }

        _arrived.Release();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        Received.AddRange(data.ToArray());
        if (device.Silent)
        {
            return;
        }

        if (protocol == SimulatedProtocol.Toledo8217 && data.Contains((byte)'W'))
        {
            Respond(device.ToledoFrame());
        }
        else if (protocol == SimulatedProtocol.Cas && !continuous)
        {
            Respond(device.CasFrame());
        }
    }

    public int Read(byte[] buffer, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (continuous && !device.Silent && Count() == 0)
        {
            Respond(protocol == SimulatedProtocol.Cas ? device.CasFrame() : device.ToledoFrame());
        }

        if (Count() == 0 && !_arrived.Wait(Math.Max(1, timeoutMs)))
        {
            return 0;
        }

        lock (_gate)
        {
            var n = 0;
            while (n < buffer.Length && _pending.Count > 0)
            {
                buffer[n++] = _pending.Dequeue();
            }

            return n;
        }
    }

    public void DiscardInput()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    public void Dispose() => _arrived.Dispose();

    private void Respond(byte[] frame) => Inject(Noise is { Length: > 0 } noise ? [.. noise, .. frame] : frame);

    private int Count()
    {
        lock (_gate)
        {
            return _pending.Count;
        }
    }
}
