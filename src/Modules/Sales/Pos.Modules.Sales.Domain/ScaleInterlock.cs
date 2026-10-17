using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Measurement;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Domain;

/// <summary>
/// Retorno a cero de la báscula de una caja, en el SERVIDOR (Fase 7-C, decisiones D1 y D3 de la revisión; tabla
/// <c>sales.scale_interlocks</c>): la última lectura firmada que se aceptó para una línea y si después hay que ver la báscula vacía.
/// Vive por caja (no por venta ni por pantalla): recargar la página o empezar otra venta no lo borra. Las horas son del RELOJ DEL
/// AGENTE (nunca se comparan con el reloj del servidor).
/// </summary>
public sealed class ScaleInterlock : Entity<Guid>
{
    /// <summary>Una lectura más vieja que la última aceptada en más de este tiempo (reloj del agente) se rechaza (⚙️ 30 s).</summary>
    public static readonly TimeSpan MaxBacklog = TimeSpan.FromSeconds(30);

    // El Id es la caja (pos_terminal_id).
    private ScaleInterlock(Guid id)
        : base(id)
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid? LastReadingId { get; private set; }

    /// <summary>Hora (reloj del agente) de la última lectura aceptada.</summary>
    public DateTimeOffset? LastReadingAt { get; private set; }

    public decimal? LastGrossKg { get; private set; }

    public Guid? LastSaleId { get; private set; }

    public Guid? LastSaleLineId { get; private set; }

    /// <summary>Se agregó una pesada y todavía no llega una lectura firmada que muestre la báscula vacía después de ella.</summary>
    public bool AwaitingEmpty { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ScaleInterlock Start(Guid posTerminalId, Guid companyId, DateTimeOffset now) => new(posTerminalId) { CompanyId = companyId, UpdatedAt = now };

    /// <summary>¿Se puede agregar una línea con esta lectura? (antigüedad por el reloj del agente y retorno a cero).</summary>
    public Result Check(ScaleReadingPayload reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (LastReadingAt is { } last && reading.At < last - MaxBacklog)
        {
            return SalesErrors.ScaleReadingStale;
        }

        if (AwaitingEmpty && (reading.LastEmptyAt is not { } empty || LastReadingAt is not { } accepted || empty <= accepted))
        {
            return SalesErrors.ScaleNotEmptied;
        }

        return Result.Success();
    }

    /// <summary>Se agregó la línea con esta lectura: la siguiente pesada exige ver la báscula vacía.</summary>
    public void Accept(ScaleReadingPayload reading, Guid saleId, Guid saleLineId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        LastReadingId = reading.ReadingId;
        LastReadingAt = LastReadingAt is { } last && last > reading.At ? last : reading.At;
        LastGrossKg = reading.GrossKg;
        LastSaleId = saleId;
        LastSaleLineId = saleLineId;
        AwaitingEmpty = true;
        UpdatedAt = now;
    }

    /// <summary>Se eliminó la línea de la última pesada (para volver a pesar): ya no hace falta retirar el producto.</summary>
    public bool Release(Guid saleLineId, DateTimeOffset now)
    {
        if (!AwaitingEmpty || LastSaleLineId != saleLineId)
        {
            return false;
        }

        AwaitingEmpty = false;
        UpdatedAt = now;
        return true;
    }
}

/// <summary>
/// Cada lectura firmada que se usó en una línea (tabla <c>sales.scale_readings</c>): el texto y la firma tal como llegaron (auditoría)
/// y su identificador único, que impide usar dos veces la misma lectura.
/// </summary>
public sealed class ScaleReadingRecord : Entity<Guid>
{
    // El Id es el identificador de la lectura (reading_id).
    private ScaleReadingRecord(Guid id)
        : base(id)
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid PosTerminalId { get; private set; }

    public Guid SaleId { get; private set; }

    public Guid SaleLineId { get; private set; }

    public Guid KeyId { get; private set; }

    public DateTimeOffset AgentAt { get; private set; }

    public decimal GrossKg { get; private set; }

    public decimal TareKg { get; private set; }

    public decimal NetKg { get; private set; }

    public string Payload { get; private set; } = string.Empty;

    public string Signature { get; private set; } = string.Empty;

    public DateTimeOffset RecordedAt { get; private set; }

    public static ScaleReadingRecord Of(
        ScaleReadingPayload reading, string payload, string signature, Guid keyId, Guid companyId, Guid saleId, Guid saleLineId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return new ScaleReadingRecord(reading.ReadingId)
        {
            CompanyId = companyId,
            PosTerminalId = reading.TerminalId,
            SaleId = saleId,
            SaleLineId = saleLineId,
            KeyId = keyId,
            AgentAt = reading.At,
            GrossKg = reading.GrossKg,
            TareKg = reading.TareKg,
            NetKg = reading.NetKg,
            Payload = payload,
            Signature = signature,
            RecordedAt = now,
        };
    }
}
