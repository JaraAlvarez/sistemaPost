using System.Text.RegularExpressions;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Catalog.Domain;

public enum TaxKind
{
    /// <summary>IVA (19 %, 5 %, exento, excluido).</summary>
    Vat,

    /// <summary>Impuesto nacional al consumo (INC) porcentual.</summary>
    Consumption,

    /// <summary>Impuesto al consumo de bolsas plásticas: valor fijo por bolsa, con vigencia anual.</summary>
    BagConsumption,

    /// <summary>Bebidas ultraprocesadas azucaradas (IBUA): valor por unidad según contenido y azúcar.</summary>
    SugaryDrinks,

    /// <summary>Comestibles ultraprocesados (ICUI): porcentaje.</summary>
    UltraProcessedFood,

    Other,
}

public enum TaxCalculation
{
    Percentage,
    FixedPerUnit,
}

/// <summary>
/// Impuesto: dato maestro de la empresa (no configuración, revisión §5). Su tarifa cambia por fecha en
/// <see cref="TaxRate"/> (D4-08). Exento = 0 % con derecho a descuento; excluido = no causa IVA.
/// </summary>
[Audited("catalog")]
public sealed partial class Tax : AggregateRoot<Guid>, ICompanyOwned, ISyncVersioned, IHasAuditLabel
{
    private Tax(Guid id, Guid companyId, string code, string name)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public TaxKind Kind { get; private set; }

    public TaxCalculation Calculation { get; private set; }

    public bool IsExempt { get; private set; }

    public bool IsExcluded { get; private set; }

    /// <summary>Código del tributo en la factura electrónica (se valida con Factus en la Fase 11-B).</summary>
    public string? DianCode { get; private set; }

    /// <summary>Sembrado por el sistema: no cambia de código, tipo ni cálculo.</summary>
    public bool IsSystem { get; private set; }

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public bool IsVat => Kind == TaxKind.Vat;

    public string AuditLabel => $"Impuesto {Code} · {Name}";

    public static Result<Tax> Create(
        Guid id, Guid companyId, string code, string name, TaxKind kind, TaxCalculation calculation, bool isExempt, bool isExcluded,
        string? dianCode, bool isSystem = false, MasterStatus status = MasterStatus.Active)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            return CatalogErrors.InvalidTaxCode;
        }

        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length is 0 or > 80)
        {
            return CatalogErrors.InvalidName;
        }

        if ((isExempt || isExcluded) && (isExempt == isExcluded || kind != TaxKind.Vat || calculation != TaxCalculation.Percentage))
        {
            return CatalogErrors.ZeroRatedMustBeVat;
        }

        return new Tax(id, companyId, normalized, trimmedName)
        {
            Kind = kind,
            Calculation = calculation,
            IsExempt = isExempt,
            IsExcluded = isExcluded,
            DianCode = string.IsNullOrWhiteSpace(dianCode) ? null : dianCode.Trim(),
            IsSystem = isSystem,
            Status = status,
        };
    }

    public Result Update(string name, string? dianCode)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length is 0 or > 80)
        {
            return CatalogErrors.InvalidName;
        }

        Name = trimmedName;
        DianCode = string.IsNullOrWhiteSpace(dianCode) ? null : dianCode.Trim();
        return Result.Success();
    }

    public void Activate() => Status = MasterStatus.Active;

    public void Deactivate() => Status = MasterStatus.Inactive;

    /// <summary>Valida el valor de una tarifa para este impuesto.</summary>
    public Result ValidateRate(decimal? rate, decimal? fixedAmount)
    {
        var valid = Calculation switch
        {
            TaxCalculation.Percentage => rate is >= 0 and <= 100 && fixedAmount is null && (!(IsExempt || IsExcluded) || rate == 0),
            _ => fixedAmount is >= 0 && rate is null,
        };
        return valid ? Result.Success() : CatalogErrors.InvalidTaxRate;
    }

    [GeneratedRegex("^[A-Z0-9_]{2,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>Tarifa de un impuesto con vigencia <c>[desde, hasta)</c> por fecha de negocio (sin solapamientos en la BD).</summary>
[Audited("catalog")]
public sealed class TaxRate : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private TaxRate(Guid id, Guid companyId, Guid taxId, DateOnly validFrom)
        : base(id)
    {
        CompanyId = companyId;
        TaxId = taxId;
        ValidFrom = validFrom;
    }

    public Guid CompanyId { get; private set; }

    public Guid TaxId { get; private set; }

    public decimal? Rate { get; private set; }

    public decimal? FixedAmount { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public string AuditLabel => $"Tarifa desde {ValidFrom:yyyy-MM-dd}";

    public static Result<TaxRate> Create(Guid id, Tax tax, decimal? rate, decimal? fixedAmount, DateOnly validFrom, DateOnly? validTo = null)
    {
        ArgumentNullException.ThrowIfNull(tax);
        var valid = tax.ValidateRate(rate, fixedAmount);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        return new TaxRate(id, tax.CompanyId, tax.Id, validFrom) { Rate = rate, FixedAmount = fixedAmount, ValidTo = validTo };
    }

    public bool IsValidOn(DateOnly date) => ValidFrom <= date && (ValidTo is null || date < ValidTo);

    public void CloseAt(DateOnly? validTo) => ValidTo = validTo;

    public void ChangeValue(decimal? rate, decimal? fixedAmount)
    {
        Rate = rate;
        FixedAmount = fixedAmount;
    }
}
