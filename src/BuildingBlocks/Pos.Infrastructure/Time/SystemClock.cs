using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.Time;

/// <summary>Reloj real basado en <see cref="TimeProvider"/> (sustituible en pruebas).</summary>
public sealed class SystemClock(TimeProvider timeProvider, TimeZoneInfo businessTimeZone) : IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public TimeZoneInfo BusinessTimeZone { get; } = businessTimeZone;
}
