using System.Globalization;
using Microsoft.Extensions.Logging;
using Pos.Modules.Billing.Application;
using Pos.Modules.Billing.Domain;

namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>
/// Adaptador de Factus API v2 (<c>Pos:Billing:Provider = FACTUS</c>) del proveedor fiscal neutro (Fase 11-B). Traduce los borradores con
/// <see cref="FactusDraftMapper"/>, llama a Factus con las credenciales de la empresa (un cliente por petición; el token solo en memoria,
/// separado por credenciales y ambiente) y traduce la respuesta al resultado neutro. NUNCA lanza (salvo cancelación del llamador):
/// <list type="bullet">
/// <item>Sin red o tiempo agotado → <see cref="FiscalOutcome.Unavailable"/> (contingencia).</item>
/// <item>429, 5xx, credenciales o token, o la DIAN aún sin responder → <see cref="FiscalOutcome.Failed"/> (con <c>RetryAfter</c> del 429).</item>
/// <item>Rechazo DIAN (FAK…) o validación (422) → <see cref="FiscalOutcome.Rejected"/> con los mensajes.</item>
/// <item><c>reference_code</c> ya validado (duplicado) → <see cref="FiscalOutcome.Accepted"/> con los datos existentes.</item>
/// </list>
/// Un documento rechazado queda en Factus y BLOQUEA los siguientes envíos: cuando el documento se reenvía (<c>Resubmission</c>, p. ej.
/// tras corregir el adquirente) se ELIMINA el no validado con el mismo <c>reference_code</c> y se envía de nuevo. Un bloqueo por OTRO
/// documento (rechazado y cancelado en el POS) no se toca: se informa como falla transitoria con el mensaje de Factus.
/// </summary>
public sealed partial class FactusFiscalProvider(IFactusApiFactory apis, FactusConnectionOptions connectionOptions, ILogger<FactusFiscalProvider> logger)
    : IFiscalProvider
{
    public const string ProviderName = "FACTUS";

    /// <summary>Zona horaria de las fechas de Factus (Colombia, sin horario de verano).</summary>
    private static readonly TimeSpan ColombiaOffset = TimeSpan.FromHours(-5);

    private static readonly string[] ValidatedAtFormats = ["dd-MM-yyyy hh:mm:ss tt", "dd-MM-yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];

    public string Name => ProviderName;

    public Task<FiscalProviderResult> SubmitInvoiceAsync(FiscalConnection connection, FiscalInvoiceDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return SubmitAsync(
            connection, FactusDocumentKind.Bill, draft.Header, () => FactusDraftMapper.ToBill(draft), (api, body, ct) => api.CreateBillAsync(body, ct),
            cancellationToken);
    }

    public Task<FiscalProviderResult> SubmitCreditNoteAsync(FiscalConnection connection, FiscalCreditNoteDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return SubmitAsync(
            connection, FactusDocumentKind.CreditNote, draft.Header, () => FactusDraftMapper.ToCreditNote(draft),
            (api, body, ct) => api.CreateCreditNoteAsync(body, ct), cancellationToken);
    }

    public Task<FiscalProviderResult> SubmitSupportDocumentAsync(FiscalConnection connection, FiscalSupportDocumentDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return SubmitAsync(
            connection, FactusDocumentKind.SupportDocument, draft.Header, () => FactusDraftMapper.ToSupportDocument(draft),
            (api, body, ct) => api.CreateSupportDocumentAsync(body, ct), cancellationToken);
    }

    public Task<FiscalProviderResult> SubmitAdjustmentNoteAsync(FiscalConnection connection, FiscalAdjustmentNoteDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return SubmitAsync(
            connection, FactusDocumentKind.AdjustmentNote, draft.Header, () => FactusDraftMapper.ToAdjustmentNote(draft),
            (api, body, ct) => api.CreateAdjustmentNoteAsync(body, ct), cancellationToken);
    }

    /// <summary>
    /// Estado por <c>reference_code</c>. Validado → aceptado. Registrado pero NO validado (pendiente o rechazado) → <see cref="FiscalOutcome.NotFound"/>:
    /// la cola lo envía de nuevo (Factus valida el pendiente al reenviarlo; el rechazado se elimina y se reenvía si es un reenvío).
    /// </summary>
    public async Task<FiscalProviderResult> GetStatusAsync(
        FiscalConnection connection, FiscalDocumentType documentType, string referenceCode, string? providerDocumentId, CancellationToken cancellationToken)
    {
        try
        {
            if (KindOf(documentType) is not { } kind)
            {
                return new FiscalProviderResult(FiscalOutcome.NotFound, Code: "UNSUPPORTED", Message: $"Factus no emite {documentType}.");
            }

            var api = Api(connection);
            var found = await api.FindByReferenceAsync(kind, referenceCode, cancellationToken);
            return found.Status switch
            {
                FactusQueryStatus.Found when found.Value!.IsValidated => Accepted(found.Value, prefix: null, "Documento ya validado en Factus (consulta)."),
                FactusQueryStatus.Found or FactusQueryStatus.NotFound => new FiscalProviderResult(
                    FiscalOutcome.NotFound, ProviderStatus: found.Value is null ? null : "NOT_VALIDATED", Code: "NOT_VALIDATED",
                    Message: found.Value is null ? "Factus no tiene el documento." : "El documento está en Factus sin validar: se reenvía."),
                _ => QueryFailure(found.Status, found.HttpStatus, found.RetryAfter, found.Detail),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unexpected(ex);
        }
    }

    public async Task<FiscalRangeSyncResult> GetNumberingRangesAsync(FiscalConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Api(connection).GetNumberingRangesAsync(onlyActive: false, documentCode: null, cancellationToken);
            if (result.IsFound)
            {
                return new FiscalRangeSyncResult(FiscalOutcome.Accepted, [.. result.Value!.Select(FactusDraftMapper.ToRange).OfType<FiscalProviderRange>()]);
            }

            var failure = QueryFailure(result.Status, result.HttpStatus, result.RetryAfter, result.Detail);
            return new FiscalRangeSyncResult(failure.Outcome, [], failure.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FiscalRangeSyncResult(FiscalOutcome.Failed, [], Unexpected(ex).Message);
        }
    }

    /// <summary>Opciones de Factus de la empresa: ambiente y credenciales de la conexión (descifradas solo para la llamada).</summary>
    public FactusOptions Options(FiscalConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new FactusOptions
        {
            Environment = connection.Environment == FiscalEnvironment.Production ? FactusEnvironment.Production : FactusEnvironment.Sandbox,
            BaseUrl = connectionOptions.BaseUrl,
            ClientId = connection.Credentials.ClientId,
            ClientSecret = connection.Credentials.ClientSecret,
            Username = connection.Credentials.Username,
            Password = connection.Credentials.Password,
            RequestTimeout = connectionOptions.RequestTimeout,
        };
    }

    // ─────────────────────────────── Envío ───────────────────────────────

    private async Task<FiscalProviderResult> SubmitAsync<TRequest>(
        FiscalConnection connection,
        FactusDocumentKind kind,
        FiscalHeader header,
        Func<TRequest> map,
        Func<IFactusApi, TRequest, CancellationToken, Task<FactusResult>> send,
        CancellationToken cancellationToken)
    {
        try
        {
            var api = Api(connection);
            var body = map();
            var result = await send(api, body, cancellationToken);

            // Reenvío de un documento que Factus tiene rechazado con este mismo reference_code: se elimina el no validado y se reenvía.
            if (header.Resubmission && result is { Outcome: FactusOutcome.Rejected, BlocksFurtherSubmissions: true, Document: { } rejected }
                && (rejected.ReferenceCode is null || rejected.ReferenceCode == header.ReferenceCode))
            {
                var deleted = await api.DeleteUnvalidatedAsync(kind, header.ReferenceCode, cancellationToken);
                if (deleted.Status is not (FactusQueryStatus.Found or FactusQueryStatus.NotFound))
                {
                    return QueryFailure(deleted.Status, deleted.HttpStatus, deleted.RetryAfter, $"No se pudo eliminar en Factus el documento rechazado: {deleted.Detail}");
                }

                LogDeletedAndResent(logger, kind, header.ReferenceCode);
                result = await send(api, body, cancellationToken);
            }

            return Translate(result, header.Numbering.Prefix);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unexpected(ex);
        }
    }

    private IFactusApi Api(FiscalConnection connection) => apis.Create(Options(connection));

    /// <summary>Resultado de Factus → resultado neutro.</summary>
    public static FiscalProviderResult Translate(FactusResult result, string? prefix)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            FactusOutcome.Accepted or FactusOutcome.Duplicate when result.Document is { IsValidated: true } document =>
                Accepted(document, prefix, result.Outcome == FactusOutcome.Duplicate ? "El reference_code ya estaba validado en Factus: se toman sus datos." : "Validado por la DIAN."),
            FactusOutcome.Pending or FactusOutcome.Accepted or FactusOutcome.Duplicate => new FiscalProviderResult(
                FiscalOutcome.Failed, ProviderDocumentId: result.Document?.Number, ProviderStatus: "PENDING_DIAN", Code: "PENDING_DIAN",
                Message: result.Detail ?? "Factus registró el documento pero la DIAN aún no lo valida: se reenvían los mismos datos."),
            FactusOutcome.Rejected when result.BlocksFurtherSubmissions && result.Document is null => new FiscalProviderResult(
                FiscalOutcome.Failed, ProviderStatus: "BLOCKED", Code: "BLOCKED",
                Message: $"{result.Detail} {Messages(result)}".Trim()),
            FactusOutcome.Rejected => new FiscalProviderResult(
                FiscalOutcome.Rejected, ProviderDocumentId: result.Document?.Number, ProviderStatus: "REJECTED",
                Code: result.Messages.FirstOrDefault(m => m.IsRejection)?.Code ?? (result.Messages.Count > 0 ? result.Messages[0].Code : null) ?? result.HttpStatus?.ToString(CultureInfo.InvariantCulture),
                Message: RejectionText(result)),
            FactusOutcome.TransientError when result.HttpStatus is null => new FiscalProviderResult(
                FiscalOutcome.Unavailable, Code: "NETWORK", Message: result.Detail ?? "Sin conexión con Factus."),
            FactusOutcome.TransientError => new FiscalProviderResult(
                FiscalOutcome.Failed, Code: result.HttpStatus.Value.ToString(CultureInfo.InvariantCulture), Message: result.Detail, RetryAfter: result.RetryAfter),
            _ => new FiscalProviderResult(
                FiscalOutcome.Failed, Code: "CREDENTIALS", Message: result.Detail ?? "Factus rechazó las credenciales o la cuenta no tiene documentos disponibles."),
        };
    }

    private static FiscalProviderResult Accepted(FactusDocument document, string? prefix, string message) => new(
        FiscalOutcome.Accepted,
        ProviderDocumentId: document.Number,
        ProviderStatus: "VALIDATED",
        FiscalNumber: document.Number,
        Consecutive: ConsecutiveOf(document.Number, prefix),
        Cufe: document.Cufe,
        QrData: document.QrUrl,
        PdfUrl: document.PublicUrl,
        Code: "200",
        Message: document.DianMessages.Count > 0 ? $"{message} Notificaciones DIAN: {string.Join("; ", document.DianMessages)}" : message,
        ValidatedAt: ParseValidatedAt(document.ValidatedAt));

    private static FiscalProviderResult QueryFailure(FactusQueryStatus status, int? httpStatus, TimeSpan? retryAfter, string? detail) => status switch
    {
        FactusQueryStatus.TransientError when httpStatus is null => new FiscalProviderResult(FiscalOutcome.Unavailable, Code: "NETWORK", Message: detail ?? "Sin conexión con Factus."),
        FactusQueryStatus.TransientError => new FiscalProviderResult(
            FiscalOutcome.Failed, Code: httpStatus.Value.ToString(CultureInfo.InvariantCulture), Message: detail, RetryAfter: retryAfter),
        FactusQueryStatus.CredentialsError => new FiscalProviderResult(FiscalOutcome.Failed, Code: "CREDENTIALS", Message: detail ?? "Credenciales de Factus inválidas."),
        _ => new FiscalProviderResult(FiscalOutcome.Failed, Code: httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "FAILED", Message: detail),
    };

    private static FiscalProviderResult Unexpected(Exception exception) =>
        new(FiscalOutcome.Failed, Code: "EXCEPTION", Message: $"Falla inesperada del adaptador de Factus: {exception.GetType().Name}.");

    private static string RejectionText(FactusResult result)
    {
        var rejections = result.Messages.Where(m => m.IsRejection).ToList();
        var messages = rejections.Count > 0 ? rejections : result.Messages.ToList();
        var text = messages.Count > 0 ? string.Join("; ", messages) : result.Detail;
        return string.IsNullOrWhiteSpace(text) ? "Factus rechazó el documento (sin detalle)." : text;
    }

    private static string Messages(FactusResult result) => string.Join("; ", result.Messages);

    /// <summary>Consecutivo del número fiscal ("SETP990000001" con prefijo "SETP" → 990000001).</summary>
    public static long? ConsecutiveOf(string? number, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(number) || prefix is null || !number.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return long.TryParse(number.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var consecutive) ? consecutive : null;
    }

    /// <summary>Fecha de validación de Factus (<c>DD-MM-YYYY hh:mm:ss AM/PM</c>, hora de Colombia).</summary>
    public static DateTimeOffset? ParseValidatedAt(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && DateTime.TryParseExact(text.Trim(), ValidatedAtFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), ColombiaOffset).ToUniversalTime()
            : null;

    private static FactusDocumentKind? KindOf(FiscalDocumentType type) => type switch
    {
        FiscalDocumentType.InvoiceElectronic => FactusDocumentKind.Bill,
        FiscalDocumentType.CreditNote => FactusDocumentKind.CreditNote,
        FiscalDocumentType.SupportDocument => FactusDocumentKind.SupportDocument,
        FiscalDocumentType.AdjustmentNote => FactusDocumentKind.AdjustmentNote,
        _ => null,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Factus: {Kind} {ReferenceCode} estaba rechazado; se eliminó el no validado y se reenvió corregido.")]
    private static partial void LogDeletedAndResent(ILogger logger, FactusDocumentKind kind, string referenceCode);
}
