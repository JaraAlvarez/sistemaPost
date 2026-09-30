using System.Security.Cryptography;
using System.Text;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Customers.Domain;

public enum ConsentPurpose
{
    /// <summary>Historial de compras y atención al cliente.</summary>
    Service,

    Marketing,

    /// <summary>Programa de puntos (8-B).</summary>
    Loyalty,
}

/// <summary>Cómo se obtuvo la autorización (prueba ante la SIC, D8-06).</summary>
public enum ConsentChannel
{
    /// <summary>Verbal en la caja: queda la prueba (usuario, caja, hora, versión de la política) y el aviso en el tiquete.</summary>
    PosVerbal,

    PosSigned,
    PaperForm,
    Web,
    Phone,
    Email,
    Import,
}

public enum PolicyStatus
{
    /// <summary>Plantilla sembrada: el propietario debe revisarla y activarla.</summary>
    PendingReview,

    Active,
    Retired,
}

public enum DataRequestType
{
    Query,
    Update,
    Revoke,
    Delete,
    Complaint,
}

public enum DataRequestStatus
{
    Open,
    Resolved,
    Rejected,
}

/// <summary>Política de tratamiento de datos con versión, texto completo, aviso corto (el que se lee en la caja) y su huella.</summary>
[Audited("customers")]
public sealed class PrivacyPolicy : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private PrivacyPolicy(Guid id, Guid companyId, int version)
        : base(id)
    {
        CompanyId = companyId;
        Version = version;
    }

    public Guid CompanyId { get; private set; }

    public int Version { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public string ShortNotice { get; private set; } = string.Empty;

    public string TextHash { get; private set; } = string.Empty;

    public PolicyStatus Status { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public Guid? ActivatedBy { get; private set; }

    public string AuditLabel => $"Política de datos v{Version}";

    public static Result<PrivacyPolicy> Create(Guid id, Guid companyId, int version, string text, string shortNotice)
    {
        var body = (text ?? string.Empty).Trim();
        var notice = (shortNotice ?? string.Empty).Trim();
        if (body.Length is < 200 or > 50_000 || notice.Length is < 20 or > 600 || version < 1)
        {
            return CustomerErrors.InvalidPolicy;
        }

        return new PrivacyPolicy(id, companyId, version)
        {
            Text = body,
            ShortNotice = notice,
            TextHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body + "\n" + notice))),
            Status = PolicyStatus.PendingReview,
        };
    }

    public Result Activate(Guid userId, DateTimeOffset now)
    {
        if (Status != PolicyStatus.PendingReview)
        {
            return Error.BusinessRule("CUSTOMERS.POLICY_NOT_PENDING", "Solo se activa una versión pendiente de revisión.");
        }

        Status = PolicyStatus.Active;
        ActivatedAt = now;
        ActivatedBy = userId;
        return Result.Success();
    }

    public void Retire() => Status = PolicyStatus.Retired;
}

/// <summary>Datos de una autorización nueva.</summary>
public sealed record ConsentInput(ConsentPurpose Purpose, bool Granted, ConsentChannel Channel, IReadOnlyList<string>? MarketingChannels, string? Evidence);

/// <summary>Dónde y quién la registró.</summary>
public sealed record ConsentContext(Guid UserId, Guid? BranchId, Guid? PosTerminalId, Guid NodeId, DateTimeOffset Now);

/// <summary>
/// Autorización de tratamiento de datos (Ley 1581 de 2012, Decreto 1377 de 2013): registro de SOLO INSERCIÓN por finalidad con
/// la versión de la política, el canal, la evidencia, quién, dónde y cuándo (D8-06). Revocar es otro registro con
/// <c>Granted = false</c>. El marketing nunca viene autorizado por defecto (RN-DAT-01).
/// </summary>
public sealed class CustomerConsent : Entity<Guid>, ICompanyOwned
{
    public static readonly IReadOnlySet<string> AllowedChannels = new HashSet<string>(StringComparer.Ordinal) { "EMAIL", "SMS", "WHATSAPP", "CALL" };

    private CustomerConsent(Guid id, Guid companyId, Guid partyId)
        : base(id)
    {
        CompanyId = companyId;
        PartyId = partyId;
    }

    public Guid CompanyId { get; private set; }

    public Guid PartyId { get; private set; }

    public ConsentPurpose Purpose { get; private set; }

    public bool Granted { get; private set; }

    public ConsentChannel Channel { get; private set; }

    public string? MarketingChannels { get; private set; }

    public Guid PolicyId { get; private set; }

    public int PolicyVersion { get; private set; }

    public string? Evidence { get; private set; }

