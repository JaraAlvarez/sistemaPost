namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>Tipo de documento electrónico en Factus (define la ruta: bills, credit-notes, support-documents).</summary>
public enum FactusDocumentKind
{
    Bill,
    CreditNote,
    SupportDocument,
}

/// <summary>Mensaje de Factus o de la DIAN: regla ("FAK24", "FAJ44b") o campo ("customer.identification") y texto.</summary>
public sealed record FactusMessage(string Code, string Message)
{
    /// <summary>La DIAN marca los rechazos con la palabra "Rechazo"; "Notificación" no invalida el documento
    /// (https://developers.factus.com.co/manejo-errores).</summary>
    public bool IsRejection => Message.Contains("rechazo", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>Documento tal como lo devuelve Factus (creación, consulta o listado).</summary>
public sealed record FactusDocument
{
    public required FactusDocumentKind Kind { get; init; }
    public string? ReferenceCode { get; init; }

    /// <summary>Número fiscal con prefijo asignado por el rango (ej. "SETP990000550").</summary>
    public string? Number { get; init; }

    /// <summary>CUFE (factura), CUDE (nota) o CUDS (documento soporte).</summary>
    public string? Cufe { get; init; }

    /// <summary><c>is_validated</c> (v2) o <c>status</c> = 1 (listado/v1).</summary>
    public bool IsValidated { get; init; }

    /// <summary>Texto tal cual lo envía Factus (<c>DD-MM-YYYY hh:mm:ss AM/PM</c>).</summary>
    public string? ValidatedAt { get; init; }

    /// <summary>URL del QR (portal de la DIAN) para imprimir en el tiquete.</summary>
    public string? QrUrl { get; init; }

    public string? PublicUrl { get; init; }
    public decimal? Total { get; init; }

    /// <summary>Notificaciones y rechazos de la DIAN (<c>data.errors</c>).</summary>
    public IReadOnlyList<FactusMessage> DianMessages { get; init; } = [];

    /// <summary>JSON recibido (para guardar como evidencia en el documento fiscal).</summary>
    public string? RawJson { get; init; }

    public bool HasRejection => !IsValidated && DianMessages.Any(m => m.IsRejection);
}

/// <summary>Resultado de enviar un documento a Factus.</summary>
public enum FactusOutcome
{
    /// <summary>Aceptado: validado por la DIAN; trae número, CUFE y QR.</summary>
    Accepted,

    /// <summary>Duplicado: el <c>reference_code</c> ya existía en Factus; se devuelve el documento existente (consultado).</summary>
    Duplicate,

    /// <summary>Registrado pero la DIAN aún no responde (<c>is_validated</c> false sin rechazo): reenviar los MISMOS datos.</summary>
    Pending,

    /// <summary>Rechazado por la DIAN (regla "…Rechazo…") o por validación (422/400). Requiere corrección.</summary>
    Rejected,

    /// <summary>Error transitorio (red, tiempo agotado, 5xx, 429): reintentar más tarde (ver <see cref="FactusResult.RetryAfter"/>).</summary>
    TransientError,

    /// <summary>Credenciales o cuenta: 401 tras renovar, 403, 402 (paquete agotado) o Factus sin configurar.</summary>
    CredentialsError,
}

/// <summary>Resultado tipado de una emisión. Nunca contiene credenciales ni tokens.</summary>
public sealed record FactusResult
{
    public required FactusOutcome Outcome { get; init; }
    public FactusDocument? Document { get; init; }
    public IReadOnlyList<FactusMessage> Messages { get; init; } = [];
    public int? HttpStatus { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public string? Detail { get; init; }

    /// <summary>
    /// Un documento rechazado queda en Factus y BLOQUEA los siguientes envíos (409 "Se encontró una factura pendiente por
    /// enviar a la DIAN") hasta eliminarlo con <see cref="IFactusApi.DeleteUnvalidatedAsync"/> y reenviarlo corregido.
    /// </summary>
    public bool BlocksFurtherSubmissions { get; init; }

    public bool IsSuccess => Outcome is FactusOutcome.Accepted || (Outcome is FactusOutcome.Duplicate && Document?.IsValidated == true);

    internal static FactusResult Transient(string detail, int? status = null, TimeSpan? retryAfter = null) =>
        new() { Outcome = FactusOutcome.TransientError, Detail = detail, HttpStatus = status, RetryAfter = retryAfter };

    internal static FactusResult Credentials(string detail, int? status = null) =>
        new() { Outcome = FactusOutcome.CredentialsError, Detail = detail, HttpStatus = status };

    public override string ToString() =>
        $"{Outcome} {Document?.Number} {(Messages.Count > 0 ? string.Join("; ", Messages) : Detail)}".TrimEnd();
}

public enum FactusQueryStatus
{
    Found,
    NotFound,
    TransientError,
    CredentialsError,

    /// <summary>Respuesta inesperada (400/422/otro): no se reintenta automáticamente.</summary>
    Failed,
}

/// <summary>Resultado de una consulta (rangos, documento, descarga, eliminación).</summary>
public sealed record FactusQueryResult<T>
{
    public required FactusQueryStatus Status { get; init; }
    public T? Value { get; init; }
    public int? HttpStatus { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public string? Detail { get; init; }

    public bool IsFound => Status == FactusQueryStatus.Found;

    internal static FactusQueryResult<T> Found(T value, int status) => new() { Status = FactusQueryStatus.Found, Value = value, HttpStatus = status };

    internal static FactusQueryResult<T> From(FactusResult failure) => new()
    {
        Status = failure.Outcome switch
        {
            FactusOutcome.TransientError => FactusQueryStatus.TransientError,
            FactusOutcome.CredentialsError => FactusQueryStatus.CredentialsError,
            _ => FactusQueryStatus.Failed,
        },
        HttpStatus = failure.HttpStatus,
        RetryAfter = failure.RetryAfter,
        Detail = failure.Detail,
    };
}

/// <summary>Rango de numeración de Factus (https://developers.factus.com.co/rangos-de-numeracion/facturación/obtener-rangos).</summary>
public sealed record FactusNumberingRange
{
    public required int Id { get; init; }

    /// <summary>Nombre o código del documento ("21" factura, "22" nota crédito, "24" documento soporte…).</summary>
    public string? Document { get; init; }

    public string? Prefix { get; init; }
    public long From { get; init; }
    public long To { get; init; }

    /// <summary>Siguiente número del rango.</summary>
    public long Current { get; init; }

    public string? ResolutionNumber { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? TechnicalKey { get; init; }
    public bool IsExpired { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Autenticación exitosa (sin exponer el token).</summary>
public sealed record FactusSession(DateTimeOffset ExpiresAt);

/// <summary>Archivo descargado (PDF o XML), ya decodificado del Base64.</summary>
public sealed record FactusFile(string FileName, string ContentType, byte[] Content);
