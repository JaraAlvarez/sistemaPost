using Pos.Licensing.Contracts;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Licensing.Domain;

/// <summary>Errores de la API del POS (códigos del contrato <see cref="LicenseErrorCodes"/>).</summary>
public static class LicenseApiErrors
{
    /// <summary>Misma respuesta para una clave inexistente y para una revocada (no se enumeran claves).</summary>
    public static readonly Error KeyInvalid = Error.Forbidden(LicenseErrorCodes.KeyInvalid, "La clave de licencia no es válida.");

    public static readonly Error KeyFormatInvalid = Error.Validation(
        LicenseErrorCodes.KeyFormatInvalid, "La clave debe tener el formato POS-XXXXX-XXXXX-XXXXX-XXXXX. Revise que la copió completa.");

    public static readonly Error EditionMismatch = Error.BusinessRule(
        LicenseErrorCodes.EditionMismatch, "La edición de la licencia no admite este tipo de instalación (Caja Única solo se instala en un equipo que hace todo).");

    public static readonly Error InstallationsExceeded = Error.Conflict(
        LicenseErrorCodes.InstallationsExceeded, "La licencia llegó al máximo de instalaciones (sucursales) contratadas.");

    public static readonly Error SubscriptionInactive = Error.BusinessRule(
        LicenseErrorCodes.SubscriptionInactive, "La suscripción de la empresa no está vigente (suspendida, cancelada o vencida). Comuníquese con soporte.");

    public static readonly Error NitMismatch = Error.BusinessRule(
        LicenseErrorCodes.NitMismatch, "El NIT configurado en el sistema no corresponde a la empresa de esta licencia.");

    public static readonly Error ReactivationRequired = Error.Conflict(
        LicenseErrorCodes.ReactivationRequired, "Este equipo debe activarse de nuevo con la clave de licencia (cambió el equipo o fue liberado).");

    public static readonly Error InstallationActiveOnOtherDevice = Error.Conflict(
        LicenseErrorCodes.InstallationActiveOnOtherDevice, "Esta instalación ya está activa en otro equipo. Pida a soporte liberar el equipo anterior.");

    public static readonly Error InstallationOfOtherLicense = Error.Conflict(
        LicenseErrorCodes.InstallationOfOtherLicense, "Esta instalación pertenece a otra licencia.");

    public static readonly Error TokenInvalid = Error.Unauthorized(LicenseErrorCodes.TokenInvalid, "El token de licencia no es válido.");

    public static readonly Error FingerprintInvalid = Error.Validation(
        LicenseErrorCodes.FingerprintInvalid, "La huella del equipo no es válida (se necesitan al menos 2 de sus 3 componentes).");

    public static readonly Error LicenseRevoked = Error.Forbidden(LicenseErrorCodes.LicenseRevoked, "La licencia de esta instalación fue revocada.");

    public static readonly Error TooManyRequests = Error.Conflict(LicenseErrorCodes.TooManyRequests, "Demasiadas solicitudes para esta licencia. Intente más tarde.");

    public static readonly Error SigningUnavailable = Error.Unexpected(
        LicenseErrorCodes.SigningUnavailable, "El servidor de licencias no puede emitir tokens en este momento. Intente más tarde.");

    public static Error InvalidField(string field, string message) =>
        Error.Validation("VALIDATION.FAILED", "Los datos enviados no son válidos.", [new FieldError(field, "INVALID", message)]);
}

/// <summary>Errores del portal con código estable.</summary>
public static class LicensingErrors
{
    public static readonly Error AccountNotFound = Error.NotFound("LICENSING.ACCOUNT_NOT_FOUND", "La cuenta no existe.");

    public static readonly Error InvalidAccount = Error.Validation(
        "LICENSING.INVALID_ACCOUNT", "Datos de la cuenta inválidos: nombre de 1 a 200 caracteres; el NIT (opcional) con su dígito de verificación correcto.");

    public static readonly Error InvalidParentAccount = Error.BusinessRule(
        "LICENSING.INVALID_PARENT_ACCOUNT", "El distribuidor padre debe ser una cuenta de tipo distribuidor distinta de esta.");

