using System.Text.RegularExpressions;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Identity.Domain;

/// <summary>Formatos y políticas de credenciales (RN-SEC-01 y reglas del PIN, docs/fases/fase-03-propuesta.md §5).</summary>
public static partial class IdentityRules
{
    /// <summary>Contraseñas frecuentes que se rechazan aunque cumplan la longitud (lista corta incrustada).</summary>
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "12345678", "123456789", "1234567890", "12345678910", "87654321", "11111111", "00000000", "88888888", "99999999",
        "password", "password1", "password123", "passw0rd", "contraseña", "contrasena", "contraseña1", "contrasena1",
        "qwerty123", "qwertyuiop", "asdfghjkl", "zxcvbnm1", "iloveyou", "teamo123", "colombia", "colombia1", "colombia123",
        "bogota123", "medellin1", "administrador", "admin123", "admin1234", "administrator", "supermercado", "supermercado1",
        "cajero123", "caja1234", "tienda123", "abcd1234", "abc12345", "a1b2c3d4", "1q2w3e4r", "1qaz2wsx", "welcome1",
        "bienvenido", "bienvenido1", "sunshine", "princess", "football", "futbol123", "millonarios", "nacional1",
        "america1", "12341234", "123123123", "123qweasd", "q1w2e3r4", "letmein1", "monkey123", "dragon123",
    };

    public static string NormalizeUsername(string username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidUsername(string username) => UsernamePattern().IsMatch(username);

    public static bool IsValidRoleCode(string code) => RoleCodePattern().IsMatch(code ?? string.Empty);

    public static bool IsValidPosCode(string code) => PosCodePattern().IsMatch(code ?? string.Empty);

    /// <summary>Error de la política de contraseñas, o <see cref="Error.None"/> si es aceptable.</summary>
    public static Error CheckPassword(string password, string username, int minLength)
    {
        if (string.IsNullOrEmpty(password) || password.Length < minLength)
        {
            return Error.Validation("IDENTITY.PASSWORD_TOO_SHORT", $"La contraseña debe tener al menos {minLength} caracteres.");
        }

        if (password.Length > 128)
        {
            return Error.Validation("IDENTITY.PASSWORD_TOO_LONG", "La contraseña admite hasta 128 caracteres.");
        }

        if (string.Equals(password, username, StringComparison.OrdinalIgnoreCase)
            || password.Contains(username, StringComparison.OrdinalIgnoreCase) && username.Length >= 4)
        {
            return Error.Validation("IDENTITY.PASSWORD_CONTAINS_USERNAME", "La contraseña no puede contener el nombre de usuario.");
        }

        if (CommonPasswords.Contains(password) || password.Distinct().Count() <= 2)
        {
            return Error.Validation("IDENTITY.PASSWORD_TOO_COMMON", "La contraseña es demasiado común o predecible.");
        }

        return Error.None;
    }

    /// <summary>
    /// PINs muy usados que se rechazan aunque no sean secuencias ni repeticiones (lista corta incrustada, PU7): pares alternos, la columna
    /// central del teclado, años y fechas típicas.
    /// </summary>
    private static readonly HashSet<string> CommonPins = new(StringComparer.Ordinal)
    {
        "1212", "2121", "1122", "2211", "1313", "1010", "2020", "6969", "2580", "0852", "1004", "1001", "2000", "2001", "1999", "1990",
        "1369", "1478", "3698", "1590", "7531", "1357", "2468", "0007", "5683",
        "121212", "112233", "123123", "147258", "159753", "258036", "101010", "111222", "123321", "696969", "007007",
    };

    /// <summary>
    /// PIN numérico de la longitud configurada, sin dígitos repetidos (1111), secuencias (1234, 9876), PINs muy comunes (1212, 2580) ni
    /// igual al código de caja (<paramref name="posCode"/>).
    /// </summary>
    public static Error CheckPin(string pin, int length, string? posCode = null)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length != length || !pin.All(char.IsAsciiDigit))
        {
            return Error.Validation("IDENTITY.INVALID_PIN", $"El PIN debe tener exactamente {length} dígitos.");
        }

        var ascending = pin.Zip(pin.Skip(1)).All(p => p.Second - p.First == 1);
        var descending = pin.Zip(pin.Skip(1)).All(p => p.First - p.Second == 1);
        if (pin.Distinct().Count() == 1 || ascending || descending)
        {
            return Error.Validation("IDENTITY.PIN_TOO_SIMPLE", "El PIN no puede ser una secuencia ni repetir el mismo dígito.");
        }

        if (CommonPins.Contains(pin))
        {
            return Error.Validation("IDENTITY.PIN_TOO_COMMON", "Ese PIN es demasiado común (como 1212 o 2580): elija otro.");
        }

        if (posCode is not null && string.Equals(pin, posCode.Trim(), StringComparison.Ordinal))
        {
            return Error.Validation("IDENTITY.PIN_EQUALS_POS_CODE", "El PIN no puede ser igual al código de caja.");
        }

        return Error.None;
    }

    /// <summary>
    /// Código de caja con el que un usuario define o cambia su propio PIN (DU-03): si ya tiene uno, se conserva (solo un administrador lo
    /// cambia); si no, debe elegir uno válido.
    /// </summary>
    public static Result<string> ResolveOwnPosCode(string? currentPosCode, string? requestedPosCode)
    {
        var requested = string.IsNullOrWhiteSpace(requestedPosCode) ? null : requestedPosCode.Trim();
        if (currentPosCode is not null)
        {
            return requested is null || requested == currentPosCode ? currentPosCode : IdentityErrors.PosCodeChangeRequiresAdmin;
        }

        if (requested is null)
        {
            return IdentityErrors.PosCodeRequired;
        }

        return IsValidPosCode(requested) ? requested : IdentityErrors.InvalidPosCode;
    }

    /// <summary>
    /// DU-07: ¿dejaría la empresa sin Propietario activo? <paramref name="isActiveOwner"/>: hoy es Propietario activo;
    /// <paramref name="remainsOwner"/>: lo sigue siendo tras el cambio; <paramref name="otherActiveOwners"/>: los demás Propietarios activos.
    /// </summary>
    public static Error CheckOwnerRemains(bool isActiveOwner, bool remainsOwner, int otherActiveOwners) =>
        isActiveOwner && !remainsOwner && otherActiveOwners == 0 ? IdentityErrors.LastOwner : Error.None;

    // Fase 13-B (D13B-09): también un correo (el Propietario entra con el correo de su cuenta del portal).
    [GeneratedRegex("^[a-z0-9._@+-]{3,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleCodePattern();

    [GeneratedRegex("^[0-9]{3,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex PosCodePattern();
}

/// <summary>Errores de negocio del módulo Identity con código estable.</summary>
public static class IdentityErrors
{
    public static readonly Error UserHasOpenCashSession = Error.BusinessRule(
        "IDENTITY.USER_HAS_OPEN_SESSION", "El usuario tiene un turno de caja sin cerrar: ciérrelo (o que lo cierre un supervisor) antes de desactivarlo (RN-SEC-07).");

    /// <summary>Mismo error exista o no el usuario: no revela qué usuarios existen.</summary>
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("AUTH.INVALID_CREDENTIALS", "Usuario o contraseña incorrectos.");

    public static readonly Error UserLocked = Error.Unauthorized(
        "AUTH.USER_LOCKED", "El usuario está bloqueado temporalmente por intentos fallidos. Intente más tarde o pida el desbloqueo.");

    public static readonly Error PinRequiresTerminal =
        Error.Forbidden("AUTH.PIN_REQUIRES_TERMINAL", "El ingreso con PIN solo está permitido desde una caja registrada.");

    public static readonly Error InvalidUsername =
        Error.Validation("IDENTITY.INVALID_USERNAME", "El usuario debe tener de 3 a 120 caracteres: minúsculas, dígitos, punto, guion, guion bajo, o ser un correo.");

    public static readonly Error InvalidPosCode =
        Error.Validation("IDENTITY.INVALID_POS_CODE", "El código de cajero debe tener de 3 a 6 dígitos.");

    public static readonly Error InvalidRoleCode =
        Error.Validation("IDENTITY.INVALID_ROLE_CODE", "El código del rol debe tener de 2 a 40 mayúsculas, dígitos o guiones bajos.");

    public static readonly Error UserNotFound = Error.NotFound("IDENTITY.USER_NOT_FOUND", "El usuario no existe.");

    public static readonly Error RoleNotFound = Error.NotFound("IDENTITY.ROLE_NOT_FOUND", "El rol no existe.");

    public static readonly Error EmployeeNotFound = Error.NotFound("IDENTITY.EMPLOYEE_NOT_FOUND", "El empleado no existe.");

    public static readonly Error SessionNotFound = Error.NotFound("IDENTITY.SESSION_NOT_FOUND", "La sesión no existe o ya terminó.");

    public static readonly Error UsernameDuplicated = Error.Conflict("IDENTITY.USERNAME_DUPLICATED", "Ya existe un usuario con ese nombre.");

    public static readonly Error PosCodeDuplicated = Error.Conflict("IDENTITY.POS_CODE_DUPLICATED", "Ya existe un cajero con ese código.");

    public static readonly Error SystemRoleImmutable =
        Error.BusinessRule("IDENTITY.SYSTEM_ROLE_IMMUTABLE", "Los roles de sistema no se modifican ni se borran: clónelo y edite la copia.");

    public static readonly Error LastAdministrator = Error.BusinessRule(
        "IDENTITY.LAST_ADMINISTRATOR", "Debe quedar al menos un Propietario o Administrador activo.");

    public static readonly Error LastOwner = Error.BusinessRule(
        "IDENTITY.LAST_OWNER", "Debe quedar al menos un Propietario activo: asigne el rol Propietario a otro usuario antes de quitárselo a este.");

    public static readonly Error TargetOutranks = Error.Forbidden(
        "IDENTITY.TARGET_OUTRANKS", "No puede gestionar a un usuario que tiene permisos que usted no tiene (por ejemplo, el Propietario).");

    public static readonly Error PosCodeRequired =
        Error.Validation("IDENTITY.POS_CODE_REQUIRED", "Elija su código de caja (3 a 6 dígitos) para definir el PIN.");

    public static readonly Error PosCodeChangeRequiresAdmin = Error.Validation(
        "IDENTITY.POS_CODE_CHANGE_REQUIRES_ADMIN", "Su código de caja solo lo cambia un administrador desde Usuarios → Restablecer PIN.");

    public static readonly Error CurrentPinInvalid = Error.Validation("IDENTITY.CURRENT_PIN_INVALID", "El PIN actual no es correcto.");

    public static readonly Error UseChangePin =
        Error.BusinessRule("IDENTITY.USE_CHANGE_PIN", "Para su propio PIN use «Cambiar PIN de caja» en su menú de usuario.");

    public static readonly Error PrivilegeEscalation = Error.Forbidden(
        "IDENTITY.PRIVILEGE_ESCALATION", "No puede conceder permisos que usted no tiene.");

    public static readonly Error SelfModification = Error.Forbidden(
        "IDENTITY.SELF_MODIFICATION", "No puede cambiar sus propios roles, permisos o estado.");

    public static readonly Error PasswordReused =
        Error.Validation("IDENTITY.PASSWORD_REUSED", "No puede reutilizar una de sus últimas contraseñas.");

    public static readonly Error CurrentPasswordInvalid =
        Error.Validation("IDENTITY.CURRENT_PASSWORD_INVALID", "La contraseña actual no es correcta.");

    public static readonly Error SelfAuthorization =
        Error.Forbidden("AUTH.SELF_AUTHORIZATION", "Un usuario no puede autorizarse a sí mismo (RN-SEC-03).");

    public static readonly Error SupervisorLacksPermission =
        Error.Forbidden("AUTH.SUPERVISOR_LACKS_PERMISSION", "El supervisor no tiene el permiso requerido en esta sucursal.");

    public static readonly Error OwnerAlreadyExists =
        Error.Conflict("SETUP.OWNER_ALREADY_EXISTS", "La empresa ya tiene un Propietario activo.");

    public static readonly Error PortalLinkInvalid = Error.Validation(
        "SETUP.PORTAL_LINK_INVALID", "La validación de tu cuenta del portal venció o no es válida. Vuelve a escribir tu correo y contraseña del portal.");

    public static readonly Error PortalLinkOtherCompany = Error.Validation(
        "SETUP.PORTAL_LINK_OTHER_COMPANY", "Tu cuenta del portal es de otra empresa: el NIT no coincide con el de la licencia.");

    public static readonly Error LocalOnly =
        Error.Forbidden("SECURITY.LOCAL_ONLY", "Esta operación solo se puede hacer desde el propio servidor.");
}
