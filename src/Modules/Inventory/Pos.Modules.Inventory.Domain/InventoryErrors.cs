using Pos.SharedKernel.Results;

namespace Pos.Modules.Inventory.Domain;

/// <summary>Errores de negocio del inventario con código estable.</summary>
public static class InventoryErrors
{
    public static readonly Error AdjustmentNotFound = Error.NotFound("INVENTORY.ADJUSTMENT_NOT_FOUND", "El ajuste no existe.");

    public static readonly Error CountNotFound = Error.NotFound("INVENTORY.COUNT_NOT_FOUND", "El conteo no existe.");

    public static readonly Error TransferNotFound = Error.NotFound("INVENTORY.TRANSFER_NOT_FOUND", "El traslado no existe.");

    public static readonly Error ReasonNotFound = Error.NotFound("INVENTORY.REASON_NOT_FOUND", "El motivo de ajuste no existe o está inactivo.");

    public static readonly Error WarehouseNotFound = Error.NotFound("INVENTORY.WAREHOUSE_NOT_FOUND", "La bodega no existe.");

    public static readonly Error ProductNotFound = Error.NotFound("INVENTORY.PRODUCT_NOT_FOUND", "Uno de los productos no existe.");

    public static readonly Error WarehouseNotLocal = Error.BusinessRule(
        "INVENTORY.WAREHOUSE_NOT_LOCAL", "La bodega pertenece a otra sucursal: cada tienda mueve solo su propio inventario (D4-04).");

    public static readonly Error WarehouseInactive = Error.BusinessRule("INVENTORY.WAREHOUSE_INACTIVE", "La bodega está inactiva.");

    public static readonly Error InsufficientStock = Error.BusinessRule(
        "INVENTORY.INSUFFICIENT_STOCK", "No hay existencias suficientes y la empresa no permite saldos negativos (RN-INV-03).");

    public static readonly Error NoLines = Error.Validation("INVENTORY.NO_LINES", "El documento debe tener al menos una línea.");

    public static readonly Error DuplicatedProduct = Error.Validation("INVENTORY.DUPLICATED_PRODUCT", "Un producto aparece más de una vez en el documento.");

    public static readonly Error InvalidQuantity = Error.Validation(
        "INVENTORY.INVALID_QUANTITY", "Cantidad inválida: distinta de cero, con hasta 4 decimales (y entera si el producto no admite decimales).");

    public static readonly Error QuantitySignMismatch = Error.Validation(
        "INVENTORY.QUANTITY_SIGN", "El motivo solo admite entradas (cantidades positivas) o solo salidas.");

    public static readonly Error UnitCostRequired = Error.Validation("INVENTORY.UNIT_COST_REQUIRED", "El saldo inicial exige el costo unitario de cada línea (≥ 0).");

    public static readonly Error NoteRequired = Error.Validation("INVENTORY.NOTE_REQUIRED", "El motivo exige una observación.");

    public static readonly Error ServiceProduct = Error.BusinessRule("INVENTORY.SERVICE_PRODUCT", "Los servicios no manejan inventario (RN-INV-10).");

    public static readonly Error InitialBalanceNotAllowed = Error.BusinessRule(
        "INVENTORY.INITIAL_BALANCE_NOT_ALLOWED", "El saldo inicial solo se registra en productos sin movimientos en esa bodega.");

    public static readonly Error InvalidStatus = Error.BusinessRule("INVENTORY.INVALID_STATUS", "El documento no está en un estado que permita esta acción.");

    public static readonly Error SelfApproval = Error.BusinessRule("INVENTORY.SELF_APPROVAL", "Quien creó el ajuste no puede aprobarlo (RN-INV-04).");

    public static readonly Error ProductNotInCount = Error.BusinessRule("INVENTORY.PRODUCT_NOT_IN_COUNT", "El producto no está en el alcance del conteo.");

    public static readonly Error CountEmpty = Error.BusinessRule("INVENTORY.COUNT_EMPTY", "El alcance del conteo no tiene productos inventariables.");

    public static readonly Error SameWarehouse = Error.Validation("INVENTORY.SAME_WAREHOUSE", "Origen y destino deben ser bodegas distintas.");

    public static readonly Error TransferBranchMismatch = Error.BusinessRule(
        "INVENTORY.TRANSFER_BRANCH", "En esta versión los traslados son entre bodegas de la misma sucursal.");

    public static readonly Error ReceivedExceedsSent =
        Error.Validation("INVENTORY.RECEIVED_EXCEEDS_SENT", "No se puede recibir más de lo despachado.");

    public static readonly Error InTransitWarehouseMissing =
        Error.BusinessRule("INVENTORY.IN_TRANSIT_WAREHOUSE_MISSING", "La sucursal no tiene bodega de tránsito.");

    public static readonly Error SystemReason = Error.BusinessRule("INVENTORY.SYSTEM_REASON", "Los motivos del sistema no cambian de código ni de tipo.");

    public static readonly Error InvalidPolicy = Error.Validation(
        "INVENTORY.INVALID_POLICY", "Política inválida: mínimo ≥ 0, máximo ≥ mínimo, punto de pedido ≥ 0 y cantidad a pedir > 0.");

    public static readonly Error ImportMissingColumns =
        Error.Validation("INVENTORY.IMPORT_MISSING_COLUMNS", "El archivo de saldo inicial necesita las columnas: producto (SKU o código), cantidad y costo.");
}