    public static readonly Error OrganizationNotFound = Error.NotFound("LICENSING.ORGANIZATION_NOT_FOUND", "La empresa no existe.");

    public static readonly Error InvalidOrganization = Error.Validation(
        "LICENSING.INVALID_ORGANIZATION", "Datos de la empresa inválidos: razón social de 1 a 200 caracteres y ciudad de hasta 120.");

    public static readonly Error NitInvalid = Error.Validation("LICENSING.NIT_INVALID", "El NIT no es válido: revise el número y el dígito de verificación.");

    public static readonly Error NitDuplicated = Error.Conflict("LICENSING.NIT_DUPLICATED", "Ya existe una empresa con ese NIT (la licencia es por razón social).");

    public static readonly Error SubscriptionNotFound = Error.NotFound("LICENSING.SUBSCRIPTION_NOT_FOUND", "La suscripción no existe.");

    public static readonly Error SubscriptionExists = Error.Conflict(
        "LICENSING.SUBSCRIPTION_EXISTS", "La empresa ya tiene una suscripción vigente; renuévela o cancélela antes de crear otra.");

    public static readonly Error InvalidTransition = Error.BusinessRule(
        "LICENSING.INVALID_TRANSITION", "La suscripción no admite esa operación en su estado actual.");

    public static readonly Error PaymentReferenceRequired = Error.Validation(
        "LICENSING.PAYMENT_REFERENCE_REQUIRED", "Indique la referencia del pago (de 3 a 100 caracteres).");

    public static readonly Error InvalidPeriods = Error.Validation("LICENSING.INVALID_PERIODS", "Los periodos a renovar deben estar entre 1 y 24.");

    public static readonly Error InvalidTrialDays = Error.Validation("LICENSING.INVALID_TRIAL_DAYS", "Los días de prueba deben estar entre 1 y 90.");

    public static readonly Error InvalidGraceDays = Error.Validation(
        "LICENSING.INVALID_GRACE_DAYS", "Los días de gracia a extender deben estar entre 1 y 30 (máximo 90 en total).");

    public static readonly Error ReasonRequired = Error.Validation("LICENSING.REASON_REQUIRED", "Indique el motivo (de 5 a 300 caracteres).");

    public static readonly Error SameEdition = Error.BusinessRule("LICENSING.SAME_EDITION", "La suscripción ya tiene esa edición.");

    public static readonly Error EditionHasStoreServers = Error.BusinessRule(
        "LICENSING.EDITION_HAS_STORE_SERVERS", "No se puede pasar a Caja Única mientras haya servidores de tienda (Multicaja) activos: libérelos primero.");

    public static readonly Error LicenseNotFound = Error.NotFound("LICENSING.LICENSE_NOT_FOUND", "La licencia no existe.");

    public static readonly Error LicenseExists = Error.Conflict(
        "LICENSING.LICENSE_EXISTS", "La empresa ya tiene una licencia vigente; regenere su clave si la perdió.");

    public static readonly Error LicenseRevoked = Error.BusinessRule("LICENSING.LICENSE_REVOKED", "La licencia ya fue revocada.");

    public static readonly Error SubscriptionRequired = Error.BusinessRule(
        "LICENSING.SUBSCRIPTION_REQUIRED", "La empresa necesita una suscripción vigente para generar su clave.");

    public static readonly Error InvalidMaxInstallations = Error.Validation(
        "LICENSING.INVALID_MAX_INSTALLATIONS", "El máximo de instalaciones debe ser 1 o más (vacío = sin límite).");

    public static readonly Error InvalidLicenseKey = Error.Validation("LICENSING.INVALID_LICENSE_KEY", "La clave no está en su forma canónica.");

    public static readonly Error ActivationNotFound = Error.NotFound("LICENSING.ACTIVATION_NOT_FOUND", "La activación no existe o ya fue liberada.");

    public static readonly Error SigningKeyNotFound = Error.NotFound("LICENSING.SIGNING_KEY_NOT_FOUND", "La clave de firma no existe.");

    public static readonly Error SigningKeyTransition = Error.BusinessRule(
        "LICENSING.SIGNING_KEY_TRANSITION", "La clave de firma no admite ese cambio de estado.");
}
