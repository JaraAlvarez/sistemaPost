using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Inventory.Application;

/// <summary>Dirección y valorización de cada tipo de movimiento (doc 07).</summary>
public static class MovementRules
{
    public static int Direction(MovementType type) => type switch
    {
        MovementType.InitialBalance or MovementType.PurchaseReceipt or MovementType.SaleVoid or MovementType.CustomerReturn
            or MovementType.CustomerReturnDamaged or MovementType.AdjustmentIn or MovementType.TransferIn or MovementType.CountAdjustmentIn => 1,
        _ => -1,
    };

    /// <summary>Entradas que cambian el costo promedio (llegan con su costo). Las demás entradas entran al promedio vigente.</summary>
    public static bool IsValuedInflow(MovementType type) => type is MovementType.InitialBalance or MovementType.PurchaseReceipt
        or MovementType.SaleVoid or MovementType.CustomerReturn or MovementType.CustomerReturnDamaged or MovementType.TransferIn;

    public static string Db(MovementType type) => type switch
    {
        MovementType.InitialBalance => "INITIAL_BALANCE",
        MovementType.PurchaseReceipt => "PURCHASE_RECEIPT",
        MovementType.Sale => "SALE",
        MovementType.SaleVoid => "SALE_VOID",
        MovementType.CustomerReturn => "CUSTOMER_RETURN",
        MovementType.CustomerReturnDamaged => "CUSTOMER_RETURN_DAMAGED",
        MovementType.SupplierReturn => "SUPPLIER_RETURN",
        MovementType.AdjustmentIn => "ADJUSTMENT_IN",
        MovementType.AdjustmentOut => "ADJUSTMENT_OUT",
        MovementType.Loss => "LOSS",
        MovementType.Damage => "DAMAGE",
        MovementType.Expiry => "EXPIRY",
        MovementType.InternalUse => "INTERNAL_USE",
        MovementType.TransferOut => "TRANSFER_OUT",
        MovementType.TransferIn => "TRANSFER_IN",
        MovementType.CountAdjustmentIn => "COUNT_ADJUSTMENT_IN",
        MovementType.CountAdjustmentOut => "COUNT_ADJUSTMENT_OUT",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Tipo de movimiento de una línea de ajuste según el motivo y el signo.</summary>
    public static MovementType ForAdjustment(ReasonKind kind, decimal signedQuantity) => kind switch
    {
        ReasonKind.InitialBalance => MovementType.InitialBalance,
        ReasonKind.Loss => MovementType.Loss,
        ReasonKind.Damage => MovementType.Damage,
        ReasonKind.Expiry => MovementType.Expiry,
        ReasonKind.InternalUse => MovementType.InternalUse,
        _ => signedQuantity > 0m ? MovementType.AdjustmentIn : MovementType.AdjustmentOut,
    };
}

/// <summary>Documentos y maestros del inventario (EF Core).</summary>
public interface IInventoryStore
{
    void Add(AdjustmentReason reason);

    void Add(InventoryAdjustment adjustment);

    void Add(InventoryCount count);

    void Add(StockTransfer transfer);

    void Add(StockPolicy policy);

    void Remove(object entity);

    Task<IReadOnlyList<AdjustmentReason>> GetReasonsAsync(CancellationToken cancellationToken);

    Task<AdjustmentReason?> GetReasonAsync(Guid id, CancellationToken cancellationToken);

    Task<InventoryAdjustment?> GetAdjustmentAsync(Guid id, CancellationToken cancellationToken);

    Task<InventoryCount?> GetCountAsync(Guid id, CancellationToken cancellationToken);

    Task<StockTransfer?> GetTransferAsync(Guid id, CancellationToken cancellationToken);

    Task<StockPolicy?> GetPolicyAsync(Guid warehouseId, Guid productId, CancellationToken cancellationToken);
}

/// <summary>Lecturas y operaciones directas sobre saldos y kardex (SQL en la transacción en curso).</summary>
public interface IStockLedger
{
    /// <summary>Saldo, valor y promedio actuales de productos en una bodega (sin fila = saldo vacío).</summary>
    Task<IReadOnlyDictionary<Guid, StockState>> GetStatesAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    /// <summary>Saldo teórico y seq del último movimiento de cada producto (para congelar un conteo).</summary>
    Task<IReadOnlyList<(Guid ProductId, decimal SystemQty, long SnapshotSeq)>> SnapshotAsync(
        Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    /// <summary>Teórico actualizado: congelado + movimientos del producto con seq posterior al congelado.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> ExpectedAsync(Guid warehouseId, IReadOnlyCollection<CountLine> lines, CancellationToken cancellationToken);

    /// <summary>Productos (de la lista) con algún movimiento en la bodega.</summary>
    Task<IReadOnlySet<Guid>> ProductsWithMovementsAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}

/// <summary>Consultas de pantalla del inventario (existencias, kardex, documentos).</summary>
public interface IInventoryReadModel
{
    Task<IReadOnlyList<StockDto>> GetStockAsync(StockFilter filter, bool includeCosts, CancellationToken cancellationToken);

