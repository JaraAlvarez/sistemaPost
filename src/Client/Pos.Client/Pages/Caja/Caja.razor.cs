using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Pos.Client.Services;

namespace Pos.Client.Pages.Caja;

/// <summary>
/// Pantalla de caja (docs/fases/fase-15-propuesta.md): el escáner siempre tiene el foco; las teclas de función abren los paneles; toda regla
/// (existencias, precios, promociones, autorizaciones, redondeo) la decide el servidor.
/// </summary>
public sealed partial class Caja
{
    private enum Panel
    {
        None,
        Buscar,
        Cantidad,
        Cobro,
        Suspendidas,
        Supervisor,
        Cierre,
    }

    /// <summary>Medios que asigna el sistema (crédito de un cambio, cupo del cliente, puntos): no se eligen en el cobro.</summary>
    private static readonly string[] ReservedKinds = ["EXCHANGE_CREDIT", "CUSTOMER_CREDIT", "LOYALTY_POINTS"];

    private sealed record PendingAction(ApiException Error, Func<Guid?, Task> Retry);

    private sealed record PaymentLine(Guid PaymentMethodId, decimal Amount, string? Reference);

    private DotNetObjectReference<Caja>? _self;
    private MudTextField<string>? _scanner;
    private bool _loading = true;
    private bool _busy;
    private Panel _panel;
    private CashSession? _session;
    private Sale? _sale;
    private Guid? _selected;
    private string? _scan;
    private IReadOnlyList<PaymentMethod> _methods = [];
    private IReadOnlyList<Denomination> _cashDenominations = [];
    private readonly Dictionary<Guid, int> _opening = [];
    private readonly Dictionary<Guid, int> _closing = [];
    private readonly Dictionary<Guid, decimal> _closingOthers = [];
    private string? _closingNote;
    private CashReport? _zReport;
    private string? _search;
    private List<JsonElement> _found = [];
    private decimal _quantity = 1;
    private readonly List<PaymentLine> _payments = [];
    private PaymentMethod? _methodSelected;
    private decimal _amount;
    private string? _reference;
    private string _completionKey = Guid.NewGuid().ToString();
    private Receipt? _lastReceipt;
    private decimal? _lastChange;
    private List<SaleSummary> _held = [];
    private PendingAction? _pending;
    private string? _supervisorCode, _supervisorPin, _supervisorReason;

    [Inject]
    private ApiClient Api { get; set; } = default!;

    [Inject]
    private SessionState Session { get; set; } = default!;

    [Inject]
    private AgentPrinter Printer { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    private decimal Pagado => _payments.Sum(p => p.Amount);

    /// <summary>Lo pagado más el valor escrito y aún no agregado (Enter/F10 lo toma con el medio seleccionado).</summary>
    private decimal PagadoConEscrito => Pagado + (_payments.Count == 0 && _methodSelected is not null ? _amount : 0);

    protected override async Task OnInitializedAsync()
    {
        if (!Session.IsSignedIn || !Session.IsTerminal)
        {
            _loading = false;
            return;
        }

        try
        {
            _methods = [.. (await Api.GetAsync<List<PaymentMethod>>("api/v1/cash/payment-methods")).Where(m => m.Status == "ACTIVE" && !ReservedKinds.Contains(m.Kind)).OrderBy(m => m.SortOrder)];
            _cashDenominations = [.. (await Api.GetAsync<List<Denomination>>("api/v1/cash/denominations")).Where(d => d.Status == "ACTIVE").OrderByDescending(d => d.Value)];
            _session = await OptionalAsync<CashSession>("api/v1/cash/sessions/current");
            if (_session is not null)
            {
                _sale = await OptionalAsync<Sale>("api/v1/sales/current");
                if (_session.Status == "CLOSING")
                {
                    _panel = Panel.Cierre;
                }
            }
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }

        _loading = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Session.IsTerminal)
        {
            _self = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("pos.registerKeys", _self);
        }
    }

