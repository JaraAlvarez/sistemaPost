using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Catalog.Domain;

/// <summary>
/// Categoría: árbol de hasta 4 niveles con ruta materializada (<c>/id1/id2/</c>) para consultar subárboles sin recursión.
/// El nombre es único entre hermanas.
/// </summary>
[Audited("catalog")]
public sealed class Category : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const int MaxLevel = 4;

    public const int MaxNameLength = 80;

    private Category(Guid id, Guid companyId, string name, string path)
        : base(id)
    {
        CompanyId = companyId;
        Name = name;
        Path = path;
    }

    public Guid CompanyId { get; private set; }

    public Guid? ParentId { get; private set; }

    public string Name { get; private set; }

    public short Level { get; private set; }

    public string Path { get; private set; }

    public int SortOrder { get; private set; }

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public string AuditLabel => $"Categoría {Name}";

    public static Result<Category> Create(Guid id, Guid companyId, string name, Category? parent, int sortOrder = 0)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return CatalogErrors.InvalidName;
        }

        if (parent is not null && parent.Level >= MaxLevel)
        {
            return CatalogErrors.CategoryTooDeep;
        }

        return new Category(id, companyId, trimmed, $"{parent?.Path ?? "/"}{id:D}/")
        {
            ParentId = parent?.Id,
            Level = (short)((parent?.Level ?? 0) + 1),
            SortOrder = sortOrder,
        };
    }

    public Result Rename(string name, int sortOrder)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return CatalogErrors.InvalidName;
        }

        Name = trimmed;
        SortOrder = sortOrder;
        return Result.Success();
    }

    /// <summary>
    /// Mueve la categoría bajo otra (o a la raíz). <paramref name="subtreeHeight"/>: niveles de su subárbol incluida ella
    /// (1 si no tiene hijas). Devuelve la ruta anterior para actualizar a las descendientes con <see cref="Rebase"/>.
    /// </summary>
    public Result<string> MoveTo(Category? newParent, int subtreeHeight)
    {
        if (newParent is not null && (newParent.Id == Id || newParent.Path.StartsWith(Path, StringComparison.Ordinal)))
        {
            return CatalogErrors.CategoryCycle;
        }

        var newLevel = (newParent?.Level ?? 0) + 1;
        if (newLevel + subtreeHeight - 1 > MaxLevel)
        {
            return CatalogErrors.CategoryTooDeep;
        }

        var oldPath = Path;
        ParentId = newParent?.Id;
        Level = (short)newLevel;
        Path = $"{newParent?.Path ?? "/"}{Id:D}/";
        return oldPath;
    }

    /// <summary>Actualiza una descendiente después de mover su ancestro de <paramref name="oldPrefix"/> a <paramref name="newPrefix"/>.</summary>
    public void Rebase(string oldPrefix, string newPrefix, int levelDelta)
    {
        if (!Path.StartsWith(oldPrefix, StringComparison.Ordinal))
        {
            throw new DomainException("La categoría no pertenece al subárbol movido.");
        }

        Path = newPrefix + Path[oldPrefix.Length..];
        Level = (short)(Level + levelDelta);
    }

    public void Deactivate() => Status = MasterStatus.Inactive;

    public void Activate() => Status = MasterStatus.Active;
}

[Audited("catalog")]
public sealed class Brand : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const int MaxNameLength = 80;

    private Brand(Guid id, Guid companyId, string name)
        : base(id)
    {
        CompanyId = companyId;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public string Name { get; private set; }

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public string AuditLabel => $"Marca {Name}";

    public static Result<Brand> Create(Guid id, Guid companyId, string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length is 0 or > MaxNameLength ? CatalogErrors.InvalidName : new Brand(id, companyId, trimmed);
    }

    public Result Rename(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return CatalogErrors.InvalidName;
        }

        Name = trimmed;
        return Result.Success();
    }

    public void Deactivate() => Status = MasterStatus.Inactive;

    public void Activate() => Status = MasterStatus.Active;
}

/// <summary>
/// Planificación de vigencias semiabiertas <c>[desde, hasta)</c> sin solapamientos (tarifas y precios, RN-CAT-05).
/// Para agregar un valor que empieza en <c>from</c>: el periodo que lo cubre se cierra en <c>from</c>; si ya hay uno
/// programado después, el nuevo termina donde empieza ese; si otro empieza exactamente en <c>from</c>, se reemplaza.
/// </summary>
public static class ValidityPlanner
{
    public sealed record Plan<T>(int? CloseIndex, int? ReplaceIndex, T? NewValidTo)
        where T : struct, IComparable<T>;

    public static Plan<T> PlanInsert<T>(IReadOnlyList<(T From, T? To)> periods, T from)
        where T : struct, IComparable<T>
    {
        ArgumentNullException.ThrowIfNull(periods);
        int? close = null;
        int? replace = null;
        T? nextStart = null;
        for (var i = 0; i < periods.Count; i++)
        {
            var (start, end) = periods[i];
            var cmp = start.CompareTo(from);
            if (cmp == 0)
            {
                replace = i;
            }
            else if (cmp < 0 && (end is null || end.Value.CompareTo(from) > 0))
            {
                close = i;
            }
            else if (cmp > 0 && (nextStart is null || start.CompareTo(nextStart.Value) < 0))
            {
                nextStart = start;
            }
        }

        return new Plan<T>(close, replace, replace is { } r ? periods[r].To : nextStart);
    }
}