    public Guid UserId { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid? PosTerminalId { get; private set; }

    public Guid NodeId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static Result<CustomerConsent> Create(Guid id, Guid companyId, Guid partyId, ConsentInput input, PrivacyPolicy policy, ConsentContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        var channels = (input.MarketingChannels ?? []).Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        var evidence = string.IsNullOrWhiteSpace(input.Evidence) ? null : input.Evidence.Trim();
        if (!Enum.IsDefined(input.Purpose) || !Enum.IsDefined(input.Channel) || policy.Status == PolicyStatus.Retired
            || channels.Any(c => !AllowedChannels.Contains(c)) || (channels.Count > 0 && !(input.Purpose == ConsentPurpose.Marketing && input.Granted))
            || evidence is { Length: > 200 })
        {
            return CustomerErrors.InvalidConsent;
        }

        return new CustomerConsent(id, companyId, partyId)
        {
            Purpose = input.Purpose,
            Granted = input.Granted,
            Channel = input.Channel,
            MarketingChannels = channels.Count == 0 ? null : string.Join(';', channels),
            PolicyId = policy.Id,
            PolicyVersion = policy.Version,
            Evidence = evidence,
            UserId = context.UserId,
            BranchId = context.BranchId,
            PosTerminalId = context.PosTerminalId,
            NodeId = context.NodeId,
            OccurredAt = context.Now,
        };
    }
}

/// <summary>
/// Solicitud del titular (Ley 1581, arts. 14 y 15): consultas en 10 días hábiles, reclamos en 15 (RN-DAT-03). Queda registrada
/// con su vencimiento, respuesta, quién y cuándo.
/// </summary>
[Audited("customers")]
public sealed class DataRequest : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private DataRequest(Guid id, Guid companyId, Guid partyId)
        : base(id)
    {
        CompanyId = companyId;
        PartyId = partyId;
    }

    public Guid CompanyId { get; private set; }

    public Guid PartyId { get; private set; }

    public DataRequestType Type { get; private set; }

    public ConsentChannel Channel { get; private set; }

    [PersonalData(PersonalDataKind.FreeText)]
    public string Detail { get; private set; } = string.Empty;

    public DateOnly ReceivedOn { get; private set; }

    public DateOnly DueOn { get; private set; }

    public DataRequestStatus Status { get; private set; } = DataRequestStatus.Open;

    public string? Response { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public string AuditLabel => $"Solicitud {Type} del titular {PartyId}";

    /// <summary>Días hábiles del plazo legal: consultas 10, reclamos (rectificar, revocar, suprimir, quejas) 15.</summary>
    public static int TermDays(DataRequestType type) => type == DataRequestType.Query ? 10 : 15;

    public static Result<DataRequest> Create(Guid id, Guid companyId, Guid partyId, DataRequestType type, ConsentChannel channel, string detail, DateOnly receivedOn)
    {
        var trimmed = (detail ?? string.Empty).Trim();
        if (!Enum.IsDefined(type) || !Enum.IsDefined(channel) || trimmed.Length is < 5 or > 1_000)
        {
            return CustomerErrors.InvalidRequest;
        }

        return new DataRequest(id, companyId, partyId)
        {
            Type = type,
            Channel = channel,
            Detail = trimmed,
            ReceivedOn = receivedOn,
            DueOn = ColombianCalendar.AddBusinessDays(receivedOn, TermDays(type)),
        };
    }

    public Result Close(bool resolved, string response, Guid userId, DateTimeOffset now)
    {
        if (Status != DataRequestStatus.Open)
        {
            return CustomerErrors.RequestClosed;
        }

        var trimmed = (response ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 2_000)
        {
            return CustomerErrors.ReasonRequired;
        }

        Status = resolved ? DataRequestStatus.Resolved : DataRequestStatus.Rejected;
        Response = trimmed;
        ResolvedBy = userId;
        ResolvedAt = now;
        return Result.Success();
    }

    public bool IsOverdue(DateOnly today) => Status == DataRequestStatus.Open && today > DueOn;
}

/// <summary>
/// Calendario de Colombia para los plazos en días hábiles: sábados, domingos y festivos (Ley 51 de 1983, "Ley Emiliani": los
/// festivos que no caen en lunes se trasladan al lunes siguiente; los de Semana Santa dependen de la Pascua). Se calcula, no se
/// mantiene en una tabla.
/// </summary>
public static class ColombianCalendar
{
    public static bool IsBusinessDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !Holidays(date.Year).Contains(date);

    /// <summary>Fecha límite: <paramref name="days"/> días hábiles después de <paramref name="from"/> (sin contarlo).</summary>
    public static DateOnly AddBusinessDays(DateOnly from, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        var date = from;
        while (days > 0)
        {
            date = date.AddDays(1);
            if (IsBusinessDay(date))
            {
                days--;
            }
        }

        return date;
    }

    public static IReadOnlySet<DateOnly> Holidays(int year)
    {
        var easter = EasterSunday(year);
        return new HashSet<DateOnly>
        {
            new(year, 1, 1),
            NextMonday(new DateOnly(year, 1, 6)),
            NextMonday(new DateOnly(year, 3, 19)),
            easter.AddDays(-3),
            easter.AddDays(-2),
            new(year, 5, 1),
            NextMonday(easter.AddDays(39)),
            NextMonday(easter.AddDays(60)),
            NextMonday(easter.AddDays(68)),
            NextMonday(new DateOnly(year, 6, 29)),
            new(year, 7, 20),
            new(year, 8, 7),
            NextMonday(new DateOnly(year, 8, 15)),
            NextMonday(new DateOnly(year, 10, 12)),
            NextMonday(new DateOnly(year, 11, 1)),
            NextMonday(new DateOnly(year, 11, 11)),
            new(year, 12, 8),
            new(year, 12, 25),
        };
    }

    /// <summary>Domingo de Pascua (algoritmo de Meeus/Jones/Butcher, calendario gregoriano).</summary>
    public static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    private static DateOnly NextMonday(DateOnly date) => date.AddDays(((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7);
}