    /// <summary>Teclas de función de la caja (D15-06), enviadas por <c>pos.js</c>.</summary>
    [JSInvokable]
    public async Task OnKey(string key)
    {
        switch (key)
        {
            case "Escape":
                await CerrarPanel();
                break;
            case "F2" when _session is not null:
                _found = [];
                _search = null;
                _panel = Panel.Buscar;
                break;
            case "F4":
                AbrirCantidad();
                break;
            case "F8":
                await SuspenderAsync();
                break;
            case "F9" when _session is not null:
                await VerSuspendidasAsync();
                break;
            case "F10" when _panel == Panel.Cobro:
                await CobrarAsync();
                break;
            case "F10":
                AbrirCobro();
                break;
            case "Delete" when _panel == Panel.None:
                await AnularLineaAsync();
                break;
        }

        StateHasChanged();
    }

    // ------------------------------------------------------------------------------------------ Venta

    private async Task EscanerTeclaAsync(KeyboardEventArgs e)
    {
        if (e.Key != "Enter" || string.IsNullOrWhiteSpace(_scan))
        {
            return;
        }

        var text = _scan.Trim();
        _scan = string.Empty;
        decimal? quantity = null;
        var star = text.IndexOf('*', StringComparison.Ordinal);
        if (star > 0 && decimal.TryParse(text[..star].Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var q))
        {
            quantity = q;
            text = text[(star + 1)..];
        }

        await AgregarAsync(new { code = text, quantity });
    }

    private async Task AgregarProductoAsync(JsonElement product)
    {
        _panel = Panel.None;
        await AgregarAsync(new { productId = product.GetProperty("id").GetGuid(), quantity = (decimal?)null });
    }

    private async Task AgregarAsync(object line)
    {
        await RunAsync(async grant =>
        {
            _sale ??= await Api.PostAsync<Sale>("api/v1/sales");
            _lastChange = null;
            if (grant is null)
            {
                try
                {
                    _sale = await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/lines", line);
                }
                catch (ApiException ex) when (ex.Code == "SALES.EXPIRED_LOT_REQUIRES_AUTHORIZATION")
                {
                    // Lote vencido (RN-SAL-18): la ruta de vencidos pide la autorización del supervisor.
                    Snackbar.Add(ex.Message, Severity.Warning);
                    _sale = await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/lines/expired", line);
                }
            }
            else
            {
                _sale = await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/lines/expired", line, grant);
            }
            _selected = _sale.ActiveLines.LastOrDefault()?.Id;
        });
    }

    private void Seleccionar(SaleLine line)
    {
        if (line.Status == "ACTIVE")
        {
            _selected = line.Id;
        }
    }

    private void AbrirCantidad()
    {
        if (_selected is null || _sale is null)
        {
            return;
        }

        _quantity = _sale.Lines.First(l => l.Id == _selected).Quantity;
        _panel = Panel.Cantidad;
    }

    private async Task CambiarCantidadAsync()
    {
        _panel = Panel.None;
        await RunAsync(async _ => _sale = await Api.PatchAsync<Sale>($"api/v1/sales/{_sale!.Id}/lines/{_selected}", new { quantity = _quantity }));
    }

    private async Task AnularLineaAsync()
    {
        if (_selected is not { } line || _sale is null)
        {
            return;
        }

        await RunAsync(async grant =>
        {
            _sale = await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/lines/{line}/void", null, grant);
            _selected = _sale.ActiveLines.LastOrDefault()?.Id;
        });
    }

    private async Task SuspenderAsync()
    {
        if (_sale is null || !_sale.ActiveLines.Any())
        {
            return;
        }

        await RunAsync(async _ =>
        {
            await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/hold", new { label = $"{_sale.ActiveLines.Count()} artículos · {DateTime.Now:HH:mm}" });
            _sale = null;
            _selected = null;
            Snackbar.Add("Venta suspendida. Recupérela con F9.", Severity.Info);
        });
    }

    private async Task VerSuspendidasAsync()
    {
        await RunAsync(async _ =>
        {
            _held = await Api.GetAsync<List<SaleSummary>>("api/v1/sales/held");
            _panel = Panel.Suspendidas;
        });
    }

    private async Task RecuperarAsync(SaleSummary held)
    {
        _panel = Panel.None;
        await RunAsync(async _ =>
        {
            _sale = await Api.PostAsync<Sale>($"api/v1/sales/{held.Id}/resume");
            _selected = _sale.ActiveLines.LastOrDefault()?.Id;
        });
    }

    private async Task CancelarVentaAsync()
    {
        if (_sale is null)
        {
            return;
        }

        await RunAsync(async grant =>
        {
            await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/cancel", new { reason = "Cancelada en la caja" }, grant);
            _sale = null;
            _selected = null;
        });
    }

