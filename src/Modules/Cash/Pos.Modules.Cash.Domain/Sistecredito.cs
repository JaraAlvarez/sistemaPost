using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Cash.Domain;

/// <summary>Ambiente de Sistecrédito (cabecera <c>SCOrigen</c>): cada ambiente tiene su propio juego de llaves (D7B-02).</summary>
public enum SistecreditoEnvironment
{
    Staging,
    Production,
}

/// <summary>Estado de un cobro con Sistecrédito en la caja (D7B-05).</summary>
public enum SistecreditoAttemptStatus
{
    /// <summary>Creado en Sistecrédito: el cliente todavía no termina en su celular.</summary>
    Pending,

    /// <summary>El cliente aprobó el crédito: la caja puede completar la venta con este cobro (una sola vez).</summary>
    Approved,

    Rejected,

    Expired,

    Cancelled,

    /// <summary>Aprobado pero sin venta completada (o aprobado después de cancelar): la administración debe revisarlo.</summary>
    ReviewRequired,
}

/// <summary>Reglas del protocolo de Sistecrédito que no dependen de HTTP: estados, documentos y enmascarado.</summary>
public static class SistecreditoProtocol
{
    /// <summary>Estado de la transacción aprobada en Sistecrédito.</summary>
    public const string ApprovedStatus = "Approved";

    /// <summary>Estado local cuando Sistecrédito responde que la transacción no existe (error 708).</summary>
    public const string NotFoundStatus = "NotFound";

    /// <summary>Error de Sistecrédito: transacción no encontrada.</summary>
    public const int NotFoundErrorCode = 708;

    /// <summary>Tipos de documento que acepta la caja (D7B-07).</summary>
    public static readonly IReadOnlyList<string> DocumentTypes = ["CC", "CE", "NIT", "PA", "TI"];

    /// <summary>Estados finales de fallo en Sistecrédito: ya no aparecerá la URL de pago.</summary>
    public static readonly IReadOnlyList<string> FailedStatuses = ["Rejected", "Cancelled", "Expired", "Abandoned", "Failed"];

    /// <summary>Estado de Sistecrédito → estado del cobro en la caja. Cualquier otro (p. ej. <c>PendingForPaymentMethod</c>) sigue pendiente.</summary>
    public static SistecreditoAttemptStatus Map(string? providerStatus) => providerStatus switch
    {
        ApprovedStatus => SistecreditoAttemptStatus.Approved,
        "Rejected" or "Failed" => SistecreditoAttemptStatus.Rejected,
        "Expired" => SistecreditoAttemptStatus.Expired,
        "Cancelled" or "Abandoned" => SistecreditoAttemptStatus.Cancelled,
        _ => SistecreditoAttemptStatus.Pending,
    };

    public static bool IsFailed(string? providerStatus) => providerStatus is not null && FailedStatuses.Contains(providerStatus);

