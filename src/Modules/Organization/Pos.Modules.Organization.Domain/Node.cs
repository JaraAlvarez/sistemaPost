using Pos.SharedKernel.Domain;

namespace Pos.Modules.Organization.Domain;

public enum NodeKind
{
    /// <summary>Edición Caja Única.</summary>
    AllInOne,

    /// <summary>Servidor de tienda (edición Multicaja).</summary>
    StoreServer,

    /// <summary>Caja autónoma con diario local (fase futura).</summary>
    TerminalAutonomous,

    /// <summary>Nube (sincronización y portal web).</summary>
    Cloud,
}

/// <summary>
/// Nodo: una instalación con base de datos propia. Es la identidad que usan la auditoría (una cadena por nodo),
/// las series (un escritor), la sincronización y la licencia (revisión arquitectónica §2).
/// </summary>
[Audited("organization")]
public sealed class Node : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    /// <summary>El primer nodo de una empresa (la instalación que la crea) tiene el número 1.</summary>
    public const short FirstNodeNumber = 1;

    private Node(Guid id, Guid companyId, Guid? branchId, NodeKind kind, string name, DateTimeOffset registeredAt, short number)
        : base(id)
    {
        Number = number;
        CompanyId = companyId;
        BranchId = branchId;
        Kind = kind;
        Name = name;
        RegisteredAt = registeredAt;
    }

    public Guid CompanyId { get; private set; }

    public Guid? BranchId { get; private set; }

    public NodeKind Kind { get; private set; }

    /// <summary>
    /// Número corto del nodo dentro de la empresa (1–999), asignado por la autoridad de la empresa: el asistente al primer
    /// nodo, la nube a los que se unen después. Forma parte de los códigos internos (SKU y EAN-13 "29…") para que dos
    /// tiendas sin conexión nunca generen el mismo código (D4-07).
    /// </summary>
    public short Number { get; private set; }

    public string Name { get; private set; }

    /// <summary>Se incrementa al restaurar un backup: distingue dos "vidas" del mismo nodo.</summary>
    public int Epoch { get; private set; } = 1;

    public RecordStatus Status { get; private set; } = RecordStatus.Active;

    public DateTimeOffset RegisteredAt { get; private set; }

    public string AuditLabel => $"Nodo {Name} ({Kind})";

    public static Node RegisterLocal(
        Guid installationId, Guid companyId, Guid branchId, NodeKind kind, string name, DateTimeOffset now, short number = FirstNodeNumber)
    {
        if (kind == NodeKind.Cloud)
        {
            throw new DomainException("Un nodo local siempre pertenece a una sucursal.");
        }

        if (number is < 1 or > 999)
        {
            throw new DomainException("El número de nodo debe estar entre 1 y 999.");
        }

        return new Node(installationId, companyId, branchId, kind, name, now, number);
    }
}
