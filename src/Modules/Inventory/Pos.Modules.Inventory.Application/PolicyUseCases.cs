using System.Text;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Files;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.Modules.Organization.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Inventory.Application;

// ─────────────────────── Reabastecimiento: mínimos y máximos (consultar, importar, calcular) ───────────────────────

/// <summary>Guarda (crea o actualiza) la política de un producto en una bodega. Los cambios quedan en la auditoría de la entidad.</summary>
public sealed class StockPolicyWriter(IInventoryStore store, IIdGenerator ids)
{
    /// <summary>
    /// Crea o actualiza. <paramref name="existing"/> es la política vigente si ya se leyó. Devuelve qué pasó: "CREATE", "UPDATE" o "UNCHANGED".
    /// </summary>
    public Result<string> Upsert(
        StockPolicy? existing, Guid companyId, WarehouseInfo warehouse, Guid productId, decimal min, decimal? max, decimal? reorderPoint, decimal? reorderQty)
    {
        if (existing is null)
        {
            var created = StockPolicy.Create(ids.NewId(), companyId, warehouse.BranchId, warehouse.Id, productId, min, max, reorderPoint, reorderQty);
            if (created.IsFailure)
            {
                return created.Error;
            }

            store.Add(created.Value);
            return "CREATE";
        }

        if (existing.MinQty == min && existing.MaxQty == max && existing.ReorderPoint == reorderPoint && existing.ReorderQty == reorderQty)
        {
            return "UNCHANGED";
        }

        var updated = existing.Update(min, max, reorderPoint, reorderQty);
        return updated.IsSuccess ? "UPDATE" : updated.Error;
    }
}

/// <summary>Mínimos y máximos de un producto en las bodegas de la sucursal (pestaña Existencias del producto).</summary>
public sealed record ListStockPoliciesQuery(Guid ProductId) : IQuery<IReadOnlyList<StockPolicyDto>>;

internal sealed class ListStockPoliciesHandler(IInstallationContext installation, IInventoryStore store)
    : IQueryHandler<ListStockPoliciesQuery, IReadOnlyList<StockPolicyDto>>
{
    public async Task<Result<IReadOnlyList<StockPolicyDto>>> Handle(ListStockPoliciesQuery request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        return (await store.GetPoliciesAsync([request.ProductId], cancellationToken))
            .Where(p => p.BranchId == local.Value.BranchId)
            .Select(p => new StockPolicyDto(p.Id, p.WarehouseId, p.ProductId, p.MinQty, p.MaxQty, p.ReorderPoint, p.ReorderQty))
            .ToList();
    }
}

/// <summary>Columnas de la plantilla de mínimos y máximos (encabezados normalizados).</summary>
public static class StockPolicyImportColumns
{
    public static readonly string[] ProductColumns = ["sku", "codigo_barras", "producto", "codigo"];

    public static readonly string[] Columns = ["sku", "bodega", "minimo", "maximo", "punto_pedido"];

    public static string Template()
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(';', Columns));
        builder.AppendLine("LECHE-ENT-1100;;12;48;");
        builder.AppendLine("7702177000014;;6;24;10");
        return builder.ToString();
    }
}

/// <summary>
/// "Importar mínimos y máximos" desde Excel o CSV (columnas sku o codigo_barras, minimo, y opcionales maximo, punto_pedido y bodega).
/// Con <c>Apply</c> = false solo valida y muestra la vista previa fila por fila; con <c>Apply</c> = true vuelve a validar y, solo si
/// ninguna fila tiene errores, guarda todo en una transacción. Una celda vacía de maximo o punto_pedido conserva el valor actual.
/// </summary>
public sealed record ImportStockPoliciesCommand(Guid? WarehouseId, string FileName, Stream Content, bool Apply) : ICommand<StockPolicyImportDto>;

