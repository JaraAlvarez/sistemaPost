using MudBlazor;

namespace Pos.Client.Services;

/// <summary>Opción del menú de la administración: título, ruta, icono y quién la ve (cada opción aparece solo con su permiso, D17-04).</summary>
public sealed record OpcionMenu(string Titulo, string Ruta, string Icono, Func<SessionState, bool> Visible);

/// <summary>Grupo del menú lateral.</summary>
public sealed record GrupoMenu(string Titulo, string Icono, IReadOnlyList<OpcionMenu> Opciones)
{
    public IEnumerable<OpcionMenu> Visibles(SessionState session) => Opciones.Where(o => o.Visible(session));
}

/// <summary>
/// Mapa único de la administración: lo usan el menú lateral y las migas de pan de cada pantalla (docs/guia-diseno.md §5). Agregar una
/// pantalla aquí la pone en ambos lugares.
/// </summary>
public static class Navegacion
{
    public const string Inicio = "/admin";

    private static bool Reportes(SessionState s) => s.Me?.Permissions.Any(p => p.StartsWith("reporting.", StringComparison.Ordinal)) == true;

    public static readonly IReadOnlyList<GrupoMenu> Grupos =
    [
        new("Ventas y caja", Icons.Material.Outlined.PointOfSale,
        [
            new("Jornadas de caja", "/admin/jornadas", Icons.Material.Outlined.EventNote, s => s.Has("cash.report.view")),
            new("Gastos", "/admin/gastos", Icons.Material.Outlined.Payments, s => s.Has("expenses.expense.view")),
            new("Reportes", "/admin/reportes", Icons.Material.Outlined.Assessment, Reportes),
            new("Facturación electrónica", "/admin/facturacion", Icons.Material.Outlined.ReceiptLong, s => s.Has("billing.document.view") || s.Has("billing.settings.manage")),
        ]),
        new("Inventario", Icons.Material.Outlined.Inventory2,
        [
            new("Productos y precios", "/admin/productos", Icons.Material.Outlined.Sell, s => s.Has("catalog.product.view")),
            new("Existencias", "/admin/existencias", Icons.Material.Outlined.Warehouse, s => s.Has("inventory.stock.view")),
            new("Conteos", "/admin/conteos", Icons.Material.Outlined.FactCheck, s => s.Has("inventory.count.register")),
            new("Traslados", "/admin/traslados", Icons.Material.Outlined.LocalShipping, s => s.Has("inventory.stock.view")),
            new("Ajustes", "/admin/ajustes", Icons.Material.Outlined.Tune, s => s.Has("inventory.stock.view")),
            new("Importar desde Excel", "/admin/importar", Icons.Material.Outlined.UploadFile, s => s.Has("catalog.import.run") || s.Has("inventory.adjustment.manage")),
        ]),
        new("Compras y proveedores", Icons.Material.Outlined.ShoppingCart,
        [
            new("Compras", "/admin/compras", Icons.Material.Outlined.ShoppingCart, s => s.Has("purchasing.purchase.view")),
            new("Proveedores", "/admin/proveedores", Icons.Material.Outlined.Store, s => s.Has("purchasing.purchase.view")),
            new("Cuentas por pagar", "/admin/cuentas-por-pagar", Icons.Material.Outlined.AccountBalance, s => s.Has("purchasing.payable.view")),
        ]),
        new("Clientes y promociones", Icons.Material.Outlined.Groups,
        [
            new("Clientes", "/admin/clientes", Icons.Material.Outlined.Groups, s => s.Has("customers.customer.view")),
            new("Promociones", "/admin/promociones", Icons.Material.Outlined.LocalOffer, s => s.Has("promotions.promotion.view")),
        ]),
        new("Administración", Icons.Material.Outlined.AdminPanelSettings,
        [
            new("Usuarios", "/admin/usuarios", Icons.Material.Outlined.People, s => s.Has("identity.user.view")),
            new("Configuración", "/admin/configuracion", Icons.Material.Outlined.Settings, s => s.Has("organization.company.view") || s.Has("organization.branch.view") || s.Has("settings.setting.view")),
            new("Cajas e impresoras", "/admin/cajas", Icons.Material.Outlined.Print, s => s.Has("organization.branch.view")),
            new("Auditoría", "/admin/auditoria", Icons.Material.Outlined.Policy, s => s.Has("audit.log.view")),
            new("Licencia", "/admin/licencia", Icons.Material.Outlined.VerifiedUser, s => s.Has("licensing.license.view")),
            new("Sincronización", "/admin/sincronizacion", Icons.Material.Outlined.CloudSync, s => s.Has("sync.sync.view")),
            new("Backups", "/admin/backups", Icons.Material.Outlined.Backup, s => s.Has("backup.backup.view")),
            new("Actualizaciones", "/admin/actualizaciones", Icons.Material.Outlined.SystemUpdate, s => s.Has("system.update.view")),
        ]),
    ];

    /// <summary>Grupo y opción de una ruta (sin la consulta), o nulos si la ruta no está en el menú.</summary>
    public static (GrupoMenu? Grupo, OpcionMenu? Opcion) Buscar(string ruta)
    {
        var path = "/" + ruta.Split('?', '#')[0].Trim('/');
        foreach (var grupo in Grupos)
        {
            foreach (var opcion in grupo.Opciones)
            {
                if (string.Equals(opcion.Ruta, path, StringComparison.OrdinalIgnoreCase))
                {
                    return (grupo, opcion);
                }
            }
        }

        return (null, null);
    }
}
