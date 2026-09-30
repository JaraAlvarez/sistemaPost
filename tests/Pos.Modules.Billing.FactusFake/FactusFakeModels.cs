namespace Pos.Modules.Billing.FactusFake;

/// <summary>Tipo de documento simulado (ruta v2 y prefijo del rango por defecto).</summary>
public enum FakeDocumentKind
{
    Bill,
    CreditNote,
    SupportDocument,

    /// <summary>Nota de ajuste al documento soporte (rango "25").</summary>
    AdjustmentNote,
}

public enum FakeDocumentState
{
    /// <summary>Validado por la DIAN.</summary>
    Validated,

    /// <summary>Registrado; la DIAN aún no responde (se valida al reenviar los mismos datos).</summary>
    Pending,

    /// <summary>Rechazado por la DIAN: bloquea los envíos siguientes hasta eliminarlo.</summary>
    Rejected,
}

/// <summary>Qué responde el simulado cuando llega un <c>reference_code</c> ya validado.</summary>
public enum DuplicateReferenceBehavior
{
    /// <summary>Idempotencia documentada en las preguntas frecuentes: devuelve el documento existente (HTTP 200).</summary>
    ReturnExisting,

    /// <summary>Variante defensiva: HTTP 409 Conflict (el cliente debe consultar por reference_code).</summary>
    Conflict,
}

/// <summary>Falla inyectada en la siguiente petición a la API (no afecta a <c>oauth/token</c> salvo que se indique).</summary>
public sealed record FakeFault
{
    public int StatusCode { get; init; }
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Cierra la conexión sin respuesta (el cliente ve un error de red).</summary>
    public bool Drop { get; init; }

    public bool AppliesToOAuth { get; init; }

    public static FakeFault TooManyRequests(TimeSpan retryAfter) => new() { StatusCode = 429, RetryAfter = retryAfter };

    public static FakeFault ServerError(int statusCode = 500) => new() { StatusCode = statusCode };

    public static FakeFault Unavailable(TimeSpan? retryAfter = null) => new() { StatusCode = 503, RetryAfter = retryAfter };

    public static FakeFault ConnectionDrop() => new() { Drop = true };
}

/// <summary>Rango de numeración simulado (campos de GET v2/numbering-ranges).</summary>
public sealed class FakeNumberingRange
{
    public required int Id { get; init; }

    /// <summary>"21" factura, "22" nota crédito, "24" documento soporte, "25" nota de ajuste al documento soporte.</summary>
    public required string Document { get; init; }

    public required string Prefix { get; init; }
    public long From { get; init; } = 1;
    public long To { get; init; } = 99_999_999;
    public long Current { get; set; } = 1;
    public string ResolutionNumber { get; init; } = "18760000001";
    public DateOnly StartDate { get; init; } = new(2026, 1, 1);
    public DateOnly EndDate { get; init; } = new(2027, 12, 31);
    public string? TechnicalKey { get; init; } = "fc8eac422eba16e22ffd8c6f94b3f40a6e38162c";
    public bool IsExpired { get; init; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Documento guardado en el simulado.</summary>
public sealed class FakeDocument
{
    public required FakeDocumentKind Kind { get; init; }
    public required string ReferenceCode { get; init; }
    public required string Number { get; init; }
    public required string Cufe { get; init; }
    public required int NumberingRangeId { get; init; }
    public FakeDocumentState State { get; set; }
    public Dictionary<string, string> Errors { get; } = [];
    public required string RequestJson { get; init; }
    public required FakeTotals Totals { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ValidatedAt { get; set; }
}

/// <summary>Totales calculados como Factus: cada impuesto por separado con redondeo bancario.</summary>
public sealed record FakeTotals(decimal GrossAmount, decimal DiscountAmount, decimal TaxableAmount, decimal TaxAmount, decimal Total);

/// <summary>Petición recibida (para verificar en las pruebas qué envió el cliente). El token nunca se guarda.</summary>
public sealed record FakeRecordedRequest(string Method, string PathAndQuery, string Body, bool HadBearerToken);
