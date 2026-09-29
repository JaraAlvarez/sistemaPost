using System.Diagnostics.CodeAnalysis;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Fiscal;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Licensing.Domain;

/// <summary>NIT colombiano con su dígito de verificación (validado con <see cref="Nit"/>). Texto: <c>900123456-8</c>.</summary>
public sealed record NitNumber(string Number, string CheckDigit)
{
    /// <summary>Acepta <c>900.123.456-8</c>, <c>900123456-8</c> o <c>900123456 - 8</c>; el DV es obligatorio y debe coincidir.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out NitNumber? nit)
    {
        nit = null;
        var parts = (text ?? string.Empty).Replace(".", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Split('-');
        return parts is [var number, var dv] && TryCreate(number, dv, out nit);
    }

    public static bool TryCreate(string? number, string? checkDigit, [NotNullWhen(true)] out NitNumber? nit)
    {
        nit = null;
        var n = number?.Trim() ?? string.Empty;
        var dv = checkDigit?.Trim() ?? string.Empty;
        if (!Nit.IsValid(n, dv))
        {
            return false;
        }

        nit = new NitNumber(n, dv);
        return true;
    }

    public override string ToString() => $"{Number}-{CheckDigit}";
}

/// <summary>Cliente comercial o distribuidor (L-03). Es dueño de una o varias empresas licenciadas.</summary>
[Audited("licensing")]
public sealed class Account : AggregateRoot<Guid>, IHasAuditLabel
{
    private Account(Guid id, string name, AccountKind kind)
        : base(id)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; private set; }

    public AccountKind Kind { get; private set; }

    public string? Nit { get; private set; }

    public string? NitCheckDigit { get; private set; }

    public string? ContactName { get; private set; }

    public string? ContactEmail { get; private set; }

    public string? ContactPhone { get; private set; }

    public Guid? ParentAccountId { get; private set; }

    public RecordStatus Status { get; private set; } = RecordStatus.Active;

    public string? Notes { get; private set; }

    public string AuditLabel => $"Cuenta {Name}";

    public static Result<Account> Create(Guid id, AccountKind kind, AccountData data, Account? parent)
    {
        var account = new Account(id, string.Empty, kind);
        var updated = account.Update(data, parent, isActive: true);
        return updated.IsSuccess ? account : updated.Error;
    }

    public Result Update(AccountData data, Account? parent, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(data);
        var name = (data.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 200 || Longer(data.ContactName, 120) || Longer(data.ContactEmail, 120) || Longer(data.ContactPhone, 30)
            || Longer(data.Notes, 500))
        {
            return LicensingErrors.InvalidAccount;
        }

        NitNumber? nit = null;
        if (!string.IsNullOrWhiteSpace(data.Nit) && !NitNumber.TryCreate(data.Nit, data.NitCheckDigit, out nit))
        {
            return LicensingErrors.NitInvalid;
        }

        if (parent is not null && (parent.Id == Id || parent.Kind != AccountKind.Reseller))
        {
            return LicensingErrors.InvalidParentAccount;
        }

        Name = name;
        Nit = nit?.Number;
        NitCheckDigit = nit?.CheckDigit;
        ContactName = Clean(data.ContactName);
        ContactEmail = Clean(data.ContactEmail);
        ContactPhone = Clean(data.ContactPhone);
        Notes = Clean(data.Notes);
        ParentAccountId = parent?.Id;
        Status = isActive ? RecordStatus.Active : RecordStatus.Inactive;
        return Result.Success();
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static bool Longer(string? value, int max) => value is not null && value.Trim().Length > max;
}

public sealed record AccountData(
    string Name, string? Nit, string? NitCheckDigit, string? ContactName, string? ContactEmail, string? ContactPhone, string? Notes);

/// <summary>Empresa licenciada (la del POS): la licencia es de la razón social, identificada por su NIT único (resolución 6).</summary>
[Audited("licensing")]
public sealed class Organization : AggregateRoot<Guid>, IHasAuditLabel
{
    private Organization(Guid id, Guid accountId, string legalName, string nit, string nitCheckDigit)
        : base(id)
    {
        AccountId = accountId;
        LegalName = legalName;
        Nit = nit;
        NitCheckDigit = nitCheckDigit;
    }

    public Guid AccountId { get; private set; }

    public string LegalName { get; private set; }

    /// <summary>NIT sin DV. Inmutable: el token del POS se liga a él.</summary>
    public string Nit { get; private set; }

    public string NitCheckDigit { get; private set; }

    public string? City { get; private set; }

    public RecordStatus Status { get; private set; } = RecordStatus.Active;

    public NitNumber NitNumber => new(Nit, NitCheckDigit);

    public string AuditLabel => $"Empresa {LegalName} · NIT {Nit}-{NitCheckDigit}";

    public static Result<Organization> Create(Guid id, Account account, string legalName, string nit, string checkDigit, string? city)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!NitNumber.TryCreate(nit, checkDigit, out var parsed))
        {
            return LicensingErrors.NitInvalid;
        }

        var organization = new Organization(id, account.Id, string.Empty, parsed.Number, parsed.CheckDigit);
        var updated = organization.Update(legalName, city, isActive: true);
        return updated.IsSuccess ? organization : updated.Error;
    }

    public Result Update(string legalName, string? city, bool isActive)
    {
        var name = (legalName ?? string.Empty).Trim();
        if (name.Length is 0 or > 200 || Account.Longer(city, 120))
        {
            return LicensingErrors.InvalidOrganization;
        }

        LegalName = name;
        City = Account.Clean(city);
        Status = isActive ? RecordStatus.Active : RecordStatus.Inactive;
        return Result.Success();
    }

    /// <summary>¿El NIT que declara el POS es el de esta empresa?</summary>
    public bool HasNit(NitNumber nit)
    {
        ArgumentNullException.ThrowIfNull(nit);
        return string.Equals(nit.Number, Nit, StringComparison.Ordinal) && string.Equals(nit.CheckDigit, NitCheckDigit, StringComparison.Ordinal);
    }
}
