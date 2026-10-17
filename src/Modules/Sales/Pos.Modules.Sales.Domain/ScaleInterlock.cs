using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Measurement;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Domain;

/// <summary>
/// Retorno a cero de la báscula de una caja, en el SERVIDOR (Fase 7-C, decisiones D1 y D3 de la revisión; tabla
/// <c>sales.scale_interlocks</c>): la última lectura firmada que se aceptó para una línea y si después hay que ver la báscula vacía.
/// Vive por caja (no por venta ni por pantalla): recargar la página o empezar otra venta no lo borra.
/// <para>BB4: el orden entre lecturas usa el arranque del agente y su secuencia (nunca la hora, que puede saltar). Si el agente se
/// reinició (otro arranque), basta que haya visto la báscula vacía desde que arrancó. La hora de la lectura solo se compara con el reloj
/// del servidor para rechazar lecturas viejas o del futuro (⚙️ 5 min); no se guarda ningún máximo que deje la caja trabada.</para>
/// </summary>
public sealed class ScaleInterlock : Entity<Guid>
{
    // El Id es la caja (pos_terminal_id).
    private ScaleInterlock(Guid id)
        : base(id)
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid? LastReadingId { get; private set; }

    /// <summary>Hora (reloj del agente) de la última lectura aceptada (informativa).</summary>
    public DateTimeOffset? LastReadingAt { get; private set; }

    /// <summary>Máximo de arranques retirados que se recuerdan por caja.</summary>
    public const int MaxRetiredBoots = 20;

    /// <summary>Arranque del agente de la última lectura aceptada.</summary>
    public Guid? LastBootId { get; private set; }

    /// <summary>
    /// BK2: arranques del agente que ya fueron reemplazados por uno posterior (con una pesada aceptada). Una lectura de un arranque
    /// retirado nunca vale: el agente que la firmó ya se reinició.
    /// </summary>
    public Guid[] RetiredBootIds { get; private set; } = [];

    /// <summary>BQ3: número de arranque más alto aceptado de esta caja; un arranque menor (anterior) nunca vale.</summary>
    public long? LastBootCounter { get; private set; }

    /// <summary>
    /// BS1: clave de la báscula con la que se aceptó la última lectura. El número de arranque, la secuencia y los arranques retirados
    /// valen solo para esa clave: con otra (volver a vincular) se empieza de cero, así que reinstalar el agente o cambiar el computador
    /// nunca deja la caja trabada.
    /// </summary>
    public Guid? LastKeyId { get; private set; }

    /// <summary>
    /// BS1: la lectura viene firmada con otra clave (la caja se volvió a vincular): el orden de los arranques empieza de cero. El retorno
    /// a cero pendiente se conserva (el agente nuevo debe ver la báscula vacía desde que arrancó). No se guarda nada si la línea no se agrega.
    /// </summary>
    public void AdoptKey(Guid keyId)
    {
        if (LastKeyId is { } current && current != keyId)
        {
            LastBootId = null;
            LastSeq = null;
            LastBootCounter = null;
            RetiredBootIds = [];
        }
    }

    /// <summary>Secuencia (en ese arranque) de la última lectura aceptada.</summary>
    public long? LastSeq { get; private set; }

    public decimal? LastGrossKg { get; private set; }

    public Guid? LastSaleId { get; private set; }

    public Guid? LastSaleLineId { get; private set; }

    /// <summary>Se agregó una pesada y todavía no llega una lectura firmada que muestre la báscula vacía después de ella.</summary>
    public bool AwaitingEmpty { get; private set; }

    /// <summary>Última limpieza de las lecturas viejas de esta caja (BB10).</summary>
    public DateTimeOffset? LastPurgeAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ScaleInterlock Start(Guid posTerminalId, Guid companyId, DateTimeOffset now) => new(posTerminalId) { CompanyId = companyId, UpdatedAt = now };

    /// <summary>¿Se puede agregar una línea con esta lectura? (hora frente al servidor, orden en el agente y retorno a cero).</summary>
    public Result Check(ScaleReadingPayload reading, DateTimeOffset serverNow, TimeSpan maxSkew)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if ((reading.At - serverNow).Duration() > maxSkew)
        {
            return SalesErrors.ScaleClockSkew;
        }

        // Una lectura de un arranque que ya conocemos y quedó atrás es una lectura vieja; un número de arranque menor que nunca vimos es un
        // agente que volvió atrás (reinstalado o restaurado): solo volver a vincular lo destraba (BS1).
        if (RetiredBootIds.Contains(reading.BootId))
        {
            return SalesErrors.ScaleReadingStale;
        }

        if (LastBootCounter is { } counter && reading.BootCounter < counter)
        {
            return SalesErrors.ScaleBootRegressed;
        }

        var sameBoot = LastBootId == reading.BootId;
        if (sameBoot && LastSeq is { } last && reading.Seq <= last)
        {
            return SalesErrors.ScaleReadingStale;
        }

        if (AwaitingEmpty && (reading.LastEmptySeq is not { } empty || (sameBoot && LastSeq is { } accepted && empty <= accepted)))
        {
            return SalesErrors.ScaleNotEmptied;
        }

        return Result.Success();
    }

    /// <summary>Se agregó la línea con esta lectura (firmada con <paramref name="keyId"/>): la siguiente pesada exige ver la báscula vacía.</summary>
    public void Accept(ScaleReadingPayload reading, Guid keyId, Guid saleId, Guid saleLineId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        AdoptKey(keyId);
        LastKeyId = keyId;
        LastReadingId = reading.ReadingId;
        LastReadingAt = reading.At;
        if (LastBootId is { } previous && previous != reading.BootId && !RetiredBootIds.Contains(previous))
        {
            RetiredBootIds = [.. RetiredBootIds.Append(previous).TakeLast(MaxRetiredBoots)];
        }

        LastBootId = reading.BootId;
        LastBootCounter = LastBootCounter is { } counter && counter > reading.BootCounter ? counter : reading.BootCounter;
        LastSeq = reading.Seq;
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

    /// <summary>"Reiniciar báscula" (BB5, con supervisor y auditoría): libera el retorno a cero de la caja.</summary>
    public bool Reset(DateTimeOffset now)
    {
        var was = AwaitingEmpty;
        AwaitingEmpty = false;
        UpdatedAt = now;
        return was;
    }

    /// <summary>¿Toca limpiar las lecturas viejas de la caja? (una vez al día como máximo).</summary>
    public bool PurgeDue(DateTimeOffset now)
    {
        if (LastPurgeAt is { } last && now - last < TimeSpan.FromDays(1))
        {
            return false;
        }

        LastPurgeAt = now;
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

    public Guid BootId { get; private set; }

    public long BootCounter { get; private set; }

    public long Seq { get; private set; }

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
            BootId = reading.BootId,
            BootCounter = reading.BootCounter,
            Seq = reading.Seq,
            GrossKg = reading.GrossKg,
            TareKg = reading.TareKg,
            NetKg = reading.NetKg,
            Payload = payload,
            Signature = signature,
            RecordedAt = now,
        };
    }
}
