using System.Globalization;
using System.Text.RegularExpressions;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Catalog.Domain;

public enum BarcodeType
{
    Ean13,
    Ean8,
    Upca,
    Code128,

    /// <summary>Código interno generado por el sistema (EAN-13 con prefijo 29) para productos sin código de fábrica.</summary>
    Internal,
}

/// <summary>Código tal como se registró y su forma normalizada (la que se busca al escanear).</summary>
public sealed record NormalizedBarcode(string Code, string NormalizedCode, BarcodeType Type);

/// <summary>
/// Códigos GTIN (EAN-13, EAN-8, UPC-A) con su dígito de control, normalización y códigos internos (D4-06, D4-07).
/// UPC-A (12 dígitos) se normaliza a EAN-13 anteponiendo 0: el escáner puede enviar cualquiera de las dos formas.
/// </summary>
public static partial class Barcodes
{
    /// <summary>Prefijo reservado a los códigos internos (las reglas de báscula usan 20–28).</summary>
    public const string InternalPrefix = "29";

    public const int MaxInternalSequence = 9_999_999;

    public static Result<NormalizedBarcode> Normalize(string? code, BarcodeType? declaredType = null)
    {
        var text = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!FreeFormPattern().IsMatch(text))
        {
            return CatalogErrors.InvalidBarcode;
        }

        var digits = text.All(char.IsAsciiDigit);
        var type = declaredType ?? (digits ? text.Length switch
        {
            13 => BarcodeType.Ean13,
            8 => BarcodeType.Ean8,
            12 => BarcodeType.Upca,
            _ => BarcodeType.Code128,
        } : BarcodeType.Code128);

        var expectedLength = type switch
        {
            BarcodeType.Ean13 or BarcodeType.Internal => 13,
            BarcodeType.Ean8 => 8,
            BarcodeType.Upca => 12,
            _ => 0,
        };

        if (expectedLength > 0)
        {
            if (!digits || text.Length != expectedLength)
            {
                return CatalogErrors.InvalidBarcode;
            }

            if (!HasValidCheckDigit(text))
            {
                return CatalogErrors.InvalidCheckDigit;
            }
        }

        if (type == BarcodeType.Internal && !text.StartsWith(InternalPrefix, StringComparison.Ordinal))
        {
            return CatalogErrors.InvalidBarcode;
        }

        return new NormalizedBarcode(text, type == BarcodeType.Upca ? "0" + text : text, type);
    }

    /// <summary>Forma normalizada de un texto escaneado (para buscar): UPC-A → EAN-13; lo demás en mayúsculas.</summary>
    public static string NormalizeForLookup(string scanned)
    {
        var text = (scanned ?? string.Empty).Trim().ToUpperInvariant();
        return text.Length == 12 && text.All(char.IsAsciiDigit) ? "0" + text : text;
    }

    /// <summary>Dígito de control GTIN (módulo 10, pesos 3 y 1 desde la derecha).</summary>
    public static char ComputeCheckDigit(string digitsWithoutCheck)
    {
        ArgumentException.ThrowIfNullOrEmpty(digitsWithoutCheck);
        var sum = 0;
        for (var i = 0; i < digitsWithoutCheck.Length; i++)
        {
            var digit = digitsWithoutCheck[digitsWithoutCheck.Length - 1 - i] - '0';
            if (digit is < 0 or > 9)
            {
                throw new ArgumentException("Solo dígitos.", nameof(digitsWithoutCheck));
            }

            sum += digit * (i % 2 == 0 ? 3 : 1);
        }

        return (char)('0' + ((10 - (sum % 10)) % 10));
    }

    public static bool HasValidCheckDigit(string gtin) =>
        gtin is { Length: >= 2 } && gtin.All(char.IsAsciiDigit) && ComputeCheckDigit(gtin[..^1]) == gtin[^1];

    /// <summary>EAN-13 interno: 29 + número de nodo (3) + consecutivo del nodo (7) + dígito de control.</summary>
    public static string Internal(short nodeNumber, long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeNumber, (short)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nodeNumber, (short)999);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1L);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, MaxInternalSequence);
        var body = string.Create(CultureInfo.InvariantCulture, $"{InternalPrefix}{nodeNumber:D3}{sequence:D7}");
        return body + ComputeCheckDigit(body);
    }

    /// <summary>SKU interno: número de nodo + consecutivo del nodo, p. ej. <c>001-000123</c>.</summary>
    public static string InternalSku(short nodeNumber, long sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"{nodeNumber:D3}-{sequence:D6}");

    [GeneratedRegex("^[A-Z0-9._/-]{1,50}$", RegexOptions.CultureInvariant)]
    private static partial Regex FreeFormPattern();
}