    Task<KardexDto?> GetKardexAsync(Guid warehouseId, Guid productId, DateOnly? from, DateOnly? to, bool includeCosts, CancellationToken cancellationToken);

    Task<IReadOnlyList<(Guid Id, string Number, string Kind, string Status, Guid WarehouseId, DateTimeOffset CreatedAt)>> ListDocumentsAsync(
        string kind, string? status, CancellationToken cancellationToken);
}

public sealed record StockFilter(Guid? WarehouseId, Guid? ProductId, string? Search, bool BelowMinimumOnly, Guid BranchId);

/// <summary>Verificación y reconstrucción de saldos contra el kardex (RN-INV-11).</summary>
public interface IStockVerifier
{
    Task<VerificationDto> VerifyAsync(string kind, Guid? triggeredBy, CancellationToken cancellationToken);

    Task<IReadOnlyList<VerificationDto>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Recalcula un saldo desde su kardex; devuelve el saldo anterior y el nuevo.</summary>
    Task<(StockState Before, StockState After)> RebuildAsync(Guid warehouseId, Guid productId, CancellationToken cancellationToken);
}

/// <summary>Configuraciones del inventario. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class InventorySettings
{
    /// <summary>RN-INV-03 / D8: permitir saldos negativos (empresa o sucursal).</summary>
    public static readonly SettingDefinition<bool> AllowNegativeStock = new(
        "inventory.allow_negative_stock",
        false,
        SettingScope.Company | SettingScope.Branch,
        "Permitir que un ajuste o traslado deje existencias negativas.");

    /// <summary>RN-INV-04: valor (absoluto) a partir del cual un ajuste requiere aprobación de otro usuario.</summary>
    public static readonly SettingDefinition<decimal> AdjustmentApprovalThreshold = new(
        "inventory.adjustment_approval_threshold",
        500_000m,
        SettingScope.Company,
        "Valor de un ajuste (a costo) desde el cual requiere la aprobación de otro usuario.",
        v => v >= 0m ? null : "Debe ser mayor o igual a cero.");

    /// <summary>Productos del alcance de un conteo que nadie contó: se asumen en cero (true) o se excluyen (false).</summary>
    public static readonly SettingDefinition<bool> UncountedAsZero = new(
        "inventory.count_uncounted_as_zero",
        false,
        SettingScope.Company,
        "En un conteo, los productos no contados se asumen en cero (si no, se excluyen).");

    /// <summary>Hora local a partir de la cual corre la verificación diaria saldo ↔ kardex.</summary>
    public static readonly SettingDefinition<int> VerificationHour = new(
        "inventory.verification_hour",
        3,
        SettingScope.Company,
        "Hora (0–23) de la verificación diaria de saldos contra el kardex.",
        v => v is >= 0 and <= 23 ? null : "Entre 0 y 23.");

    public static IEnumerable<SettingDefinition> All => [AllowNegativeStock, AdjustmentApprovalThreshold, UncountedAsZero, VerificationHour];
}

public sealed class InventorySettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => InventorySettings.All;
}

public sealed class InventoryPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => InventoryPermissions.All;
}

/// <summary>Motivos de ajuste iniciales de cada empresa (propuesta §4.4). Idempotente.</summary>
public sealed class InventoryInitializer(IInventoryStore store, IIdGenerator ids) : ICompanyInitializer
{
    public const string InitialBalance = "INITIAL_BALANCE";
    public const string TransferShortage = "TRANSFER_SHORTAGE";

    public int Order => 200;

    public async Task InitializeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var existing = (await store.GetReasonsAsync(cancellationToken)).Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        (string Code, string Name, ReasonKind Kind, bool Note)[] seeds =
        [
            (InitialBalance, "Saldo inicial", ReasonKind.InitialBalance, false),
            ("CORRECTION", "Error de digitación o de conteo", ReasonKind.Adjustment, true),
            ("FOUND", "Mercancía encontrada", ReasonKind.Adjustment, false),
            ("DAMAGE", "Producto averiado", ReasonKind.Damage, false),
            ("EXPIRY", "Producto vencido", ReasonKind.Expiry, false),
            ("LOSS", "Pérdida o robo", ReasonKind.Loss, true),
            ("INTERNAL_USE", "Consumo interno", ReasonKind.InternalUse, false),
            ("TASTING", "Degustación", ReasonKind.InternalUse, false),
            (TransferShortage, "Faltante en traslado", ReasonKind.Loss, false),
        ];
        foreach (var (code, name, kind, note) in seeds.Where(s => !existing.Contains(s.Code)))
        {
            store.Add(AdjustmentReason.Create(ids.NewId(), companyId, code, name, kind, note, isSystem: true).Value);
        }
    }
}

internal static class InventoryContext
{
    public static readonly Error SetupRequired =
        Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");

    public static Result<(Guid CompanyId, Guid BranchId)> RequireLocal(this IInstallationContext installation) =>
        installation.CompanyId is { } company && installation.BranchId is { } branch ? (company, branch) : SetupRequired;

    public static string Db<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}
