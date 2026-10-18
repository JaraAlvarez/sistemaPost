using Pos.SharedKernel.Results;

namespace Pos.Application.Abstractions.Licensing;

/// <summary>
/// Lo que la licencia permite hacer ahora (docs/fases/fase-12b-propuesta.md §2). Lo calcula el módulo Licensing a partir del token
/// firmado, la huella del equipo y el reloj confiable; mientras no hay módulo de licencia, nada está restringido.
/// </summary>
/// <param name="Restricted"><c>RESTRICTED</c> o <c>REACTIVATION_REQUIRED</c>: solo pasan los comandos <see cref="IAllowedWhenRestricted"/>.</param>
/// <param name="Demo">Instalación en demostración: los documentos impresos llevan la marca "DEMOSTRACIÓN".</param>
/// <param name="Reason">Motivo legible de la restricción (se antepone al mensaje del error).</param>
/// <param name="SalesWindowClosed">
/// Restringida desde hace más de 24 h: los comandos <see cref="IRestrictedSalesWindow"/> (vender) también se bloquean y solo queda
/// cerrar la jornada.
/// </param>
/// <param name="DemoEnded">La prueba gratuita de este equipo ya terminó (lo muestran el asistente y la pantalla de ingreso).</param>
public sealed record LicenseGateState(bool Restricted, bool Demo, string? Reason, bool SalesWindowClosed = false, bool DemoEnded = false)
{
    public static readonly LicenseGateState Unrestricted = new(false, false, null);
}

public interface ILicenseGate
{
    LicenseGateState Current { get; }
}

/// <summary>
/// Marca un comando que sigue disponible con la licencia restringida (D12B-02: lista de PERMITIDOS). Vender y cerrar las jornadas ya
/// abiertas, iniciar sesión, respaldar, exportar y verificar nunca se bloquean (RN-LIC-01/04). Un comando sin esta marca queda
/// bloqueado en <c>RESTRICTED</c> (nunca en los demás estados). Las consultas no pasan por el filtro.
/// </summary>
#pragma warning disable CA1040 // Interfaz marcadora intencional.
public interface IAllowedWhenRestricted;

/// <summary>
/// Comando de VENTA permitido con la licencia restringida solo durante la ventana de 24 h desde que quedó restringida (H6). Pasada la
/// ventana se bloquea; cancelar, anular, reimprimir y cerrar la jornada siguen disponibles.
/// </summary>
public interface IRestrictedSalesWindow : IAllowedWhenRestricted;
#pragma warning restore CA1040

public static class LicenseGateErrors
{
    public static readonly Error Restricted = Error.BusinessRule(
        "LICENSE.RESTRICTED",
        "La licencia está restringida: puede vender y cerrar las jornadas abiertas, consultar, exportar y respaldar, pero no abrir jornadas ni administrar. Revise la licencia en Configuración.");

    public static readonly Error SalesWindowClosed = Error.BusinessRule(
        "LICENSE.RESTRICTED",
        "Pasaron más de 24 horas desde que la licencia quedó restringida: ya no se puede vender, solo cerrar la jornada, consultar, exportar y respaldar. Revise la licencia en Configuración.");
}

/// <summary>Sin módulo de licencia (pruebas de otros módulos, consola): nada restringido.</summary>
public sealed class UnrestrictedLicenseGate : ILicenseGate
{
    public LicenseGateState Current => LicenseGateState.Unrestricted;
}
