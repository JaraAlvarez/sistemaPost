// Generado junto con R__audit__action_types.sql desde la misma fuente: si agrega una acción, agréguela en ambos
// (una prueba compara el catálogo del código con la BD).
namespace Pos.Modules.Audit.Contracts;

/// <summary>Acción de la bitácora: código técnico, módulo, nombre en español y severidad por defecto (D10-01).</summary>
public sealed record AuditActionDefinition(string Code, string Module, string Name, string Severity);

/// <summary>Entidad auditada automáticamente (<c>[Audited]</c>): módulo y nombre en español.</summary>
public sealed record AuditedEntityDefinition(string Module, string Name);

/// <summary>
/// Catálogo de acciones de la auditoría (Fase 10, D10-01). Las acciones explícitas las escriben los casos de uso con
/// <c>IAuditWriter</c>; las de las entidades marcadas <c>[Audited]</c> las genera el interceptor: <c>ENTIDAD_CREATED|UPDATED|DELETED</c>.
/// </summary>
public static class AuditActions
{
    public static IReadOnlyList<AuditActionDefinition> Explicit { get; } =
    [
        new("AUDIT_VERIFIED", "audit", "Bitácora verificada sin hallazgos", "INFO"),
        new("AUDIT_VERIFICATION_FAILED", "audit", "Bitácora alterada: verificación con hallazgos", "CRITICAL"),
        new("INTEGRITY_INCIDENT_ACKNOWLEDGED", "audit", "Incidente de integridad reconocido", "WARNING"),
        new("INTEGRITY_CERTIFICATE_ISSUED", "audit", "Constancia de integridad emitida", "INFO"),
        new("BACKUP_COMPLETED", "backup", "Backup realizado y verificado", "INFO"),
        new("BACKUP_DESTINATION_CHANGED", "backup", "Destino de backups creado o modificado", "WARNING"),
        new("BACKUP_DOWNLOADED", "backup", "Backup descargado", "WARNING"),
        new("BACKUP_FAILED", "backup", "Backup fallido", "CRITICAL"),
        new("BACKUP_RESTORED", "backup", "Backup restaurado", "CRITICAL"),
        new("RECOVERY_CODE_CONFIRMED", "backup", "Código de recuperación confirmado", "INFO"),
        new("RECOVERY_CODE_GENERATED", "backup", "Código de recuperación generado", "WARNING"),
        new("RESTORE_TEST_FAILED", "backup", "Restauración de prueba fallida", "CRITICAL"),
        new("RESTORE_TEST_PASSED", "backup", "Restauración de prueba correcta", "INFO"),
        new("FISCAL_BUYER_CORRECTED", "billing", "Datos del adquirente corregidos en un documento electrónico", "WARNING"),
        new("FISCAL_CREDENTIALS_CHANGED", "billing", "Credenciales del proveedor de facturación electrónica cambiadas", "CRITICAL"),
        new("FISCAL_DOCUMENT_REJECTED", "billing", "Documento electrónico rechazado", "CRITICAL"),
        new("FISCAL_DOCUMENT_RETRIED", "billing", "Reintento manual de un documento electrónico", "INFO"),
        new("FISCAL_RANGE_ALERT", "billing", "Rango de numeración por agotarse o vencer", "WARNING"),
        new("FISCAL_RANGE_ASSIGNED", "billing", "Rango de numeración asignado", "WARNING"),
        new("FISCAL_RANGE_MISSING", "billing", "Documento electrónico sin rango de numeración vigente", "CRITICAL"),
        new("FISCAL_RANGES_SYNCED", "billing", "Rangos de numeración sincronizados", "INFO"),
        new("FISCAL_SETTINGS_CHANGED", "billing", "Modo o ambiente de la facturación electrónica cambiado", "WARNING"),
        new("CASH_CORRECTION", "cash", "Corrección de caja", "WARNING"),
        new("CASH_DRAWER_OPENED_NO_SALE", "cash", "Cajón abierto sin venta", "WARNING"),
        new("CASH_SESSION_CLOSED", "cash", "Jornada de caja cerrada", "INFO"),
        new("CASH_SESSION_CLOSED_BY_SUPERVISOR", "cash", "Jornada cerrada por un supervisor", "WARNING"),
        new("CASH_SESSION_OPENED", "cash", "Jornada de caja abierta", "INFO"),
        new("CASH_SESSION_REVIEWED", "cash", "Cierre de caja revisado", "INFO"),
        new("CASH_WITHDRAWAL", "cash", "Retiro de caja", "WARNING"),
        new("CATALOG_IMPORT_APPLIED", "catalog", "Importación de catálogo aplicada", "INFO"),
        new("PRODUCT_PRICE_CHANGED", "catalog", "Precio de venta cambiado", "INFO"),
        new("PRODUCT_PRICE_SCHEDULED", "catalog", "Precio de venta programado", "INFO"),
        new("CUSTOMER_ANONYMIZED", "customers", "Datos del cliente suprimidos", "WARNING"),
        new("CUSTOMER_DATA_CORRECTED", "customers", "Datos del cliente corregidos", "INFO"),
        new("CUSTOMER_DATA_EXPORTED", "customers", "Datos del cliente exportados", "WARNING"),
        new("CUSTOMER_PRICING_ASSIGNED", "customers", "Grupo y lista de precio del cliente asignados", "INFO"),
        new("CUSTOMER_QUICK_CREATED", "customers", "Cliente creado en la caja", "INFO"),
        new("DATA_REQUEST_RECEIVED", "customers", "Solicitud de un titular recibida", "INFO"),
        new("EXPENSE_POSTED", "expenses", "Gasto registrado", "INFO"),
        new("EXPENSE_VOIDED", "expenses", "Gasto anulado", "WARNING"),
        new("LOGIN_FAILED", "identity", "Ingreso fallido", "INFO"),
        new("LOGIN_SUCCEEDED", "identity", "Ingreso al sistema", "INFO"),
        new("LOGOUT", "identity", "Salida del sistema", "INFO"),
        new("OWNER_CREATED", "identity", "Propietario creado", "WARNING"),
        new("OWNER_EMERGENCY_RESET", "identity", "Recuperación de emergencia del propietario", "CRITICAL"),
        new("PASSWORD_CHANGED", "identity", "Contraseña cambiada", "INFO"),
        new("PASSWORD_RESET", "identity", "Contraseña restablecida por un administrador", "WARNING"),
        new("PIN_CHANGED", "identity", "PIN cambiado", "INFO"),
        new("PIN_RESET", "identity", "PIN restablecido por un administrador", "WARNING"),
        new("SESSION_REVOKED", "identity", "Sesión cerrada por un administrador", "WARNING"),
        new("SUPERVISOR_AUTHORIZATION_GRANTED", "identity", "Autorización de supervisor", "INFO"),
        new("SYSTEM_ROLE_UPDATED", "identity", "Rol de sistema actualizado", "INFO"),
        new("USER_LOCKED", "identity", "Usuario bloqueado por intentos fallidos", "WARNING"),
        new("USER_SESSIONS_REVOKED", "identity", "Sesiones del usuario cerradas", "WARNING"),
        new("INVENTORY_ADJUSTMENT_POSTED", "inventory", "Ajuste de inventario contabilizado", "INFO"),
        new("INVENTORY_COUNT_POSTED", "inventory", "Conteo de inventario contabilizado", "INFO"),
        new("INVENTORY_QUICK_ADJUSTMENT", "inventory", "Ajuste rápido desde la caja", "WARNING"),
        new("INVENTORY_TRANSFER_SHORTAGE", "inventory", "Faltante en un traslado", "WARNING"),
        new("STOCK_BALANCE_REBUILT", "inventory", "Saldo reconstruido desde el kardex", "CRITICAL"),
        new("STOCK_VERIFICATION_FAILED", "inventory", "Verificación del kardex con diferencias", "CRITICAL"),
        new("LICENSE_ACTIVATED", "licensing", "Licencia activada", "INFO"),
        new("LICENSE_CHECKIN_FAILED", "licensing", "Verificación de la licencia fallida", "WARNING"),
        new("LICENSE_CLOCK_ROLLBACK", "licensing", "Reloj atrasado: licencia restringida", "CRITICAL"),
        new("LICENSE_DEACTIVATED", "licensing", "Equipo liberado de la licencia", "WARNING"),
        new("LICENSE_STATE_CHANGED", "licensing", "Cambio de estado de la licencia", "WARNING"),
        new("UPDATE_APPLIED", "system", "Actualización instalada", "INFO"),
        new("UPDATE_DOWNLOADED", "system", "Actualización descargada y verificada", "INFO"),
        new("UPDATE_FAILED", "system", "Actualización fallida", "CRITICAL"),
        new("UPDATE_INSTALL_REQUESTED", "system", "Instalación inmediata de una actualización solicitada", "WARNING"),
        new("UPDATE_ROLLED_BACK", "system", "Actualización revertida a la versión anterior", "CRITICAL"),
        new("DEVICE_PAIRING_CODE_CREATED", "organization", "Código de emparejamiento creado", "INFO"),
        new("DEVICE_PAIRING_FAILED", "organization", "Emparejamiento de equipo fallido", "WARNING"),
        new("SETUP_COMPLETED", "organization", "Configuración inicial completada", "WARNING"),
        new("PAYABLE_PAYMENT_POSTED", "purchasing", "Pago a proveedor registrado", "INFO"),
        new("PAYABLE_PAYMENT_VOIDED", "purchasing", "Pago a proveedor anulado", "WARNING"),
        new("PURCHASE_POSTED", "purchasing", "Compra contabilizada", "INFO"),
        new("PURCHASE_VOIDED", "purchasing", "Compra anulada", "WARNING"),
        new("SUPPLIER_BANK_ACCOUNT_CHANGED", "purchasing", "Cuenta bancaria de proveedor modificada", "WARNING"),
        new("SUPPLIER_BANK_ACCOUNT_CREATED", "purchasing", "Cuenta bancaria de proveedor registrada", "INFO"),
        new("SUPPLIER_BANK_ACCOUNT_VERIFIED", "purchasing", "Cuenta bancaria de proveedor verificada", "INFO"),
        new("SUPPLIER_RETURN_POSTED", "purchasing", "Devolución a proveedor contabilizada", "INFO"),
        new("REPORT_EXPORTED", "reporting", "Reporte exportado", "INFO"),
        new("SETTING_CHANGED", "settings", "Configuración modificada", "INFO"),
        new("SETTING_OVERRIDE_REMOVED", "settings", "Excepción de configuración eliminada", "INFO"),
        new("EXCHANGE_STARTED", "sales", "Cambio de mercancía iniciado", "INFO"),
        new("SALE_CANCELLED", "sales", "Venta cancelada", "WARNING"),
        new("SALE_DISCOUNT_APPLIED", "sales", "Descuento manual aplicado", "INFO"),
        new("SALE_EXPIRED_LOT_AUTHORIZED", "sales", "Venta de lote vencido autorizada", "WARNING"),
        new("SALE_LINE_VOIDED", "sales", "Línea eliminada de una venta", "INFO"),
        new("SALE_PRICE_OVERRIDDEN", "sales", "Precio cambiado en la caja", "WARNING"),
        new("SALE_REPRINTED", "sales", "Tiquete reimpreso", "INFO"),
        new("SALE_VOIDED", "sales", "Venta anulada", "WARNING"),
        new("WARRANTY_REFUND", "sales", "Reintegro de dinero por garantía", "WARNING"),
        new("CLOCK_JUMP_DETECTED", "system", "Reloj del servidor atrasado respecto a la bitácora", "CRITICAL"),
        new("DATABASE_MIGRATED", "system", "Base de datos actualizada a una nueva versión", "WARNING"),
        new("SERVER_STARTED", "system", "Servidor iniciado", "INFO"),
    ];