internal sealed class ImportStockPoliciesHandler(
    IInstallationContext installation,
    ITabularFileReader reader,
    IInventoryStore store,
    ICatalogReader catalog,
    IWarehouseDirectory warehouses,
    StockPolicyWriter writer,
    IAuditWriter audit,
    IIdGenerator ids) : ICommandHandler<ImportStockPoliciesCommand, StockPolicyImportDto>
{
    public const int MaxRows = 5000;

    public async Task<Result<StockPolicyImportDto>> Handle(ImportStockPoliciesCommand request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        var table = await reader.ReadAsync(request.Content, request.FileName, MaxRows, cancellationToken);
        if (table.IsFailure)
        {
            return table.Error;
        }

        var headers = table.Value.Headers;
        var productColumn = StockPolicyImportColumns.ProductColumns.FirstOrDefault(headers.Contains);
        if (productColumn is null || !headers.Contains("minimo"))
        {
            return InventoryErrors.PolicyImportMissingColumns;
        }

        var branchWarehouses = (await warehouses.ListByBranchAsync(local.Value.BranchId, cancellationToken))
            .Where(w => w.IsActive && w.Kind != "IN_TRANSIT").ToList();
        WarehouseInfo? fallback = null;
        if (request.WarehouseId is { } chosen)
        {
            fallback = branchWarehouses.FirstOrDefault(w => w.Id == chosen);
            if (fallback is null)
            {
                return InventoryErrors.WarehouseNotLocal;
            }
        }
        else if (!headers.Contains("bodega"))
        {
            return InventoryErrors.PolicyImportNeedsWarehouse;
        }

        // Productos del archivo (por SKU o código de barras, en una sola consulta) y sus políticas actuales.
        var products = await catalog.FindByCodesAsync(
            [.. table.Value.Rows.Select(r => r.Get(productColumn)).OfType<string>().Distinct(StringComparer.Ordinal)], cancellationToken);
        var policies = (await store.GetPoliciesAsync([.. products.Values.Select(p => p.Id).Distinct()], cancellationToken))
            .ToDictionary(p => (p.WarehouseId, p.ProductId));

        var seen = new Dictionary<(Guid, Guid), int>();
        var analyzed = new List<(StockPolicyImportRowDto Dto, WarehouseInfo? Warehouse, decimal? ReorderQty)>();
        foreach (var row in table.Value.Rows)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var code = row.Get(productColumn);
            var product = code is null ? null : products.GetValueOrDefault(code);
            if (code is null)
            {
                errors.Add($"{productColumn}: obligatorio.");
            }
            else if (product is null)
            {
                errors.Add($"{productColumn}: no se encontró el producto '{code}'.");
            }
            else if (!product.IsStockable)
            {
                errors.Add("El producto es un servicio: no maneja existencias.");
            }
            else if (product.Status == "DISCONTINUED")
            {
                errors.Add("El producto está descontinuado: ya no se compra ni se repone.");
            }
            else if (product.Status == "INACTIVE")
            {
                warnings.Add("El producto está inactivo: no aparecerá en la caja hasta activarlo.");
            }

            var warehouseCode = row.Get("bodega");
            var warehouse = warehouseCode is null
                ? fallback
                : branchWarehouses.FirstOrDefault(w => string.Equals(w.Code, warehouseCode, StringComparison.OrdinalIgnoreCase));
            if (warehouse is null)
            {
                errors.Add(warehouseCode is null
                    ? "bodega: escriba el código de la bodega o elíjala antes de subir el archivo."
                    : $"bodega: '{warehouseCode}' no es una bodega activa de esta sucursal.");
            }

            var min = ParseQuantity(row, "minimo", required: true, product, errors);
            var maxCell = ParseQuantity(row, "maximo", required: false, product, errors);
            var reorderCell = ParseQuantity(row, "punto_pedido", required: false, product, errors);

            StockPolicy? current = null;
            if (product is not null && warehouse is not null)
            {
                if (seen.TryGetValue((warehouse.Id, product.Id), out var firstRow))
                {
                    errors.Add($"El producto está repetido para la misma bodega (fila {firstRow}).");
                }
                else
                {
                    seen[(warehouse.Id, product.Id)] = row.Number;
                }

                current = policies.GetValueOrDefault((warehouse.Id, product.Id));
            }

            // Celda vacía = conserva lo que ya tiene.
            var max = maxCell ?? current?.MaxQty;
            var reorderPoint = reorderCell ?? current?.ReorderPoint;
            if (min is { } m && errors.Count == 0
                && StockPolicy.Create(Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, m, max, reorderPoint, current?.ReorderQty) is
                    { IsFailure: true } invalid)
            {
                errors.Add(max < m ? "maximo: debe ser mayor o igual que el mínimo."
                    : invalid.Error == InventoryErrors.ReorderPointNeedsTarget ? "punto_pedido: indique también el máximo (o la cantidad a pedir en la ficha)."
                    : invalid.Error.Message);
            }

            var action = errors.Count > 0 ? "ERROR"
                : current is null ? "CREATE"
                : current.MinQty == min && current.MaxQty == max && current.ReorderPoint == reorderPoint ? "UNCHANGED"
                : "UPDATE";
            analyzed.Add((
                new StockPolicyImportRowDto(row.Number, action, code, product?.Id, product?.Name, warehouse?.Code, min, max, reorderPoint,
                    current?.MinQty, current?.MaxQty, errors, warnings),
                warehouse, current?.ReorderQty));
        }

        var rows = analyzed.Select(a => a.Dto).ToList();
        var errorRows = rows.Count(r => r.Action == "ERROR");
        var applied = false;
        if (request.Apply && errorRows == 0 && rows.Count > 0)
        {
            foreach (var (dto, warehouse, reorderQty) in analyzed.Where(a => a.Dto.Action is "CREATE" or "UPDATE"))
            {
                var saved = writer.Upsert(policies.GetValueOrDefault((warehouse!.Id, dto.ProductId!.Value)), local.Value.CompanyId, warehouse,
                    dto.ProductId!.Value, dto.MinQuantity!.Value, dto.MaxQuantity, dto.ReorderPoint, reorderQty);
                if (saved.IsFailure)
                {
                    return saved.Error;
                }
            }

            applied = true;
            await audit.WriteAsync(
                new AuditEntry("inventory", "STOCK_POLICIES_IMPORTED", "StockPolicy", ids.NewId(), Path.GetFileName(request.FileName),
                    $"Mínimos y máximos importados de {Path.GetFileName(request.FileName)}: {rows.Count(r => r.Action == "CREATE")} nuevos, "
                    + $"{rows.Count(r => r.Action == "UPDATE")} cambiados.",
                    NewValues: new Dictionary<string, object?>
                    {
                        ["file"] = Path.GetFileName(request.FileName),
                        ["created"] = rows.Count(r => r.Action == "CREATE"),
                        ["updated"] = rows.Count(r => r.Action == "UPDATE"),
                    }),
                cancellationToken);
        }

        return new StockPolicyImportDto(
            Path.GetFileName(request.FileName), applied, rows.Count, rows.Count(r => r.Action == "CREATE"), rows.Count(r => r.Action == "UPDATE"),
            rows.Count(r => r.Action == "UNCHANGED"), errorRows, rows);
    }

    private static decimal? ParseQuantity(TabularRow row, string column, bool required, CatalogProductInfo? product, List<string> errors)
    {
        var text = row.Get(column);
        if (text is null)
        {
            if (required)
            {
                errors.Add($"{column}: obligatorio.");
            }

            return null;
        }

        if (!DecimalParsing.TryParse(text, out var value) || value < 0m)
        {
            errors.Add($"{column}: escriba un número mayor o igual a cero.");
            return null;
        }

        if (product is { AllowsDecimalQuantity: false } && decimal.Truncate(value) != value)
        {
            errors.Add($"{column}: el producto se maneja en unidades enteras.");
            return null;
        }

        return value;
    }
}

