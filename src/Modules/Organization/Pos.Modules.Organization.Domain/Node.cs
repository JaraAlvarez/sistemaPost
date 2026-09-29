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
    private Node(Guid id, Guid companyId, Guid? branchId, NodeKind kind, string name, DateTimeOffset registeredAt)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        Kind = kind;
        Name = name;
        RegisteredAt = registeredAt;
    }

    public Guid CompanyId { get; private set; }

    public Guid? BranchId { get; private set; }

    public NodeKind Kind { get; private set; }

    public string Name { get; private set; }

    /// <summary>Se incrementa al restaurar un backup: distingue dos "vidas" del mismo nodo.</summary>
    public int Epoch { get; private set; } = 1;

    public RecordStatus Status { get; private set; } = RecordStatus.Active;

    public DateTimeOffset RegisteredAt { get; private set; }

    public string AuditLabel => $"Nodo {Name} ({Kind})";

    public static Node RegisterLocal(Guid installationId, Guid companyId, Guid branchId, NodeKind kind, string name, DateTimeOffset now)
    {
        if (kind == NodeKind.Cloud)
        {
            throw new DomainException("Un nodo local siempre pertenece a una sucursal.");
        }

        return new Node(installationId, companyId, branchId, kind, name, now);
    }
}
