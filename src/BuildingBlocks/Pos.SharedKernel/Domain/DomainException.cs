using Pos.SharedKernel.Results;

namespace Pos.SharedKernel.Domain;

/// <summary>
/// Violación de una invariante del dominio que no debió llegar hasta aquí (la validación previa falló).
/// Los flujos de negocio esperados usan <see cref="Result"/>; esta excepción es la red de seguridad.
/// La API la traduce a HTTP 422 con su código de error.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException()
        : this(Error.Unexpected("DOMAIN.INVARIANT_VIOLATED", "Se violó una regla del dominio."))
    {
    }

    public DomainException(string message)
        : this(Error.BusinessRule("DOMAIN.INVARIANT_VIOLATED", message))
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException) =>
        Error = Error.BusinessRule("DOMAIN.INVARIANT_VIOLATED", message);

    public DomainException(Error error)
        : base(Guard.NotNull(error).Message) => Error = error;

    public Error Error { get; }
}