/// <summary>
/// "Calcular mínimos sugeridos": venta neta de los últimos <c>Days</c> días en la bodega ÷ días = venta diaria; mínimo = venta diaria ×
/// días de entrega; máximo = venta diaria × (días de entrega + días de cobertura). Solo muestra: nada se guarda hasta aplicar.
/// </summary>
public sealed record SuggestMinimumsQuery(Guid WarehouseId, int Days, int LeadTimeDays, int CoverageDays) : IQuery<MinimumSuggestionsDto>;

internal sealed class SuggestMinimumsHandler(InventoryGuards guards, IInventoryReadModel readModel, IInventoryStore store, IClock clock)
    : IQueryHandler<SuggestMinimumsQuery, MinimumSuggestionsDto>
{
    public const int MaxRows = 5000;

    public async Task<Result<MinimumSuggestionsDto>> Handle(SuggestMinimumsQuery request, CancellationToken cancellationToken)
    {
        if (request.Days is < 1 or > MinimumSuggestion.MaxDays || request.LeadTimeDays is < 1 or > 180 || request.CoverageDays is < 0 or > 180)
        {
            return InventoryErrors.InvalidSuggestionParameters;
        }

        var warehouse = await guards.LocalWarehouseAsync(request.WarehouseId, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error;
        }

        // Los últimos N días completos (hoy no cuenta: el día no ha terminado).
        var to = clock.Today.AddDays(-1);
        var from = clock.Today.AddDays(-request.Days);
        var sales = await readModel.GetNetSalesAsync(request.WarehouseId, from, to, MaxRows + 1, cancellationToken);
        var truncated = sales.Count > MaxRows;
        sales = [.. sales.Take(MaxRows)];
        var policies = (await store.GetPoliciesAsync([.. sales.Select(s => s.ProductId)], cancellationToken))
            .Where(p => p.WarehouseId == request.WarehouseId)
            .ToDictionary(p => p.ProductId);
        return new MinimumSuggestionsDto([.. sales.Select(s =>
        {
            var (average, min, max) = MinimumSuggestion.Calculate(s.SoldQuantity, request.Days, request.LeadTimeDays, request.CoverageDays, s.AllowsDecimalQuantity);
            var current = policies.GetValueOrDefault(s.ProductId);
            return new MinimumSuggestionDto(
                warehouse.Value.Id, warehouse.Value.Code, s.ProductId, s.Sku, s.ProductName, s.BaseUnitCode, s.SoldQuantity, average,
                current?.MinQty, current?.MaxQty, min, max);
        })], truncated);
    }
}

