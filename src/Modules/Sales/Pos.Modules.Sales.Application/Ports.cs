using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Application;

/// <summary>Ventas y cambios de mercancía (EF Core).</summary>
public interface ISalesStore
{
    void Add(Sale sale);

    void Add(CustomerReturn customerReturn);

    Task<Sale?> GetSaleAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Bloquea la venta (FOR UPDATE) y la carga: serializa cobros, anulaciones y cambios concurrentes.</summary>
    Task<Sale?> LockSaleAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Venta en curso (OPEN) de la caja.</summary>
    Task<Sale?> GetOpenSaleAsync(Guid posTerminalId, CancellationToken cancellationToken);

    Task<int> CountHeldAsync(Guid posTerminalId, CancellationToken cancellationToken);

    Task<CustomerReturn?> GetReturnAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> HasDraftReturnAsync(Guid originalSaleId, CancellationToken cancellationToken);
}

/// <summary>Encabezado del tiquete: empresa, sucursal, caja y cajero.</summary>
public sealed record TicketHeader(
    string LegalName, string TradeName, string Nit, string? Address, string? Phone, string BranchName, string? BranchAddress, string TerminalCode, string CashierName);

public sealed record SaleFilter(
    Guid BranchId, DateOnly? From, DateOnly? To, string? Status, string? Number, Guid? PosTerminalId, Guid? CashSessionId, int Limit, Guid? CustomerId = null);

/// <summary>Lecturas de pantalla y del tiquete (cruza con org e identity para mostrar nombres).</summary>
public interface ISalesReadModel
{
    Task<TicketHeader?> GetTicketHeaderAsync(Guid posTerminalId, Guid cashierId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SaleSummaryDto>> ListSalesAsync(SaleFilter filter, CancellationToken cancellationToken);

    Task<IReadOnlyList<SaleSummaryDto>> ListHeldAsync(Guid posTerminalId, CancellationToken cancellationToken);
}

/// <summary>Configuraciones de ventas. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class SalesSettings
{
    /// <summary>D7-09 (pregunta 3): múltiplo al que se redondea la parte pagada en efectivo (0 = cobro exacto).</summary>
    public static readonly SettingDefinition<decimal> CashRoundingIncrement = new(
        "sales.cash_rounding_increment",
        50m,
        SettingScope.Company | SettingScope.Branch,
        "Múltiplo al que se redondea la parte pagada en efectivo (0 = sin redondeo). La diferencia queda como ajuste de redondeo.",
        v => v is >= 0m and <= 1_000m ? null : "Entre 0 y 1.000.");

    /// <summary>Escanear dos veces el mismo producto suma la cantidad en la misma línea.</summary>
    public static readonly SettingDefinition<bool> MergeSameProduct = new(
        "sales.merge_same_product", true, SettingScope.Company | SettingScope.Branch | SettingScope.Terminal,
        "Escanear de nuevo el mismo producto (misma presentación y precio) suma la cantidad en la misma línea.");

    /// <summary>RN-SAL-07: máximo de ventas suspendidas por caja.</summary>
    public static readonly SettingDefinition<int> MaxHeldPerTerminal = new(
        "sales.max_held_per_terminal", 10, SettingScope.Company | SettingScope.Branch, "Máximo de ventas suspendidas por caja.",
        v => v is >= 1 and <= 50 ? null : "Entre 1 y 50.");

    /// <summary>RN-RET-03: plazo en días para un cambio de mercancía.</summary>
    public static readonly SettingDefinition<int> ExchangeDays = new(
        "sales.exchange_days", 30, SettingScope.Company | SettingScope.Branch, "Días (desde la venta) en que se aceptan cambios de mercancía.",
        v => v is >= 0 and <= 365 ? null : "Entre 0 y 365.");

    /// <summary>RN-SAL-13: total desde el cual la venta debe identificar al cliente (0 = sin tope).</summary>
    public static readonly SettingDefinition<decimal> AnonymousSaleLimit = new(
        "sales.anonymous_sale_limit", 0m, SettingScope.Company,
        "Total a partir del cual una venta a Consumidor final exige identificar al cliente (0 = sin tope).",
        v => v >= 0m ? null : "Debe ser mayor o igual a cero.");

    public static IEnumerable<SettingDefinition> All => [CashRoundingIncrement, MergeSameProduct, MaxHeldPerTerminal, ExchangeDays, AnonymousSaleLimit];
}

public sealed class SalesSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => SalesSettings.All;
}

public sealed class SalesPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => SalesPermissions.All;
}

/// <summary>Errores de la capa de aplicación (combinan la venta con la caja, el catálogo o la configuración).</summary>
internal static class SalesAppErrors
{
    public static readonly Error SetupRequired = Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");

    public static readonly Error ExchangeInProgress = Error.Conflict(
        "SALES.EXCHANGE_IN_PROGRESS", "La venta ya tiene un cambio en curso: cóbrelo o cancélelo antes de iniciar otro.");

    public static readonly Error ExchangeSaleNotVoidable = Error.BusinessRule(
        "SALES.VOID_NOT_ALLOWED", "Una venta pagada con el crédito de un cambio no se anula: el cambio ya se registró.");

    public static readonly Error CodeNotFound = Error.NotFound("SALES.CODE_NOT_FOUND", "El código no corresponde a ningún producto.");

    public static readonly Error DamagedWarehouseMissing = Error.BusinessRule(
        "SALES.DAMAGED_WAREHOUSE_MISSING", "La sucursal no tiene bodega de averías para recibir la mercancía.");
}

internal static class SalesMapping
{
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
