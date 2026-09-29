namespace Pos.SharedKernel.Domain;

/// <summary>
/// Entidad de negocio que pertenece a una empresa (columna <c>company_id</c>). Los repositorios filtran siempre
/// por la empresa del contexto; una prueba de arquitectura exige esta interfaz en toda entidad de negocio.
/// </summary>
public interface ICompanyOwned
{
    Guid CompanyId { get; }
}

/// <summary>
/// Maestro con borrado lógico (<c>deleted_at</c>/<c>deleted_by</c>). Eliminarlo en el repositorio lo marca como borrado;
/// las consultas lo excluyen automáticamente. Los documentos nunca implementan esta interfaz: no se borran.
/// </summary>
#pragma warning disable CA1040 // Interfaz marcadora intencional: la infraestructura la usa por convención.
public interface ISoftDeletable;

/// <summary>
/// Maestro sincronizable con otros nodos (tiendas y portal). La infraestructura incrementa su <c>row_version</c>
/// lógico en cada cambio; la sincronización lo usa para detectar ediciones concurrentes.
/// </summary>
public interface ISyncVersioned;
#pragma warning restore CA1040

/// <summary>Descripción legible que la auditoría guarda como snapshot (p. ej. "Sucursal S01 · Centro").</summary>
public interface IHasAuditLabel
{
    string AuditLabel { get; }
}

/// <summary>Los cambios de esta entidad se registran automáticamente en la bitácora de auditoría (antes/después).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class AuditedAttribute(string module) : Attribute
{
    /// <summary>Módulo que se registra en la auditoría (p. ej. <c>organization</c>).</summary>
    public string Module { get; } = module;
}

/// <summary>El valor de esta propiedad nunca se escribe en la auditoría (se registra como <c>***</c>).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute : Attribute;

/// <summary>Esta propiedad no se registra en la auditoría (p. ej. imágenes o datos derivados).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotAuditedAttribute : Attribute;