/// <summary>
/// Guarda los mínimos y máximos elegidos (aplicar los sugeridos). Conserva el punto de pedido y la cantidad a pedir que ya tengan; un
/// máximo vacío conserva el máximo actual. Un producto que no se puede guardar (por ejemplo, con punto de pedido quedaría sin máximo ni
/// cantidad a pedir) se rechaza con su SKU en <c>Rejected</c> y los demás sí se guardan: nunca se quita el punto de pedido en silencio.
/// </summary>
public sealed record ApplyMinimumsCommand(IReadOnlyList<MinMaxInput> Items) : ICommand<StockPolicyBulkResultDto>;

internal sealed class ApplyMinimumsHandler(
    IInstallationContext installation, InventoryGuards guards, IInventoryStore store, StockPolicyWriter writer, IAuditWriter audit, IIdGenerator ids)
    : ICommandHandler<ApplyMinimumsCommand, StockPolicyBulkResultDto>
{
    public async Task<Result<StockPolicyBulkResultDto>> Handle(ApplyMinimumsCommand request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        var items = request.Items ?? [];
        if (items.Count == 0)
        {
            return InventoryErrors.NothingToApply;
        }

        if (items.GroupBy(i => (i.WarehouseId, i.ProductId)).Any(g => g.Count() > 1))
        {
            return InventoryErrors.DuplicatedProduct;
        }

        var products = await guards.StockableAsync(
            [.. items.SelectMany(i => new[] { (i.ProductId, i.MinQuantity), (i.ProductId, i.MaxQuantity ?? 0m) })], cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        var policies = (await store.GetPoliciesAsync([.. items.Select(i => i.ProductId)], cancellationToken)).ToDictionary(p => (p.WarehouseId, p.ProductId));
        var warehouses = new Dictionary<Guid, WarehouseInfo>();
        int created = 0, updated = 0, unchanged = 0;
        var rejected = new List<string>();
        foreach (var item in items)
        {
            if (!warehouses.TryGetValue(item.WarehouseId, out var warehouse))
            {
                var found = await guards.LocalWarehouseAsync(item.WarehouseId, cancellationToken);
                if (found.IsFailure)
                {
                    return found.Error;
                }

                warehouses[item.WarehouseId] = warehouse = found.Value;
            }

            var current = policies.GetValueOrDefault((item.WarehouseId, item.ProductId));
            var saved = writer.Upsert(current, local.Value.CompanyId, warehouse, item.ProductId, item.MinQuantity, item.MaxQuantity ?? current?.MaxQty,
                current?.ReorderPoint, current?.ReorderQty);
            if (saved.IsFailure)
            {
                var sku = products.Value.TryGetValue(item.ProductId, out var product) ? product.Sku : item.ProductId.ToString("D");
                rejected.Add($"{sku} ({warehouse.Code}): {saved.Error.Message}");
                continue;
            }

            _ = saved.Value switch
            {
                "CREATE" => created++,
                "UPDATE" => updated++,
                _ => unchanged++,
            };
        }

        if (created + updated > 0)
        {
            await audit.WriteAsync(
                new AuditEntry("inventory", "STOCK_POLICIES_SUGGESTED", "StockPolicy", ids.NewId(), "Mínimos sugeridos",
                    $"Se guardaron mínimos y máximos calculados por la venta: {created} nuevos, {updated} cambiados"
                    + (rejected.Count > 0 ? $", {rejected.Count} rechazados." : "."),
                    NewValues: new Dictionary<string, object?>
                    {
                        ["created"] = created, ["updated"] = updated, ["products"] = items.Count, ["rejected"] = rejected.ToArray(),
                    }),
                cancellationToken);
        }

        return new StockPolicyBulkResultDto(created, updated, unchanged, rejected);
    }
}