    private async Task BuscarAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
        {
            _found = [];
            return;
        }

        var page = await Api.GetAsync<JsonElement>($"api/v1/catalog/products?search={Uri.EscapeDataString(text)}&status=ACTIVE&pageSize=20");
        var items = page.ValueKind == JsonValueKind.Array ? page : page.TryGetProperty("items", out var list) ? list : default;
        _found = items.ValueKind == JsonValueKind.Array ? [.. items.EnumerateArray()] : [];
    }

    // ------------------------------------------------------------------------------------------ Cobro

    private void AbrirCobro()
    {
        if (_sale is null || !_sale.ActiveLines.Any())
        {
            return;
        }

        _payments.Clear();
        _completionKey = Guid.NewGuid().ToString();
        _methodSelected = _methods.FirstOrDefault(m => m.AffectsCashDrawer) ?? (_methods.Count > 0 ? _methods[0] : null);
        _amount = _sale.AmountDue;
        _reference = null;
        _panel = Panel.Cobro;
    }

    private void AgregarPago(PaymentMethod method)
    {
        if (_methodSelected != method)
        {
            _methodSelected = method;
            _amount = Math.Max(0, _sale!.AmountDue - Pagado);
            return;
        }

        if (_amount <= 0)
        {
            return;
        }

        if (method.RequiresReference && string.IsNullOrWhiteSpace(_reference))
        {
            Snackbar.Add("Escriba la referencia del pago.", Severity.Warning);
            return;
        }

        _payments.Add(new PaymentLine(method.Id, _amount, method.RequiresReference ? _reference : null));
        _amount = Math.Max(0, _sale!.AmountDue - Pagado);
        _reference = null;
    }

    private async Task CobroTeclaAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await CobrarAsync();
        }
    }

    private async Task CobrarAsync()
    {
        if (_sale is null)
        {
            return;
        }

        // Si escribió un valor sin pulsar el medio, se toma con el medio seleccionado.
        if (_payments.Count == 0 && _methodSelected is not null && _amount > 0)
        {
            AgregarPago(_methodSelected);
        }

        if (Pagado < _sale.AmountDue)
        {
            return;
        }

        await RunAsync(async grant =>
        {
            var receipt = await Api.PostAsync<Receipt>($"api/v1/sales/{_sale.Id}/complete",
                new { payments = _payments.Select(p => new { paymentMethodId = p.PaymentMethodId, amount = p.Amount, reference = p.Reference }) }, grant, _completionKey);
            _lastReceipt = receipt;
            _lastChange = receipt.Sale.ChangeTotal;
            _sale = null;
            _selected = null;
            _panel = Panel.None;
            if (await Printer.PrintAsync(Session.Me!.PosTerminalId!.Value, receipt.Ticket) is { } problem)
            {
                Snackbar.Add(problem, Severity.Warning);
            }
        });
    }

    private async Task ReimprimirAsync()
    {
        if (_lastReceipt is null)
        {
            return;
        }

        await RunAsync(async grant =>
        {
            var receipt = await Api.PostAsync<Receipt>($"api/v1/sales/{_lastReceipt.Sale.Id}/reprint", null, grant);
            if (await Printer.PrintAsync(Session.Me!.PosTerminalId!.Value, receipt.Ticket) is { } problem)
            {
                Snackbar.Add(problem, Severity.Warning);
            }
        });
    }

    // ------------------------------------------------------------------------------------------ Jornada

    private decimal Total(Dictionary<Guid, int> count) => _cashDenominations.Sum(d => d.Value * count.GetValueOrDefault(d.Id));

    private async Task AbrirJornadaAsync()
    {
        var cash = _methods.FirstOrDefault(m => m.AffectsCashDrawer);
        if (cash is null)
        {
            Snackbar.Add("No hay un medio de pago en efectivo activo.", Severity.Error);
            return;
        }

        await RunAsync(async _ =>
        {
            _session = await Api.PostAsync<CashSession>("api/v1/cash/sessions", new
            {
                openingFloat = Total(_opening),
                openingCount = _opening.Where(c => c.Value > 0).Select(c => new { paymentMethodId = cash.Id, denominationId = c.Key, quantity = c.Value }),
            });
            Snackbar.Add($"Jornada {_session.Number} abierta.", Severity.Success);
        });
    }

    private async Task IniciarCierreAsync()
    {
        if (_sale is not null && _sale.ActiveLines.Any())
        {
            Snackbar.Add("Cobre, suspenda o cancele la venta en curso antes de cerrar.", Severity.Warning);
            return;
        }

        await RunAsync(async _ =>
        {
            _session = await Api.PostAsync<CashSession>($"api/v1/cash/sessions/{_session!.Id}/start-closing");
            _zReport = null;
            _panel = Panel.Cierre;
        });
    }

    private async Task CancelarCierreAsync()
    {
        await RunAsync(async _ =>
        {
            _session = await Api.PostAsync<CashSession>($"api/v1/cash/sessions/{_session!.Id}/cancel-closing");
            _panel = Panel.None;
        });
    }

    private async Task CerrarJornadaAsync()
    {
        var cash = _methods.First(m => m.AffectsCashDrawer);
        var count = _closing.Where(c => c.Value > 0)
            .Select(c => (object)new { paymentMethodId = cash.Id, denominationId = (Guid?)c.Key, quantity = (int?)c.Value, amount = (decimal?)null })
            .Concat(_closingOthers.Where(o => o.Value > 0)
                .Select(o => (object)new { paymentMethodId = o.Key, denominationId = (Guid?)null, quantity = (int?)null, amount = (decimal?)o.Value }))
            .ToList();
        await RunAsync(async _ =>
        {
            _zReport = await Api.PostAsync<CashReport>($"api/v1/cash/sessions/{_session!.Id}/close", new { count, differenceNote = _closingNote });
        });
    }

    // ------------------------------------------------------------------------------------------ Comunes

    private async Task CerrarPanel()
    {
        if (_panel is Panel.Cierre)
        {
            return;
        }

        _panel = Panel.None;
        _pending = null;
        await FocusScannerAsync();
    }

    private async Task SalirAsync()
    {
        try
        {
            await Api.PostAsync("api/v1/auth/logout");
        }
        catch (ApiException)
        {
            // La sesión ya no existía.
        }

        Session.SignOut();
        Navigation.NavigateTo("/ingresar");
    }

    /// <summary>Ejecuta una acción; si el servidor pide autorización de supervisor, abre el panel y la repite con la autorización.</summary>
    private async Task RunAsync(Func<Guid?, Task> action)
    {
        _busy = true;
        try
        {
            await action(null);
        }
        catch (ApiException ex) when (ex.NeedsSupervisor)
        {
            _pending = new PendingAction(ex, action);
            _supervisorCode = _supervisorPin = _supervisorReason = null;
            _panel = Panel.Supervisor;
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, ex.Status == HttpStatusCode.UnprocessableEntity ? Severity.Warning : Severity.Error);
        }
        catch (HttpRequestException)
        {
            Snackbar.Add("Se perdió la conexión con el servidor de la tienda. La venta en curso no se pierde: reintente.", Severity.Error);
        }
        finally
        {
            _busy = false;
        }

        if (_panel == Panel.None)
        {
            await FocusScannerAsync();
        }
    }

    private async Task AutorizarAsync()
    {
        if (_pending is not { } pending)
        {
            return;
        }

        _busy = true;
        try
        {
            var grant = await Api.PostAsync<JsonElement>("api/v1/auth/authorizations", new
            {
                supervisorCode = _supervisorCode, supervisorPin = _supervisorPin, permissionCode = pending.Error.Permission,
                action = pending.Error.Action, targetId = pending.Error.TargetId, targetType = "Sale",
                reason = string.IsNullOrWhiteSpace(_supervisorReason) ? "Autorizado en la caja" : _supervisorReason,
            });
            _panel = Panel.None;
            _pending = null;
            _busy = false;
            await RunAsync(_ => pending.Retry(grant.GetProperty("grantId").GetGuid()));
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<T?> OptionalAsync<T>(string path)
        where T : class
    {
        try
        {
            return await Api.GetAsync<T>(path);
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task FocusScannerAsync()
    {
        if (_scanner is not null)
        {
            await _scanner.FocusAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("pos.unregisterKeys");
        }
        catch (JSDisconnectedException)
        {
            // La página se está cerrando.
        }

        _self?.Dispose();
    }
}
