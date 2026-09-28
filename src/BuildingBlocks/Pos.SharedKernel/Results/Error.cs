using System.Text.RegularExpressions;

namespace Pos.SharedKernel.Results;

/// <summary>Categoría del error; determina el código HTTP en la API.</summary>
public enum ErrorType
{
    None = 0,
    Validation,
    NotFound,
    Conflict,
    BusinessRule,
    Unauthorized,
    Forbidden,
    Unexpected,
}

/// <summary>Error de un campo concreto (validación de datos de entrada).</summary>
public sealed record FieldError(string Field, string Code, string Message);

/// <summary>
/// Error con código estable (<c>MODULO.DESCRIPCION</c>, p. ej. <c>SALES.INSUFFICIENT_STOCK</c>).
/// El código es un contrato: la UI, las pruebas y la documentación dependen de él; el mensaje puede cambiar.
/// </summary>
public sealed partial record Error
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);

    private Error(string code, string message, ErrorType type, IReadOnlyList<FieldError>? fieldErrors = null)
    {
        Code = code;
        Message = message;
        Type = type;
        FieldErrors = fieldErrors ?? [];
    }

    public string Code { get; }

    public string Message { get; }

    public ErrorType Type { get; }

    public IReadOnlyList<FieldError> FieldErrors { get; }

    public static Error Validation(string code, string message, IReadOnlyList<FieldError>? fieldErrors = null) =>
        Create(code, message, ErrorType.Validation, fieldErrors);

    public static Error NotFound(string code, string message) => Create(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => Create(code, message, ErrorType.Conflict);

    public static Error BusinessRule(string code, string message) => Create(code, message, ErrorType.BusinessRule);

    public static Error Unauthorized(string code, string message) => Create(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => Create(code, message, ErrorType.Forbidden);

    public static Error Unexpected(string code, string message) => Create(code, message, ErrorType.Unexpected);

    public static bool IsValidCode(string code) => !string.IsNullOrEmpty(code) && CodePattern().IsMatch(code);

    private static Error Create(string code, string message, ErrorType type, IReadOnlyList<FieldError>? fieldErrors = null)
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException(
                $"Código de error inválido '{code}'. Formato esperado: MODULO.DESCRIPCION en mayúsculas.", nameof(code));
        }

        Guard.NotNullOrWhiteSpace(message);
        return new Error(code, message, type, fieldErrors);
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]*(\\.[A-Z][A-Z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