public enum VariableBarcodeContent
{
    /// <summary>La etiqueta trae el peso (la báscula calcula el precio con el precio por kilo).</summary>
    Weight,

    /// <summary>La etiqueta trae el precio a cobrar.</summary>
    Price,
}

public enum MasterStatus
{
    Active,
    Inactive,
}

/// <summary>Lectura de una etiqueta de báscula: PLU (sin ceros a la izquierda) y peso en kg o precio.</summary>
public sealed record VariableBarcodeReading(string Plu, VariableBarcodeContent Content, decimal Value);

/// <summary>
/// Regla de etiqueta de báscula (D4-12): EAN-13 que empieza por un prefijo 20–28 y lleva el PLU y el peso o el precio en
/// posiciones fijas (1-based). Ejemplo "20 PPPPP WWWWW C": prefijo 20, PLU en 3–7, peso en gramos en 8–12 (3 decimales).
/// </summary>
[Audited("catalog")]
public sealed class VariableBarcodeRule : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private static readonly decimal[] Powers = [1m, 10m, 100m, 1000m];

    private VariableBarcodeRule(Guid id, Guid companyId, string prefix)
        : base(id)
    {
        CompanyId = companyId;
        Prefix = prefix;
    }

    public Guid CompanyId { get; private set; }

    public string Prefix { get; private set; }

    public VariableBarcodeContent Content { get; private set; }

    public short PluStart { get; private set; }

    public short PluLength { get; private set; }

    public short ValueStart { get; private set; }

    public short ValueLength { get; private set; }

    public short ValueDecimals { get; private set; }

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public string AuditLabel => $"Regla de báscula {Prefix} ({Content})";

    public static Result<VariableBarcodeRule> Create(
        Guid id, Guid companyId, string prefix, VariableBarcodeContent content, short pluStart, short pluLength, short valueStart,
        short valueLength, short valueDecimals)
    {
        var rule = new VariableBarcodeRule(id, companyId, (prefix ?? string.Empty).Trim());
        var result = rule.Configure(content, pluStart, pluLength, valueStart, valueLength, valueDecimals);
        return result.IsSuccess ? rule : result.Error;
    }

    public Result Configure(VariableBarcodeContent content, short pluStart, short pluLength, short valueStart, short valueLength, short valueDecimals)
    {
        if (Prefix == Barcodes.InternalPrefix)
        {
            return CatalogErrors.PrefixReservedForInternalCodes;
        }

        var valid = Prefix.Length == 2 && Prefix[0] == '2' && Prefix[1] is >= '0' and <= '8'
            && pluStart >= 3 && pluLength is >= 1 and <= 6 && pluStart + pluLength - 1 <= 12
            && valueStart >= 3 && valueLength is >= 1 and <= 7 && valueStart + valueLength - 1 <= 12
            && (pluStart + pluLength <= valueStart || valueStart + valueLength <= pluStart)
            && valueDecimals is >= 0 and <= 3 && valueDecimals < valueLength;
        if (!valid)
        {
            return CatalogErrors.InvalidBarcodeRule;
        }

        Content = content;
        PluStart = pluStart;
        PluLength = pluLength;
        ValueStart = valueStart;
        ValueLength = valueLength;
        ValueDecimals = valueDecimals;
        return Result.Success();
    }

    public void Deactivate() => Status = MasterStatus.Inactive;

    public void Activate() => Status = MasterStatus.Active;

    /// <summary>Interpreta el código si corresponde a esta regla (13 dígitos, prefijo y dígito de control válidos).</summary>
    public VariableBarcodeReading? Read(string code)
    {
        if (Status != MasterStatus.Active || code is not { Length: 13 } || !code.StartsWith(Prefix, StringComparison.Ordinal)
            || !Barcodes.HasValidCheckDigit(code))
        {
            return null;
        }

        var plu = code.Substring(PluStart - 1, PluLength).TrimStart('0');
        var raw = decimal.Parse(code.AsSpan(ValueStart - 1, ValueLength), NumberStyles.None, CultureInfo.InvariantCulture);
        var value = raw / Powers[ValueDecimals];
        return new VariableBarcodeReading(plu.Length == 0 ? "0" : plu, Content, value);
    }
}
