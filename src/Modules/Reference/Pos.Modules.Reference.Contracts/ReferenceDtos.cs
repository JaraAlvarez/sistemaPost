namespace Pos.Modules.Reference.Contracts;

public sealed record CountryDto(string Code, string Iso3, string Name, string? PhonePrefix);

public sealed record DepartmentDto(string Code, string Name);

public sealed record MunicipalityDto(string Code, string DepartmentCode, string Name, string Kind);

public sealed record IdentificationTypeDto(
    string Code, string Name, string FiscalCode, bool RequiresCheckDigit, bool AllowsNatural, bool AllowsLegal);

public sealed record CodeNameDto(string Code, string Name);
