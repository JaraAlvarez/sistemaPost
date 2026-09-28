namespace Pos.SharedKernel.Time;

/// <summary>
/// Reloj del sistema. Todo el código obtiene la hora de aquí (nunca de DateTime.Now) para que:
/// las fechas se guarden en UTC (RN-GEN-08), la fecha de negocio use la zona de la empresa
/// y las pruebas puedan simular el tiempo (vencimientos de licencia, jornadas nocturnas).
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Zona horaria del negocio (Colombia: America/Bogota, UTC-5 sin horario de verano).</summary>
    TimeZoneInfo BusinessTimeZone { get; }

    /// <summary>Fecha calendario actual en la zona del negocio.</summary>
    DateOnly Today => BusinessDateOf(UtcNow);

    DateTimeOffset ToBusinessTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, BusinessTimeZone);

    DateOnly BusinessDateOf(DateTimeOffset instant) => DateOnly.FromDateTime(ToBusinessTime(instant).DateTime);
}

public static class BusinessTimeZones
{
    public const string ColombiaIanaId = "America/Bogota";

    private const string ColombiaWindowsId = "SA Pacific Standard Time";

    /// <summary>Resuelve la zona de Colombia por su Id IANA, con respaldo al Id de Windows.</summary>
    public static TimeZoneInfo Colombia => Find(ColombiaIanaId);

    public static TimeZoneInfo Find(string id)
    {
        Guard.NotNullOrWhiteSpace(id);
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
        {
            return zone;
        }

        if (id == ColombiaIanaId && TimeZoneInfo.TryFindSystemTimeZoneById(ColombiaWindowsId, out zone))
        {
            return zone;
        }

        throw new TimeZoneNotFoundException($"No se encontró la zona horaria '{id}'.");
    }
}
