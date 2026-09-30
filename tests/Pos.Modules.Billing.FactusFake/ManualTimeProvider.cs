namespace Pos.Modules.Billing.FactusFake;

/// <summary>Reloj manual para compartir entre el cliente y el simulado (vencimiento de tokens, ventana del límite de ritmo).</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.FromHours(-5)))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now.ToUniversalTime();
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
            _now += delta;
    }
}