    /// <summary>Documento sin puntos, espacios ni guiones, en mayúsculas.</summary>
    public static string NormalizeDocument(string? number) =>
        new((number ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Documento para la bitácora y los registros: solo los últimos 4 caracteres (p. ej. <c>••••5678</c>).</summary>
    public static string Mask(string? number)
    {
        var value = number ?? string.Empty;
        return value.Length <= 4 ? new string('•', value.Length) : $"••••{value[^4..]}";
    }
}

/// <summary>
/// Configuración de Sistecrédito de la empresa (D7B-02): llaves del convenio cifradas con DPAPI (el dominio solo ve bytes), ambiente
/// y si está encendido. La API nunca devuelve las llaves; solo los últimos 4 caracteres de la llave de suscripción.
/// </summary>
public sealed class SistecreditoSettings : ICompanyOwned
{
    private SistecreditoSettings(Guid companyId)
    {
        CompanyId = companyId;
    }

    public Guid CompanyId { get; private set; }

    public SistecreditoEnvironment Environment { get; private set; } = SistecreditoEnvironment.Staging;

    public bool IsEnabled { get; private set; }

#pragma warning disable CA1819 // Bytes cifrados que EF guarda tal cual.
    public byte[]? Credentials { get; private set; }
#pragma warning restore CA1819

    public DateTimeOffset? CredentialsUpdatedAt { get; private set; }

    /// <summary>Últimos 4 caracteres de la llave de suscripción, para reconocerla en pantalla.</summary>
    public string? KeyHint { get; private set; }

    public DateTimeOffset? LastVerifiedAt { get; private set; }

    public bool? LastVerificationOk { get; private set; }

    public string? LastVerificationMessage { get; private set; }

    public bool HasCredentials => Credentials is { Length: > 0 };

    public static SistecreditoSettings Create(Guid companyId) => new(companyId);

    /// <summary>Guarda llaves nuevas (ya cifradas) si llegan, el ambiente y el encendido. Encender exige llaves.</summary>
    public Result Configure(byte[]? credentials, string? keyHint, SistecreditoEnvironment environment, bool enabled, DateTimeOffset now)
    {
        if (credentials is { Length: > 0 })
        {
            Credentials = credentials;
            CredentialsUpdatedAt = now;
            KeyHint = keyHint is { Length: > 4 } ? keyHint[^4..] : keyHint;
            LastVerifiedAt = null;
            LastVerificationOk = null;
            LastVerificationMessage = null;
        }

        if (enabled && !HasCredentials)
        {
            return SistecreditoErrors.CredentialsRequired;
        }

        if (environment != Environment)
        {
            LastVerifiedAt = null;
            LastVerificationOk = null;
            LastVerificationMessage = null;
        }

        Environment = environment;
        IsEnabled = enabled;
        return Result.Success();
    }

    public void Disable() => IsEnabled = false;

    public void RecordVerification(DateTimeOffset now, bool ok, string? message)
    {
        LastVerifiedAt = now;
        LastVerificationOk = ok;
        LastVerificationMessage = message is { Length: > 500 } ? message[..500] : message;
    }
}

/// <summary>
/// Un cobro con Sistecrédito desde la caja (D7B-05): queda guardado siempre. La venta solo se completa con un cobro APROBADO, de la misma
/// venta y por el mismo valor, que no se haya usado antes. Un aprobado sin venta queda "Requiere revisión" para la administración.
/// </summary>
public sealed class SistecreditoAttempt : Entity<Guid>, ICompanyOwned
{
    private SistecreditoAttempt(Guid id)
        : base(id)
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid SaleId { get; private set; }

    public Guid? PosTerminalId { get; private set; }

    /// <summary>Factura enviada a Sistecrédito (única por cobro: Sistecrédito rechaza una factura repetida, error 738).</summary>
    public string Invoice { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string DocumentType { get; private set; } = string.Empty;

    public string DocumentNumber { get; private set; } = string.Empty;

    public SistecreditoEnvironment Environment { get; private set; }

    public string? TransactionId { get; private set; }

    /// <summary>Último estado que informó Sistecrédito, tal cual (p. ej. <c>PendingForPaymentMethod</c>, <c>Approved</c>).</summary>
    public string? ProviderStatus { get; private set; }

    public SistecreditoAttemptStatus Status { get; private set; } = SistecreditoAttemptStatus.Pending;

    public string? RedirectUrl { get; private set; }

    /// <summary>Cuotas que eligió el cliente, si Sistecrédito las informa.</summary>
    public int? Installments { get; private set; }

    /// <summary>Motivo del rechazo, de la cancelación o de la revisión, en palabras simples.</summary>
    public string? Message { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? LastCheckedAt { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public Guid? CompletedSaleId { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public DateTimeOffset? ReviewRequiredAt { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public string? ReviewNote { get; private set; }

    public string MaskedDocument => SistecreditoProtocol.Mask(DocumentNumber);

    /// <summary>
    /// Sirve para completar SU venta: el cliente aprobó (aunque haya quedado "Requiere revisión": la plata ya se cobró), Sistecrédito lo
    /// sigue reportando aprobado, nadie lo usó y la administración no lo cerró.
    /// </summary>
    public bool IsUsable =>
        Status is SistecreditoAttemptStatus.Approved or SistecreditoAttemptStatus.ReviewRequired
        && ApprovedAt is not null && CompletedSaleId is null && ReviewedAt is null && ProviderStatus == SistecreditoProtocol.ApprovedStatus;

    public static Result<SistecreditoAttempt> Create(
        Guid id, Guid companyId, Guid saleId, Guid? posTerminalId, decimal amount, string? documentType, string? documentNumber,
        SistecreditoEnvironment environment, DateTimeOffset now)
    {
        if (amount <= 0m || amount > 999_999_999m)
        {
            return SistecreditoErrors.InvalidAmount;
        }

        // Sistecrédito cobra pesos enteros: la parte con centavos se cobra con otro medio (la caja ya propone el valor sin centavos).
        if (decimal.Truncate(amount) != amount)
        {
            var whole = decimal.Truncate(amount);
            return whole > 0m
                ? Error.Validation(
                    SistecreditoErrors.InvalidAmount.Code,
                    $"Sistecrédito solo cobra pesos enteros (sin centavos): cobre {whole:N0} con Sistecrédito y los {amount - whole:N2} restantes con otro medio de pago.")
                : SistecreditoErrors.InvalidAmount;
        }

        var type = (documentType ?? string.Empty).Trim().ToUpperInvariant();
        var number = SistecreditoProtocol.NormalizeDocument(documentNumber);
        if (!SistecreditoProtocol.DocumentTypes.Contains(type) || number.Length is < 3 or > 20)
        {
            return SistecreditoErrors.InvalidDocument;
        }

        return new SistecreditoAttempt(id)
        {
            CompanyId = companyId,
            SaleId = saleId,
            PosTerminalId = posTerminalId,
            Invoice = $"BP-{id:N}",
            Amount = amount,
            DocumentType = type,
            DocumentNumber = number,
            Environment = environment,
            StartedAt = now,
        };
    }

    /// <summary>Sistecrédito creó la transacción.</summary>
    public void Started(string transactionId, string? providerStatus, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        TransactionId = transactionId.Length > 60 ? transactionId[..60] : transactionId;
        Apply(providerStatus, null, null, null, now);
    }

    /// <summary>
    /// Aplica lo que respondió Sistecrédito. Pendiente → aprobado o fallido; un cobro cancelado en la caja que el cliente igual aprobó
    /// queda "Requiere revisión". Devuelve <c>true</c> si cambió el estado.
    /// </summary>
    public bool Apply(string? providerStatus, string? redirectUrl, int? installments, string? description, DateTimeOffset now)
    {
        var before = Status;
        LastCheckedAt = now;

        // Un estado intermedio después de aprobado no borra la aprobación (solo un estado final distinto de Approved la invalida).
        if (!string.IsNullOrWhiteSpace(providerStatus)
            && !(ApprovedAt is not null && SistecreditoProtocol.Map(providerStatus) == SistecreditoAttemptStatus.Pending))
        {
            ProviderStatus = providerStatus.Length > 40 ? providerStatus[..40] : providerStatus;
        }

        if (!string.IsNullOrWhiteSpace(redirectUrl) && Uri.TryCreate(redirectUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            RedirectUrl = redirectUrl.Length > 1000 ? redirectUrl[..1000] : redirectUrl;
        }

        if (installments is > 0 and < 1000)
        {
            Installments = installments;
        }

        var mapped = SistecreditoProtocol.Map(providerStatus);
        switch (Status)
        {
            case SistecreditoAttemptStatus.Pending when mapped == SistecreditoAttemptStatus.Approved:
                Status = SistecreditoAttemptStatus.Approved;
                ApprovedAt = now;
                Message = null;
                break;
            case SistecreditoAttemptStatus.Pending when mapped is not SistecreditoAttemptStatus.Pending:
                Status = mapped;
                FinishedAt = now;
                Message = FailureText(mapped, description);
                break;
            case SistecreditoAttemptStatus.Cancelled when mapped == SistecreditoAttemptStatus.Approved && CompletedSaleId is null:
                ApprovedAt = now;
                RequireReview("El cliente aprobó el crédito después de cancelar el cobro en la caja.", now);
                break;

            // Aprobado y sin usar, pero Sistecrédito ya no lo reporta aprobado (reversado, cancelado…): no se puede usar y se revisa.
            case SistecreditoAttemptStatus.Approved or SistecreditoAttemptStatus.ReviewRequired
                when CompletedSaleId is null && ReviewedAt is null && !string.IsNullOrWhiteSpace(providerStatus)
                     && providerStatus != SistecreditoProtocol.ApprovedStatus && mapped != SistecreditoAttemptStatus.Pending:
                RequireReview(
                    Trim($"Sistecrédito ya no reporta aprobado este cobro (estado {ProviderStatus}): no se puede usar en la venta. Revíselo en el portal de Sistecrédito."),
                    now);
                break;
        }

        return Status != before;
    }

    /// <summary>Se consultó (o se intentó consultar) a Sistecrédito: la revisión automática le da turno al que lleva más tiempo sin mirar.</summary>
    public void Checked(DateTimeOffset now) => LastCheckedAt = now;

    /// <summary>
    /// No se alcanzó a crear la transacción en Sistecrédito (error o sin conexión): el registro local queda cancelado con el motivo. Si
    /// la creación sí llegó a Sistecrédito, el cliente nunca recibió el enlace de pago, así que no puede aprobarla.
    /// </summary>
    public void StartFailed(string reason, DateTimeOffset now)
    {
        if (Status != SistecreditoAttemptStatus.Pending || TransactionId is not null)
        {
            return;
        }

        Status = SistecreditoAttemptStatus.Cancelled;
        FinishedAt = now;
        LastCheckedAt = now;
        Message = Trim($"No se pudo crear el cobro en Sistecrédito: {reason}");
    }

    /// <summary>Un pendiente que ya no va a terminar (lleva demasiado tiempo o Sistecrédito no lo encuentra) se da por vencido.</summary>
    public bool Expire(string reason, DateTimeOffset now)
    {
        if (Status != SistecreditoAttemptStatus.Pending)
        {
            return false;
        }

        Status = SistecreditoAttemptStatus.Expired;
        FinishedAt = now;
        LastCheckedAt = now;
        Message = Trim(reason);
        return true;
    }

    /// <summary>
    /// Sistecrédito no encuentra la transacción (error 708): si alguna vez se aprobó y no se usó, la administración debe revisarlo; si
    /// seguía pendiente, se da por vencido.
    /// </summary>
    public void NotFoundAtProvider(DateTimeOffset now)
    {
        LastCheckedAt = now;
        ProviderStatus = SistecreditoProtocol.NotFoundStatus;
        if (ApprovedAt is not null && CompletedSaleId is null && ReviewedAt is null)
        {
            if (Status != SistecreditoAttemptStatus.ReviewRequired)
            {
                RequireReview("Sistecrédito ya no encuentra la transacción de este cobro aprobado: revíselo en el portal de Sistecrédito.", now);
            }
        }
        else
        {
            Expire("Sistecrédito no encuentra la transacción: el cobro se da por vencido.", now);
        }
    }

    /// <summary>Cancelado en la caja (Sistecrédito no tiene cómo anularlo: si el cliente igual aprueba, la revisión automática lo detecta).</summary>
    public Result CancelLocally(string reason, DateTimeOffset now)
    {
        if (Status != SistecreditoAttemptStatus.Pending)
        {
            return SistecreditoErrors.NotPending;
        }

        Status = SistecreditoAttemptStatus.Cancelled;
        FinishedAt = now;
        Message = reason;
        return Result.Success();
    }

    /// <summary>La venta se completa con este cobro: misma venta, mismo valor, aprobado y sin usar.</summary>
    public Result Use(Guid saleId, decimal amount, DateTimeOffset now)
    {
        if (CompletedSaleId is not null)
        {
            return SistecreditoErrors.AlreadyUsed;
        }

        if (SaleId != saleId)
        {
            return SistecreditoErrors.OtherSale;
        }

        if (!IsUsable)
        {
            return ReviewedAt is null && ApprovedAt is not null && ProviderStatus != SistecreditoProtocol.ApprovedStatus
                ? SistecreditoErrors.NoLongerApproved
                : SistecreditoErrors.NotApproved;
        }

        if (amount != Amount)
        {
            return Error.BusinessRule(SistecreditoErrors.AmountMismatch.Code, $"{SistecreditoErrors.AmountMismatch.Message} Aprobado: {Amount:N0}.");
        }

        // Un "Requiere revisión" aprobado y sin usar completa su venta y sale de la revisión (la plata del cliente ya se cobró).
        Status = SistecreditoAttemptStatus.Approved;
        CompletedSaleId = saleId;
        UsedAt = now;
        return Result.Success();
    }

    /// <summary>La venta pagada con este cobro se anuló: la administración debe reversar el pago en el portal de Sistecrédito.</summary>
    public bool SaleVoided(DateTimeOffset now)
    {
        if (CompletedSaleId is null || ReviewedAt is not null || Status == SistecreditoAttemptStatus.ReviewRequired)
        {
            return false;
        }

        Status = SistecreditoAttemptStatus.ReviewRequired;
        ReviewRequiredAt = now;
        Message = "Venta anulada: reverse el pago en el portal de Sistecrédito";
        return true;
    }

    /// <summary>Aprobado sin venta: la administración debe completar la venta o reversar el pago en el portal de Sistecrédito.</summary>
    public void RequireReview(string reason, DateTimeOffset now)
    {
        Status = SistecreditoAttemptStatus.ReviewRequired;
        ReviewRequiredAt = now;
        FinishedAt ??= now;
        Message = reason;
    }

    /// <summary>La administración revisó el caso (p. ej. reversó el pago en el portal de Sistecrédito) y deja la nota.</summary>
    public Result Resolve(string? note, Guid userId, DateTimeOffset now)
    {
        var text = (note ?? string.Empty).Trim();
        if (Status != SistecreditoAttemptStatus.ReviewRequired || ReviewedAt is not null)
        {
            return SistecreditoErrors.NotUnderReview;
        }

        if (text.Length is < 5 or > 300)
        {
            return SistecreditoErrors.ReviewNoteRequired;
        }

        ReviewedAt = now;
        ReviewedBy = userId;
        ReviewNote = text;
        return Result.Success();
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] : text;

    private static string FailureText(SistecreditoAttemptStatus status, string? description)
    {
        var detail = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var text = status switch
        {
            SistecreditoAttemptStatus.Rejected => "Sistecrédito no aprobó el crédito.",
            SistecreditoAttemptStatus.Expired => "El cobro venció: el cliente no confirmó a tiempo.",
            _ => "El cobro se canceló en Sistecrédito.",
        };
        text = detail is null ? $"{text} Elija otro medio de pago." : $"{text} Motivo: {detail}. Elija otro medio de pago.";
        return text.Length > 300 ? text[..300] : text;
    }
}

/// <summary>Errores de Sistecrédito con código estable.</summary>
public static class SistecreditoErrors
{
    public static readonly Error NotConfigured = Error.BusinessRule(
        "CASH.SISTECREDITO_NOT_CONFIGURED",
        "Sistecrédito no está activo en esta tienda. La administración lo configura en Configuración → Medios de pago → Sistecrédito.");

    public static readonly Error CredentialsUnreadable = Error.BusinessRule(
        "CASH.SISTECREDITO_CREDENTIALS_UNREADABLE",
        "Las llaves de Sistecrédito no se pueden leer en este equipo (¿se restauró la base de datos en otro computador?): vuelva a ingresarlas.");

    public static readonly Error CredentialsRequired = Error.Validation(
        "CASH.SISTECREDITO_CREDENTIALS_REQUIRED", "Para activar Sistecrédito ingrese la llave de suscripción, la ApplicationKey y el ApplicationToken.");

    public static readonly Error InvalidCredentials = Error.Validation(
        "CASH.SISTECREDITO_INVALID_CREDENTIALS", "Ingrese las tres llaves de Sistecrédito (llave de suscripción, ApplicationKey y ApplicationToken), de hasta 200 caracteres.");

    public static readonly Error InvalidEnvironment = Error.Validation("CASH.SISTECREDITO_INVALID_ENVIRONMENT", "Ambiente desconocido: STAGING (pruebas) o PRODUCTION.");

    public static readonly Error Offline = Error.BusinessRule(
        "CASH.SISTECREDITO_OFFLINE", "Sistecrédito necesita internet: no hay conexión en este momento. Elija otro medio de pago o intente de nuevo.");

    public static readonly Error InvalidDocument = Error.Validation(
        "CASH.SISTECREDITO_INVALID_DOCUMENT", "Tipo de documento CC, CE, NIT, PA o TI y número de 3 a 20 letras o dígitos.");

    public static readonly Error InvalidAmount = Error.Validation(
        "CASH.SISTECREDITO_INVALID_AMOUNT", "El valor para Sistecrédito va en pesos enteros y mayor que cero.");

    public static readonly Error AmountAboveDue = Error.BusinessRule(
        "CASH.SISTECREDITO_AMOUNT_ABOVE_DUE", "El valor para Sistecrédito no puede ser mayor que lo que falta por pagar de la venta.");

    public static readonly Error SaleNotOpen = Error.BusinessRule("CASH.SISTECREDITO_SALE_NOT_OPEN", "La venta no está en curso en esta caja.");

    public static readonly Error AttemptNotFound = Error.NotFound("CASH.SISTECREDITO_NOT_FOUND", "El cobro de Sistecrédito no existe.");

    public static readonly Error NotPending = Error.BusinessRule("CASH.SISTECREDITO_NOT_PENDING", "El cobro de Sistecrédito ya terminó: no se puede cancelar.");

    public static readonly Error NotApproved = Error.BusinessRule(
        "CASH.SISTECREDITO_NOT_APPROVED", "El cobro de Sistecrédito no está aprobado: espere la aprobación del cliente o elija otro medio de pago.");

    public static readonly Error AmountMismatch = Error.BusinessRule(
        "CASH.SISTECREDITO_AMOUNT_MISMATCH", "El valor del pago con Sistecrédito debe ser exactamente el valor aprobado.");

    public static readonly Error NoLongerApproved = Error.BusinessRule(
        "CASH.SISTECREDITO_NO_LONGER_APPROVED",
        "Sistecrédito ya no reporta aprobado este cobro: no se puede usar. Elija otro medio de pago; la administración lo revisa en el portal de Sistecrédito.");

    public static readonly Error AlreadyApproved = Error.BusinessRule(
        "CASH.SISTECREDITO_ALREADY_APPROVED",
        "Esta venta ya tiene cobros aprobados con Sistecrédito sin usar: agréguelos como pago (vuelva a abrir el cobro) en lugar de generar otro.");

    public static readonly Error AlreadyUsed = Error.Conflict("CASH.SISTECREDITO_ALREADY_USED", "Ese cobro de Sistecrédito ya se usó en otra venta.");

    public static readonly Error OtherSale = Error.BusinessRule("CASH.SISTECREDITO_OTHER_SALE", "Ese cobro de Sistecrédito es de otra venta.");

    public static readonly Error NotUnderReview = Error.BusinessRule("CASH.SISTECREDITO_NOT_UNDER_REVIEW", "El cobro no está pendiente de revisión.");

    public static readonly Error ReviewNoteRequired = Error.Validation(
        "CASH.SISTECREDITO_REVIEW_NOTE_REQUIRED", "Escriba qué se hizo (de 5 a 300 caracteres), por ejemplo: \"Reversado en el portal de Sistecrédito\".");

    /// <summary>Error de Sistecrédito ya traducido (credenciales, valor mínimo, método no disponible…).</summary>
    public static Error Provider(string message) => Error.BusinessRule("CASH.SISTECREDITO_REJECTED", message);
}