    public static IReadOnlyDictionary<string, AuditedEntityDefinition> Entities { get; } = new Dictionary<string, AuditedEntityDefinition>(StringComparer.Ordinal)
    {
        ["CashSession"] = new("cash", "Jornada de caja"),
        ["Denomination"] = new("cash", "Denominación"),
        ["PaymentMethod"] = new("cash", "Medio de pago"),
        ["Brand"] = new("catalog", "Marca"),
        ["Category"] = new("catalog", "Categoría"),
        ["PriceList"] = new("catalog", "Lista de precio"),
        ["Product"] = new("catalog", "Producto"),
        ["ProductBarcode"] = new("catalog", "Código de barras"),
        ["ProductPackaging"] = new("catalog", "Presentación"),
        ["ProductPrice"] = new("catalog", "Precio de producto"),
        ["ProductTax"] = new("catalog", "Impuesto de producto"),
        ["Tax"] = new("catalog", "Impuesto"),
        ["TaxRate"] = new("catalog", "Tarifa de impuesto"),
        ["VariableBarcodeRule"] = new("catalog", "Regla de código de báscula"),
        ["Customer"] = new("customers", "Cliente"),
        ["CustomerGroup"] = new("customers", "Grupo de clientes"),
        ["DataRequest"] = new("customers", "Solicitud de titular"),
        ["PrivacyPolicy"] = new("customers", "Política de datos"),
        ["Expense"] = new("expenses", "Gasto"),
        ["ExpenseCategory"] = new("expenses", "Categoría de gasto"),
        ["Employee"] = new("identity", "Empleado"),
        ["Role"] = new("identity", "Rol"),
        ["User"] = new("identity", "Usuario"),
        ["AdjustmentReason"] = new("inventory", "Motivo de ajuste"),
        ["InventoryAdjustment"] = new("inventory", "Ajuste de inventario"),
        ["InventoryCount"] = new("inventory", "Conteo de inventario"),
        ["StockPolicy"] = new("inventory", "Mínimo y máximo"),
        ["StockTransfer"] = new("inventory", "Traslado"),
        ["Branch"] = new("organization", "Sucursal"),
        ["Company"] = new("organization", "Empresa"),
        ["Device"] = new("organization", "Equipo"),
        ["Node"] = new("organization", "Nodo"),
        ["PosTerminal"] = new("organization", "Caja"),
        ["TerminalDevice"] = new("organization", "Periférico de caja"),
        ["Warehouse"] = new("organization", "Bodega"),
        ["Party"] = new("parties", "Tercero"),
        ["Promotion"] = new("promotions", "Promoción"),
        ["AccountPayable"] = new("purchasing", "Cuenta por pagar"),
        ["PayablePayment"] = new("purchasing", "Pago a proveedor"),
        ["Purchase"] = new("purchasing", "Compra"),
        ["PurchaseOrder"] = new("purchasing", "Orden de compra"),
        ["Supplier"] = new("purchasing", "Proveedor"),
        ["SupplierProduct"] = new("purchasing", "Producto del proveedor"),
        ["SupplierReturn"] = new("purchasing", "Devolución a proveedor"),
        ["SupplierSchedule"] = new("purchasing", "Días de visita del proveedor"),
        ["SupplierWithholdingDefault"] = new("purchasing", "Retención por defecto del proveedor"),
    };

    public static IReadOnlyList<AuditActionDefinition> All { get; } =
    [
        .. Explicit,
        .. Entities.SelectMany(e => new[]
        {
            new AuditActionDefinition($"{ToUpperSnake(e.Key)}_CREATED", e.Value.Module, $"{e.Value.Name} · creado", "INFO"),
            new AuditActionDefinition($"{ToUpperSnake(e.Key)}_UPDATED", e.Value.Module, $"{e.Value.Name} · modificado", "INFO"),
            new AuditActionDefinition($"{ToUpperSnake(e.Key)}_DELETED", e.Value.Module, $"{e.Value.Name} · eliminado", "WARNING"),
        }),
    ];

    private static readonly Dictionary<string, AuditActionDefinition> ByCode = All.ToDictionary(a => a.Code, StringComparer.Ordinal);

    public static AuditActionDefinition? Find(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>Nombre en español de la acción; si no está en el catálogo, el código.</summary>
    public static string NameOf(string code) => Find(code)?.Name ?? code;

    /// <summary>Igual que el interceptor: <c>ProductPrice</c> → <c>PRODUCT_PRICE</c>.</summary>
    public static string ToUpperSnake(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : char.ToUpperInvariant(c).ToString()));
}
