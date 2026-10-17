using Pos.SharedKernel.Text;
using System.Text.Json;
using MudBlazor;

namespace Pos.Client.Services;

/// <summary>
/// Bodegas de la sucursal del usuario (<c>api/v1/organization/branches/{id}</c>), leídas una sola vez por sesión y compartidas por las
/// pantallas de inventario y compras. Sin el permiso <c>organization.branch.view</c> (403) o sin respuesta se avisa con un mensaje y la
/// lista queda vacía: la pantalla sigue funcionando y los nombres se muestran como "—". Configuración llama a <see cref="Invalidar"/> al
/// crear, editar o inactivar una bodega.
/// </summary>
public sealed class Bodegas(ApiClient api, SessionState session, ISnackbar snackbar)
{
    private List<JsonElement> _todas = [];
    private string? _clave;

    /// <summary>Todas las bodegas de la sucursal, en el orden de la API (incluye inactivas y la de tránsito).</summary>
    public IReadOnlyList<JsonElement> Todas => _todas;

    /// <summary>Las bodegas activas, en el orden de la API.</summary>
    public IEnumerable<JsonElement> Activas => _todas.Where(b => EnumText.Same(b.Str("status"), "ACTIVE"));

    /// <summary>Las que se pueden elegir en un documento de inventario: activas, sin la de tránsito y con la sala de ventas primero.</summary>
    public IEnumerable<JsonElement> Elegibles => Activas.Where(b => !EnumText.Same(b.Str("kind"), "IN_TRANSIT")).OrderBy(b => EnumText.Same(b.Str("kind"), "SALES_FLOOR") ? 0 : 1);

    /// <summary>La bodega que se propone por defecto: la primera sala de ventas de la lista o, si no hay, la primera.</summary>
    public static Guid? Primera(IEnumerable<JsonElement> bodegas) =>
        bodegas.OrderBy(b => EnumText.Same(b.Str("kind"), "SALES_FLOOR") ? 0 : 1).Select(b => (Guid?)b.GetProperty("id").GetGuid()).FirstOrDefault();

    /// <summary>Lee las bodegas si aún no se tienen para esta sesión y sucursal; con <paramref name="recargar"/>, siempre.</summary>
    public async Task CargarAsync(bool recargar = false)
    {
        if (session.Me is not { } me)
        {
            return;
        }

        var clave = $"{me.BranchId}|{session.Token}";
        if (!recargar && clave == _clave)
        {
            return;
        }

        try
        {
            _todas = (await api.GetAsync<JsonElement>($"api/v1/organization/branches/{me.BranchId}")).Prop("warehouses").Items();
            _clave = clave;
        }
        catch (ApiException ex)
        {
            // No se guarda en memoria: la próxima pantalla lo vuelve a intentar.
            _todas = [];
            _clave = null;
            snackbar.Add($"No se pudieron cargar las bodegas: {ex.Texto}", Severity.Warning);
        }
    }

    /// <summary>Olvida lo leído: la próxima <see cref="CargarAsync"/> vuelve a consultar la API.</summary>
    public void Invalidar() => _clave = null;

    /// <summary>"CÓDIGO · Nombre" de la bodega, o "—" si no está en la lista.</summary>
    public string Nombre(Guid id) => Nombre(id.ToString());

    /// <summary>"CÓDIGO · Nombre" de la bodega con ese id (como lo da la API), o "—" si no está en la lista.</summary>
    public string Nombre(string id)
    {
        var bodega = _todas.FirstOrDefault(b => string.Equals(b.Str("id"), id, StringComparison.OrdinalIgnoreCase));
        return bodega.ValueKind == JsonValueKind.Object ? Etiqueta(bodega) : "—";
    }

    /// <summary>"CÓDIGO · Nombre" de una bodega de la lista (para los <c>MudSelectItem</c>).</summary>
    public static string Etiqueta(JsonElement bodega) => $"{bodega.Str("code")} · {bodega.Str("name")}";
}
