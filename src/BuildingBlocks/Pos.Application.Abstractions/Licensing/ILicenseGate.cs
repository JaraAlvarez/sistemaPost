using Pos.SharedKernel.Results;

namespace Pos.Application.Abstractions.Licensing;

/// <summary>
/// Lo que la licencia permite hacer ahora (docs/fases/fase-12b-propuesta.md §2). Lo calcula el módulo Licensing a partir del token
/// firmado, la huella del equipo y el reloj confiable; mientras no hay módulo de licencia, nada está restringido.
/// </summary>
/// <param name="Restricted"><c>RESTRICTED</c> o <c>REACTIVATION_REQUIRED</c>: solo pasan los comandos <see cref="IAllowedWhenRestricted"/>.</param>
/// <param name="Demo">Instalación en demostración: los documentos impresos llevan la marca "DEMOSTRACIÓN".</param>
/// <param name="Reason">Motivo legible de la restricción (se antepone al mensaje del error).</param>
public sealed record LicenseGateState(bool Restricted, bool Demo, string? Reason)
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
#pragma warning restore CA1040

public static class LicenseGateErrors
{
    public static readonly Error Restricted = Error.BusinessRule(
        "LICENSE.RESTRICTED",
        "La licencia está restringida: puede vender y cerrar las jornadas abiertas, consultar, exportar y respaldar, pero no abrir jornadas ni administrar. Revise la licencia en Configuración.");
}

/// <summary>Sin módulo de licencia (pruebas de otros módulos, consola): nada restringido.</summary>
public sealed class UnrestrictedLicenseGate : ILicenseGate
{
    public LicenseGateState Current => LicenseGateState.Unrestricted;
}
