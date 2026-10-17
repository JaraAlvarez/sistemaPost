using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Pos.Client.Services;
using Pos.SharedKernel.Text;
using SistecreditoPaymentDto = Pos.Modules.Cash.Contracts.SistecreditoPaymentDto;

namespace Pos.Client.Pages.Caja;

/// <summary>
/// Pantalla de caja (docs/fases/fase-15-propuesta.md, docs/guia-diseno.md §7): el escáner siempre tiene el foco; las teclas de función
/// abren los paneles; toda regla (existencias, precios, promociones, descuentos, autorizaciones, redondeo) la decide el servidor.
/// </summary>
public sealed partial class Caja
{
    private enum Panel
    {
        None,
        Cantidad,
        Cobro,
        Suspendidas,
        Supervisor,
        Cierre,
        Cliente,
        Descuento,
        CancelarVenta,
        Ayuda,
        Consulta,

        /// <summary>Leyendo la báscula para un producto "Se vende por báscula" (Fase 7-C).</summary>
        Pesando,

        /// <summary>La báscula no responde: el cajero digita el peso (con permiso o supervisor).</summary>
        PesoDigitado,
    }

    /// <summary>Medios que asigna el sistema (crédito de un cambio, cupo del cliente, puntos): no se eligen en el cobro.</summary>
    private static readonly string[] ReservedKinds = ["EXCHANGE_CREDIT", "CUSTOMER_CREDIT", "LOYALTY_POINTS"];

    /// <summary>Billetes para el cobro rápido en efectivo (se proponen los que cubren el total).</summary>
    private static readonly decimal[] Billetes = [5_000m, 10_000m, 20_000m, 50_000m, 100_000m];

    private static readonly TimeSpan IntervaloConexion = TimeSpan.FromSeconds(15);

    /// <summary>Cada cuánto la caja pregunta por el cobro de Sistecrédito mientras el cliente confirma en su celular.</summary>
    private static readonly TimeSpan IntervaloSistecredito = TimeSpan.FromSeconds(3);

    /// <summary>Tipos de documento que acepta Sistecrédito en la caja (D7B-07).</summary>
    private static readonly string[] TiposDocumento = ["CC", "CE", "NIT", "PA", "TI"];

    private sealed record PendingAction(ApiException Error, Func<Guid?, Task> Retry);

    private sealed record PaymentLine(Guid PaymentMethodId, decimal Amount, string? Reference);

    private readonly CancellationTokenSource _stop = new();
    private DotNetObjectReference<Caja>? _self;
    private MudTextField<string>? _scanner;
    private bool _loading = true;
    private bool _enfocado;
    private bool _busy;
    private bool _offline;
    private DateTime _ahora = DateTime.Now;
    private Panel _panel;
    private CashSession? _session;
    private Sale? _sale;
    private Guid? _selected;
    private Guid? _nueva;
    private string? _scan;
    private IReadOnlyList<PaymentMethod> _methods = [];
    private IReadOnlyList<Denomination> _cashDenominations = [];
    private readonly Dictionary<Guid, int> _opening = [];
    private readonly Dictionary<Guid, int> _closing = [];
    private readonly Dictionary<Guid, decimal> _closingOthers = [];
    private string? _closingNote;
    private CashReport? _zReport;

    /// <summary>Productos que coinciden con el nombre escrito en el escáner (lista bajo el campo) y el elegido con las flechas.</summary>
    private List<JsonElement> _sugerencias = [];
    private int _sugerida = -1;
    private CancellationTokenSource? _sugerirCts;
    private decimal _quantity = 1;
    private readonly List<PaymentLine> _payments = [];
    private PaymentMethod? _methodSelected;
    private decimal _amount;
    private string? _reference;
    private string _completionKey = Guid.NewGuid().ToString();

    // Fase 7-B: cobro con Sistecrédito en curso.
    private string _scTipo = "CC";
    private string? _scDocumento;
    private SistecreditoPaymentDto? _sc;
    private string? _scQr;
    private CancellationTokenSource? _scCts;
    private Receipt? _lastReceipt;
    private decimal? _lastChange;
    private List<SaleSummary> _held = [];
    private int _heldCount;
    private PendingAction? _pending;
    private string? _supervisorCode, _supervisorPin, _supervisorReason;
    private string? _customerSearch, _customerError;
    private List<JsonElement> _customers = [];
    private bool _descuentoVenta;
    private MudTextField<string>? _consultaCampo;
    private string? _consulta, _consultaError;
    private List<JsonElement> _consultaLista = [];
    private int _consultaSel = -1;
    private CancellationTokenSource? _consultaCts;
    private ScanResult? _consultado;
    private decimal? _consultaExistencias;
    private bool _descuentoPorcentaje = true;
    private decimal _descuentoValor;
    private string? _descuentoMotivo;

    // Fase 7-C: lectura automática de la báscula al registrar un producto de báscula.
    private ScanResult? _pesoProducto;
    private Guid _pesoProductoId;
    private string _pesoMensaje = string.Empty;
    private string _pesoTono = "info";
    private ScaleRead? _pesoLectura;
    private CancellationTokenSource? _pesoCts;
    private decimal _pesoDigitado;
    private bool _pesoRetirar;

    [Inject]
    private ApiClient Api { get; set; } = default!;

    [Inject]
    private SessionState Session { get; set; } = default!;

    [Inject]
    private AgentPrinter Printer { get; set; } = default!;

    [Inject]
    private AgentScale Scale { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    private decimal Pagado => _payments.Sum(p => p.Amount);

    /// <summary>Lo pagado más el valor escrito y aún no agregado (Enter/F10 lo toma con el medio seleccionado).</summary>
    private decimal PagadoConEscrito => Pagado + (_payments.Count == 0 && _methodSelected is not null && !EsSistecredito(_methodSelected) ? _amount : 0);

    private bool HayLineas => _sale is not null && _sale.ActiveLines.Any();

    private SaleLine? LineaSeleccionada => _sale?.Lines.FirstOrDefault(l => l.Id == _selected);

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
            if (Session.Me?.PosTerminalId is { } terminalId && await Scale.LoadAsync(terminalId) is { } avisoBascula)
            {
                Snackbar.Add($"Báscula: {avisoBascula} Mientras tanto se puede digitar el peso.", Severity.Warning);
            }

            if (_session is not null)
            {
                _sale = await OptionalAsync<Sale>("api/v1/sales/current");
                _selected = _sale?.ActiveLines.LastOrDefault()?.Id;
                await ContarSuspendidasAsync();
                if (_session.Status == "CLOSING")
                {
                    _panel = Panel.Cierre;
                }
            }
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Texto, Severity.Error);
        }
        catch (HttpRequestException)
        {
            _offline = true;
        }

        _loading = false;
        _ = VigilarAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Session.IsTerminal)
        {
            _self = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("pos.registerKeys", _self, "escaner");
        }

        // El escáner está deshabilitado mientras carga la caja (AutoFocus no lo alcanza): al quedar lista se le da el foco una vez,
        // para que el primer código del lector no se pierda.
        if (!_enfocado && !_loading && _session is not null && _panel == Panel.None)
        {
            _enfocado = true;
            await FocusScannerAsync();
        }
    }

    /// <summary>Reloj de la barra y estado de la conexión con el servidor de la tienda.</summary>
    private async Task VigilarAsync()
    {
        using var timer = new PeriodicTimer(IntervaloConexion);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var enLinea = await Api.PingAsync(_stop.Token);
                var cambio = enLinea == _offline || _ahora.Minute != DateTime.Now.Minute;
                if (enLinea && _offline)
                {
                    Snackbar.Add("Se recuperó la conexión con el servidor de la tienda.", Severity.Success);
                }

                _offline = !enLinea;
                _ahora = DateTime.Now;
                if (cambio)
                {
                    await InvokeAsync(StateHasChanged);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // La pantalla se cerró.
        }
        catch (ObjectDisposedException)
        {
            // La pantalla se cerró durante una consulta.
        }
    }

    /// <summary>Teclas de función de la caja (D15-06), enviadas por <c>pos.js</c>. La lista está en docs/guia-diseno.md §7.</summary>
    [JSInvokable]
    public async Task OnKey(string key)
    {
        if (_session is null && key is not "Escape" and not "F1" and not "F5")
        {
            return;
        }

        switch (key)
        {
            case "Escape":
                CancelarPesaje();
                await CerrarPanel();
                break;
            case "F1":
                _panel = _panel == Panel.Ayuda ? Panel.None : _panel == Panel.None ? Panel.Ayuda : _panel;
                break;
            case "F5" when _panel is Panel.None or Panel.Consulta:
                AbrirConsulta();
                break;
            case "F2" when _panel == Panel.None:
                await FocusScannerAsync();
                break;
            case "F3" when _panel == Panel.None:
                AbrirCliente();
                break;
            case "F4" when _panel == Panel.None:
                AbrirCantidad();
                break;
            case "F6" when _panel == Panel.None:
                AbrirDescuento();
                break;
            case "F7" when _panel == Panel.None:
                PedirCancelarVenta();
                break;
            case "F8" when _panel == Panel.None:
                await SuspenderAsync();
                break;
            case "F9" when _panel == Panel.None:
                await VerSuspendidasAsync();
                break;
            case "F10" when _panel == Panel.Cobro:
                await CobrarAsync();
                break;
            case "F10" when _panel == Panel.None:
                AbrirCobro();
                break;
            case "Delete" when _panel == Panel.None:
                await AnularLineaAsync();
                break;
            case "Insert" when _panel is Panel.None or Panel.Pesando && Scale.Enabled:
                await TaraManualAsync();
                break;
        }

        StateHasChanged();
    }

    // ------------------------------------------------------------------------------------------ Venta

    /// <summary>Separa "3*arroz" en cantidad (3) y texto (arroz).</summary>
    private static (decimal? Quantity, string Text) Separar(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        var star = text.IndexOf('*', StringComparison.Ordinal);
        return star > 0 && decimal.TryParse(text[..star].Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var q)
            ? (q, text[(star + 1)..].Trim())
            : (null, text);
    }

    /// <summary>
    /// Al escribir un nombre (con letras, 2 o más caracteres) se muestra la lista de productos bajo el escáner. Un código de barras o PLU
    /// (solo números) no busca nada: el lector sigue igual de rápido.
    /// </summary>
    private async Task EscanerCambioAsync(string? value)
    {
        _scan = value;
        _sugerirCts?.Cancel();
        var (_, text) = Separar(value);
        if (text.Length < 2 || !text.Any(char.IsLetter))
        {
            CerrarSugerencias();
            return;
        }

        var cts = _sugerirCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            var encontrados = await BuscarProductosAsync(text);
            if (!cts.IsCancellationRequested)
            {
                _sugerencias = encontrados;
                _sugerida = encontrados.Count > 0 ? 0 : -1;
                StateHasChanged();
            }
        }
        catch (TaskCanceledException)
        {
            // Se siguió escribiendo: cuenta la búsqueda más reciente.
        }
    }

    private void CerrarSugerencias()
    {
        _sugerirCts?.Cancel();
        _sugerencias = [];
        _sugerida = -1;
    }

    private async Task<List<JsonElement>> BuscarProductosAsync(string text)
    {
        try
        {
            var page = await Api.GetAsync<JsonElement>($"api/v1/catalog/products?search={Uri.EscapeDataString(text)}&status=ACTIVE&pageSize=8");
            var items = page.ValueKind == JsonValueKind.Array ? page : page.TryGetProperty("items", out var list) ? list : default;
            return items.ValueKind == JsonValueKind.Array ? [.. items.EnumerateArray()] : [];
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Texto, Severity.Error);
        }
        catch (HttpRequestException)
        {
            _offline = true;
        }

        return [];
    }

    private async Task AgregarSugerenciaAsync(JsonElement product)
    {
        var (quantity, _) = Separar(_scan);
        _scan = string.Empty;
        CerrarSugerencias();
        var productId = product.GetProperty("id").GetGuid();
        if (quantity is null && Scale.Enabled && await ProductoDeBasculaAsync(product.Str("sku")) is { } pesable)
        {
            await PesarAsync(pesable, productId);
            return;
        }

        await AgregarAsync(new { productId, quantity });
    }

    // ------------------------------------------------------------------------------------------ Báscula (Fase 7-C)

    /// <summary>
    /// Si el código es de un producto "Se vende por báscula" (y no una etiqueta de báscula, que ya trae el peso), lo devuelve para
    /// leer la báscula. Solo se consulta cuando la caja tiene báscula configurada.
    /// </summary>
    private async Task<ScanResult?> ProductoDeBasculaAsync(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        try
        {
            var item = await Api.GetAsync<ScanResult>($"api/v1/catalog/scan/{Uri.EscapeDataString(code)}?branchId={Session.Me!.BranchId}");
            return item is { IsSoldByScale: true, PackagingId: null, Source: not ("SCALE_WEIGHT" or "SCALE_PRICE") } ? item : null;
        }
        catch (ApiException)
        {
            // Código desconocido u otro problema: la venta normal da el mensaje.
            return null;
        }
    }

    /// <summary>
    /// Lee la báscula y agrega la línea con el peso estable (decisión del propietario): espera hasta "Espera peso estable" segundos a
    /// que se quede quieto y hasta "Espera producto" a que pongan el producto; con sobrepeso, negativo o sin báscula avisa. Esc cancela.
    /// <para>Retorno a cero (D1): después de cada pesada la báscula debe quedar vacía antes de la siguiente. Si el agente todavía no la ha
    /// visto vacía, muestra "Retire el producto de la báscula" y espera (como con el peso inestable). El servidor exige lo mismo con la
    /// lectura firmada por el agente (D3), así que recargar la página no lo salta.</para>
    /// </summary>
    private async Task PesarAsync(ScanResult producto, Guid productId)
    {
        CancelarPesada();
        var cts = _pesoCts = new CancellationTokenSource();
        _pesoProducto = producto;
        _pesoProductoId = productId;
        _pesoLectura = null;
        _pesoRetirar = false;
        _pesoMensaje = "Leyendo la báscula…";
        _pesoTono = "info";
        _panel = Panel.Pesando;
        StateHasChanged();

        var config = Scale.Config!;
        var inicio = DateTime.UtcNow;
        DateTime? inestableDesde = null;
        DateTime? retirarDesde = null;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var lectura = _pesoLectura = await AgentScale.ReadAsync();
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                if (lectura.Unavailable)
                {
                    BasculaSinRespuesta(lectura.Message, config.AllowManualWeight);
                    return;
                }

                var ahora = DateTime.UtcNow;

                // Tara (una sola regla): la del producto solo cuenta si no hubo tara manual (Ins) ni la báscula envió un peso neto.
                var tara = lectura.TareKg == 0m && !lectura.ScaleNet ? producto.TareGrams / 1000m : 0m;
                if (lectura.IsOk && !lectura.EmptiedSinceLastAccepted)
                {
                    retirarDesde ??= ahora;
                    // BB1: con la trama neta (tara puesta con la tecla de la báscula) el agente no puede ver la báscula vacía.
                    (_pesoMensaje, _pesoTono) = (lectura.ScaleNet ? "Quite la tara de la báscula (tecla de tara) para continuar" : "Retire el producto de la báscula", "alerta");
                    _pesoRetirar = true;
                    if (ahora - retirarDesde > TimeSpan.FromSeconds(config.EmptyWaitSeconds))
                    {
                        TerminarPesaje("La báscula no quedó vacía después de la pesada anterior: no se agregó. Retire el producto y vuelva a registrarlo.");
                        return;
                    }
                }
                else if (lectura.IsOk && lectura.SignedReading is null)
                {
                    // La báscula responde, pero el agente no está vinculado con esta caja: el servidor no aceptaría el peso.
                    BasculaSinRespuesta("Báscula no vinculada: vincule con F1 → Vincular báscula (con la autorización de un supervisor).", config.AllowManualWeight);
                    return;
                }
                else if (lectura.IsOk && lectura.SignedReading is { } firmada && lectura.WeightKg - tara > 0m)
                {
                    _panel = Panel.None;
                    var lecturaId = lectura.ReadingId;
                    var taraManual = lectura.TareKg;
                    await AgregarAsync(
                        new
                        {
                            productId,
                            weightKg = lectura.WeightKg,
                            weightSource = "SCALE",
                            scale = new { payload = firmada.Payload, signature = firmada.Signature },
                        },
                        () => PesadaAgregada(lecturaId, taraManual));
                    return;
                }
                else if (lectura.Status == "UNSTABLE")
                {
                    retirarDesde = null;
                    inestableDesde ??= ahora;
                    (_pesoMensaje, _pesoTono) = ("Esperando peso estable…", "info");
                    if (ahora - inestableDesde > TimeSpan.FromSeconds(config.StableWaitSeconds))
                    {
                        TerminarPesaje("El peso no se quedó quieto: no se agregó. Vuelva a registrar el producto.");
                        return;
                    }
                }
                else
                {
                    inestableDesde = null;
                    retirarDesde = null;
                    (_pesoMensaje, _pesoTono) = lectura.Status switch
                    {
                        "OK" => ($"El peso no supera la tara del producto ({producto.TareGrams} g): ponga el producto en la báscula.", "alerta"),
                        "ZERO" => ("Ponga el producto en la báscula", "info"),
                        _ => (lectura.Message, "error"),
                    };
                    if (ahora - inicio > TimeSpan.FromSeconds(config.EmptyWaitSeconds))
                    {
                        TerminarPesaje(lectura.Status == "ZERO" || lectura.Status == "OK"
                            ? "No llegó peso a la báscula: no se agregó nada."
                            : $"{lectura.Message} No se agregó nada.");
                        return;
                    }
                }

                await InvokeAsync(StateHasChanged);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                await Task.Delay(150, cts.Token);
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or ObjectDisposedException)
        {
            // El cajero canceló con Esc (o empezó otra pesada / abrió el cobro, que cancelan y desechan esta).
        }
    }

    /// <summary>
    /// Se agregó la línea pesada: el agente anota la pesada (la siguiente exige ver la báscula vacía; su vigilante la lee hasta verla
    /// vacía) y se quita la tara manual, que solo valía para esta pesada (D2).
    /// </summary>
    private static void PesadaAgregada(Guid? lecturaId, decimal taraManual) => _ = TrasPesadaAsync(lecturaId, taraManual > 0m);

    private static async Task TrasPesadaAsync(Guid? lecturaId, bool quitarTara)
    {
        if (lecturaId is { } id)
        {
            await AgentScale.AcceptedAsync(id);
        }

        if (quitarTara)
        {
            await AgentScale.TareAsync(clear: true);
        }
    }

    /// <summary>Si el servidor ya no espera que se retire el producto, el agente tampoco (BN1).</summary>
    private async Task SincronizarRetornoACeroAsync()
    {
        try
        {
            if (!(await Api.GetAsync<Pos.Modules.Sales.Contracts.ScaleInterlockDto>("api/v1/sales/scale/interlock")).AwaitingEmpty)
            {
                await AgentScale.ReleaseAcceptedAsync();
            }
        }
        catch (ApiException)
        {
            // Sin respuesta del servidor: se deja como está (el servidor es el que decide al pesar).
        }
    }

    /// <summary>
    /// "Reiniciar báscula" (BB5): con permiso o la autorización de un supervisor (queda auditado), libera la espera de "retire el producto"
    /// en el servidor y luego en el agente.
    /// </summary>
    private async Task ReiniciarBasculaAsync()
    {
        CancelarPesada();
        _panel = Panel.None;
        await RunAsync(async grant =>
        {
            var estado = await Api.SendAsync<Pos.Modules.Sales.Contracts.ScaleInterlockDto>(HttpMethod.Post, "api/v1/sales/scale/reset", null, grant);
            if (!estado.AwaitingEmpty)
            {
                await AgentScale.ReleaseAcceptedAsync();
            }

            Snackbar.Add("Báscula reiniciada: ya puede pesar el siguiente producto.", Severity.Info);
        });
    }

    /// <summary>
    /// "Vincular báscula" (D3/BB2): en ESTA caja, el agente crea la clave que firma las lecturas y el servidor registra la pública. Exige el
    /// permiso o la autorización de un supervisor; el servidor solo lo acepta desde la sesión de esta caja en su propio equipo.
    /// </summary>
    private async Task VincularBasculaAsync()
    {
        if (Session.Me?.PosTerminalId is not { } caja)
        {
            return;
        }

        _panel = Panel.None;
        var url = $"api/v1/organization/terminals/{caja}/scale/reading-key";
        await RunAsync(async grant =>
        {
            // BK1: primero el permiso o la autorización del supervisor. Sin el permiso, esta consulta sin clave hace que el servidor pida
            // el supervisor (RunAsync muestra el panel y repite con la autorización) ANTES de tocar el agente.
            if (grant is null && !Session.Has("organization.scale.link"))
            {
                await Api.SendAsync<Pos.Modules.Organization.Contracts.TerminalScaleDto>(HttpMethod.Put, url, new { });
            }

            // El agente prepara una clave PENDIENTE (la vigente sigue firmando); solo se activa si el servidor la registró.
            var (vinculo, error) = await AgentScale.PairAsync(caja);
            if (vinculo is null)
            {
                Snackbar.Add(error ?? "El agente de esta caja no respondió.", Severity.Error);
                return;
            }

            await Api.SendAsync<Pos.Modules.Organization.Contracts.TerminalScaleDto>(
                HttpMethod.Put, url, new { keyId = vinculo.KeyId, publicKey = vinculo.PublicKey }, grant);
            if (await AgentScale.CommitPairingAsync(vinculo.KeyId) is { } fallo)
            {
                Snackbar.Add($"El servidor registró la clave, pero el agente no la activó: {fallo} Vuelva a vincular la báscula.", Severity.Error);
                return;
            }

            var aviso = await Scale.LoadAsync(caja);
            Snackbar.Add(aviso is null ? "Báscula vinculada con esta caja." : $"Báscula vinculada. {aviso}", aviso is null ? Severity.Success : Severity.Warning);
        });
    }

    /// <summary>Detiene la pesada en curso: primero Cancel y luego Dispose (su bucle revisa la cancelación antes de usar el token).</summary>
    private void CancelarPesada()
    {
        var cts = _pesoCts;
        _pesoCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    /// <summary>Esc durante la pesada: la tara manual tomada para ella se quita (D2).</summary>
    private void CancelarPesaje()
    {
        if (_panel is Panel.Pesando or Panel.PesoDigitado && _pesoLectura is { TareKg: > 0m })
        {
            _ = AgentScale.TareAsync(clear: true);
        }
    }

    private void TerminarPesaje(string mensaje)
    {
        _panel = Panel.None;
        Snackbar.Add(mensaje, Severity.Warning);
        _ = InvokeAsync(async () =>
        {
            StateHasChanged();
            await FocusScannerAsync();
        });
    }

    /// <summary>La báscula o el agente no responden: si la caja lo permite, se digita el peso (queda marcado y exige permiso).</summary>
    private void BasculaSinRespuesta(string mensaje, bool permiteDigitar)
    {
        if (!permiteDigitar)
        {
            TerminarPesaje($"{mensaje} Avise al administrador.");
            return;
        }

        _pesoMensaje = mensaje;
        _pesoDigitado = 0m;
        _panel = Panel.PesoDigitado;
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task PesoDigitadoTeclaAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await AgregarPesoDigitadoAsync();
        }
    }

    private async Task AgregarPesoDigitadoAsync()
    {
        if (_pesoDigitado <= 0m)
        {
            Snackbar.Add("Escriba el peso en kilos, por ejemplo 0,700.", Severity.Warning);
            return;
        }

        var body = new { productId = _pesoProductoId, weightKg = _pesoDigitado };
        _panel = Panel.None;
        await RunAsync(async grant =>
        {
            _sale ??= await Api.PostAsync<Sale>("api/v1/sales");
            _lastChange = null;
            _sale = await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/lines/manual-weight", body, grant);
            _selected = _sale.ActiveLines.LastOrDefault()?.Id;
            _nueva = _selected;
        });
    }

    /// <summary>
    /// Tara manual (tecla Insert): toma el peso actual (bolsa o bandeja) y lo descuenta de la SIGUIENTE pesada; reemplaza la tara del
    /// producto. Se quita sola al agregar la línea o cuando la báscula queda vacía.
    /// </summary>
    private async Task TaraManualAsync()
    {
        var lectura = await AgentScale.TareAsync(clear: false);
        Snackbar.Add(
            lectura.Unavailable || lectura.Status == "ERROR" ? lectura.Message
                : lectura.TareKg > 0m ? $"Tara tomada: {lectura.TareKg.ToString("0.000", Format.Colombia)} kg." : "Tara quitada.",
            lectura.Unavailable || lectura.Status == "ERROR" ? Severity.Warning : Severity.Info);
    }

    private async Task EscanerTeclaAsync(KeyboardEventArgs e)
    {
        if (_sugerencias.Count > 0)
        {
            switch (e.Key)
            {
                case "ArrowUp" or "ArrowDown":
                    _sugerida = Math.Clamp(_sugerida + (e.Key == "ArrowUp" ? -1 : 1), 0, _sugerencias.Count - 1);
                    return;
                case "Escape":
                    CerrarSugerencias();
                    return;
                case "Enter" when _sugerida >= 0:
                    await AgregarSugerenciaAsync(_sugerencias[_sugerida]);
                    return;
            }
        }

        if (e.Key is "ArrowUp" or "ArrowDown")
        {
            await MoverSeleccionAsync(e.Key == "ArrowUp" ? -1 : 1);
            return;
        }

        if (e.Key != "Enter" || string.IsNullOrWhiteSpace(_scan))
        {
            return;
        }

        var (quantity, text) = Separar(_scan);

        // Enter con letras antes de que aparezca la lista: SKU exacto o un solo producto se agrega; si hay varios, se muestra la lista.
        if (text.Any(char.IsLetter))
        {
            _sugerirCts?.Cancel();
            var encontrados = await BuscarProductosAsync(text);
            var exacto = encontrados.FirstOrDefault(p => string.Equals(p.Str("sku"), text, StringComparison.OrdinalIgnoreCase));
            if (exacto.ValueKind == JsonValueKind.Object || encontrados.Count == 1)
            {
                await AgregarSugerenciaAsync(exacto.ValueKind == JsonValueKind.Object ? exacto : encontrados[0]);
                return;
            }

            _sugerencias = encontrados;
            _sugerida = encontrados.Count > 0 ? 0 : -1;
            if (encontrados.Count == 0)
            {
                Snackbar.Add($"No encontramos productos con \"{text}\".", Severity.Warning);
            }

            return;
        }

        _scan = string.Empty;
        CerrarSugerencias();
        if (quantity is null && Scale.Enabled && await ProductoDeBasculaAsync(text) is { } pesable)
        {
            await PesarAsync(pesable, pesable.ProductId);
            return;
        }

        await AgregarAsync(new { code = text, quantity });
    }

    /// <summary>Flechas en el escáner: cambian la línea seleccionada (para F4 cantidad, Supr anular o F6 descuento).</summary>
    private async Task MoverSeleccionAsync(int paso)
    {
        if (_sale is null)
        {
            return;
        }

        var activas = _sale.ActiveLines.ToList();
        if (activas.Count == 0)
        {
            return;
        }

        var actual = activas.FindIndex(l => l.Id == _selected);
        var nueva = actual < 0 ? activas.Count - 1 : Math.Clamp(actual + paso, 0, activas.Count - 1);
        _selected = activas[nueva].Id;
        await Js.InvokeVoidAsync("pos.scrollIntoView", $"linea-{_selected}");
    }

    /// <summary>Agrega una línea a la venta en curso (la crea si no hay).</summary>
    /// <param name="line">Cuerpo de <c>POST /sales/{id}/lines</c>.</param>
    /// <param name="alAgregar">Se llama cuando el servidor agregó la línea (también si la agregó después de autorizar un supervisor).</param>
    private async Task AgregarAsync(object line, Action? alAgregar = null)
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
                catch (ApiException ex) when (ex.Code == Pos.Modules.Sales.Contracts.PosClientBuild.OutdatedCode)
                {
                    // Fase 7-C (BN10): el servidor pide una versión más nueva de esta pantalla: se recarga.
                    Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
                    return;
                }
                catch (ApiException ex) when (ex.Code == "SALES.EXPIRED_LOT_REQUIRES_AUTHORIZATION")
                {
                    // Lote vencido (RN-SAL-18): la ruta de vencidos pide la autorización del supervisor.
                    Snackbar.Add(ex.Texto, Severity.Warning);
                    _sale = await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/lines/expired", line);
                }
            }
            else
            {
                _sale = await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/lines/expired", line, grant);
            }
            alAgregar?.Invoke();
            _selected = _sale.ActiveLines.LastOrDefault()?.Id;
            _nueva = _selected;
        });
        if (_selected is not null)
        {
            await Js.InvokeVoidAsync("pos.scrollIntoView", $"linea-{_selected}");
        }
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

        var linea = _sale.Lines.First(l => l.Id == _selected);
        if (linea.WeightSource is not null)
        {
            // Una línea pesada conserva su peso (báscula, etiqueta o digitado): se elimina y se vuelve a pesar.
            Snackbar.Add("La cantidad de un producto pesado no se cambia: elimine la línea (Supr) y vuelva a pesarlo.", Severity.Warning);
            return;
        }

        _quantity = linea.Quantity;
        _panel = Panel.Cantidad;
    }

    private async Task CantidadTeclaAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await CambiarCantidadAsync();
        }
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

        // BN1: eliminar la última pesada de la caja (para volver a pesar) libera el retorno a cero en el servidor; la caja libera también el
        // agente SOLO si el servidor lo liberó (si la última pesada fue otra, p. ej. en otra venta, los dos siguen esperando).
        var pesada = _sale.ActiveLines.Any(l => l.Id == line && l.WeightSource == "SCALE");
        await RunAsync(async grant =>
        {
            _sale = await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/lines/{line}/void", null, grant);
            _selected = _sale.ActiveLines.LastOrDefault()?.Id;
            if (pesada)
            {
                await SincronizarRetornoACeroAsync();
            }
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
            await ContarSuspendidasAsync();
        });
    }

    private async Task VerSuspendidasAsync()
    {
        if (_session is null)
        {
            return;
        }

        await RunAsync(async _ =>
        {
            _held = await Api.GetAsync<List<SaleSummary>>("api/v1/sales/held");
            _heldCount = _held.Count;
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
            _lastChange = null;
            await ContarSuspendidasAsync();
        });
    }

    /// <summary>Cuántas ventas suspendidas tiene la caja (aviso de la barra). Si falla, se deja el valor anterior.</summary>
    private async Task ContarSuspendidasAsync()
    {
        try
        {
            _heldCount = (await Api.GetAsync<List<SaleSummary>>("api/v1/sales/held")).Count;
        }
        catch (ApiException)
        {
            // Solo es un aviso.
        }
    }

    private void PedirCancelarVenta()
    {
        if (_sale is not null)
        {
            _panel = Panel.CancelarVenta;
        }
    }

    private async Task CancelarVentaAsync()
    {
        if (_sale is null)
        {
            return;
        }

        _panel = Panel.None;
        await RunAsync(async grant =>
        {
            await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/cancel", new { reason = "Cancelada en la caja" }, grant);
            _sale = null;
            _selected = null;
            Snackbar.Add("Venta cancelada.", Severity.Info);
        });
    }

    // ------------------------------------------------------------------------------------------ Consultar precio (F5)

    /// <summary>
    /// Consulta de precio sin vender (para inventariar o revisar cambios de precio): el mismo resultado que daría el lector en la venta
    /// (precio de la sucursal, presentación, báscula), sin tocar la venta en curso.
    /// </summary>
    private void AbrirConsulta()
    {
        _consulta = null;
        _consultaError = null;
        _consultaLista = [];
        _consultaSel = -1;
        _consultado = null;
        _consultaExistencias = null;
        _panel = Panel.Consulta;
    }

    private async Task ConsultaCambioAsync(string? value)
    {
        _consulta = value;
        _consultaCts?.Cancel();
        var text = (value ?? string.Empty).Trim();
        if (text.Length < 2 || !text.Any(char.IsLetter))
        {
            _consultaLista = [];
            _consultaSel = -1;
            return;
        }

        var cts = _consultaCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            var encontrados = await BuscarProductosAsync(text);
            if (!cts.IsCancellationRequested)
            {
                _consultaLista = encontrados;
                _consultaSel = encontrados.Count > 0 ? 0 : -1;
                StateHasChanged();
            }
        }
        catch (TaskCanceledException)
        {
            // Se siguió escribiendo.
        }
    }

    private async Task ConsultaTeclaAsync(KeyboardEventArgs e)
    {
        if (_consultaLista.Count > 0 && e.Key is "ArrowUp" or "ArrowDown")
        {
            _consultaSel = Math.Clamp(_consultaSel + (e.Key == "ArrowUp" ? -1 : 1), 0, _consultaLista.Count - 1);
            return;
        }

        if (e.Key != "Enter" || string.IsNullOrWhiteSpace(_consulta))
        {
            return;
        }

        if (_consultaLista.Count > 0 && _consultaSel >= 0)
        {
            await ConsultarProductoAsync(_consultaLista[_consultaSel]);
            return;
        }

        await ConsultarCodigoAsync(_consulta.Trim());
    }

    private Task ConsultarProductoAsync(JsonElement product) => ConsultarCodigoAsync(product.Str("sku"));

    /// <summary>"IVA 19 %", "INC 8 %"…: el código del impuesto sin la tarifa pegada (IVA19 → IVA) y la tarifa vigente.</summary>
    private static string NombreImpuesto(ScanTax tax)
    {
        var nombre = new string(tax.Code.TakeWhile(char.IsLetter).ToArray()) is { Length: > 0 } letras ? letras : tax.Code;
        return tax.Rate is { } rate ? $"{nombre} {rate.ToString("0.##", Format.Colombia)} %" : nombre;
    }

    private async Task ConsultarCodigoAsync(string code)
    {
        _consultaCts?.Cancel();
        _consultaLista = [];
        _consultaSel = -1;
        _consultaError = null;
        _consultaExistencias = null;
        try
        {
            _consultado = await Api.GetAsync<ScanResult>($"api/v1/catalog/scan/{Uri.EscapeDataString(code)}?branchId={Session.Me!.BranchId}");
            _consultaExistencias = await ExistenciasAsync(_consultado.ProductId);
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound && code.Any(char.IsLetter))
        {
            // No es un código: se busca por nombre y se muestra la lista.
            _consultado = null;
            _consultaLista = await BuscarProductosAsync(code);
            _consultaSel = _consultaLista.Count > 0 ? 0 : -1;
            _consultaError = _consultaLista.Count == 0 ? $"No encontramos productos con \"{code}\"." : null;
            return;
        }
        catch (ApiException ex)
        {
            _consultado = null;
            _consultaError = ex.Status == HttpStatusCode.NotFound ? $"El código {code} no corresponde a ningún producto." : ex.Texto;
        }
        catch (HttpRequestException)
        {
            _offline = true;
        }

        // Listo para el siguiente: el campo queda vacío y con el foco (consultas seguidas con el lector).
        _consulta = string.Empty;
        StateHasChanged();
        await Task.Delay(1);
        if (_consultaCampo is not null)
        {
            await _consultaCampo.FocusAsync();
        }
    }

    /// <summary>Existencias del producto en la sucursal, si el usuario puede verlas (si no, no se muestran).</summary>
    private async Task<decimal?> ExistenciasAsync(Guid productId)
    {
        if (!Session.Has("inventory.stock.view"))
        {
            return null;
        }

        try
        {
            var stock = await Api.GetAsync<JsonElement>($"api/v1/inventory/stock?productId={productId}");
            return stock.Items().Sum(r => r.Dec("quantity"));
        }
        catch (ApiException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------ Cliente y descuentos

    private void AbrirCliente()
    {
        _customerSearch = null;
        _customerError = null;
        _customers = [];
        _panel = Panel.Cliente;
    }

    private async Task BuscarClienteAsync(string text)
    {
        _customerError = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
        {
            _customers = [];
            return;
        }

        try
        {
            _customers = (await Api.GetAsync<JsonElement>($"api/v1/customers?pageSize=10&status=ACTIVE&search={Uri.EscapeDataString(text)}")).Items();
        }
        catch (ApiException ex)
        {
            _customers = [];
            _customerError = ex.Status == HttpStatusCode.Forbidden
                ? "Su usuario no puede buscar clientes. Pida ayuda a un supervisor."
                : ex.Texto;
        }
        catch (HttpRequestException)
        {
            _offline = true;
        }
    }

    /// <summary>Asigna el cliente a la venta (o lo quita con <c>null</c>: consumidor final).</summary>
    private async Task AsignarClienteAsync(Guid? partyId)
    {
        _panel = Panel.None;
        await RunAsync(async _ =>
        {
            _sale ??= await Api.PostAsync<Sale>("api/v1/sales");
            _sale = await Api.PutAsync<Sale>($"api/v1/sales/{_sale.Id}/customer", new { partyId });
            Snackbar.Add(partyId is null ? "Venta a consumidor final." : $"Cliente: {_sale.Customer.Name}.", Severity.Success);
        });
    }

    private void AbrirDescuento()
    {
        if (!HayLineas)
        {
            return;
        }

        _descuentoVenta = _selected is null;
        _descuentoPorcentaje = true;
        _descuentoValor = 0;
        _descuentoMotivo = null;
        _panel = Panel.Descuento;
    }

    private async Task AplicarDescuentoAsync()
    {
        if (_sale is null || _descuentoValor <= 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_descuentoMotivo))
        {
            Snackbar.Add("Escriba el motivo del descuento.", Severity.Warning);
            return;
        }

        var lineId = _descuentoVenta ? null : _selected;
        var percent = _descuentoPorcentaje ? _descuentoValor : (decimal?)null;
        var amount = _descuentoPorcentaje ? (decimal?)null : _descuentoValor;
        var reason = _descuentoMotivo.Trim();
        _panel = Panel.None;
        await RunAsync(async grant =>
        {
            _sale = await Api.PostAsync<Sale>($"api/v1/sales/{_sale.Id}/discounts", new { lineId, percent, amount, reason }, grant);
            Snackbar.Add("Descuento aplicado.", Severity.Success);
        });
    }

    // ------------------------------------------------------------------------------------------ Cobro

    private void AbrirCobro()
    {
        if (_sale is null || !_sale.ActiveLines.Any())
        {
            return;
        }

        _payments.Clear();
        NuevoSistecredito();
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
            if (EsSistecredito(method))
            {
                PrellenarDocumento();
            }

            return;
        }

        // Sistecrédito no se agrega a mano: entra solo cuando el cliente aprueba el cobro ("Generar cobro").
        if (EsSistecredito(method))
        {
            if (_sc is null)
            {
                Snackbar.Add("Con Sistecrédito: escriba el documento del cliente y pulse «Generar cobro».", Severity.Info);
            }

            return;
        }

        if (_amount <= 0)
        {
            return;
        }

        if (method.RequiresReference && string.IsNullOrWhiteSpace(_reference))
        {
            Snackbar.Add("Escriba la referencia del pago (número de aprobación del datáfono).", Severity.Warning);
            return;
        }

        _payments.Add(new PaymentLine(method.Id, _amount, method.RequiresReference ? _reference : null));
        _amount = Math.Max(0, _sale!.AmountDue - Pagado);
        _reference = null;
    }

    /// <summary>Valores rápidos para el efectivo: el exacto y los billetes que cubren lo que falta.</summary>
    private IEnumerable<decimal> ValoresRapidos()
    {
        var falta = Math.Max(0, (_sale?.AmountDue ?? 0) - Pagado);
        if (falta <= 0)
        {
            return [];
        }

        var valores = new SortedSet<decimal> { falta };
        foreach (var billete in Billetes)
        {
            var redondeo = Math.Ceiling(falta / billete) * billete;
            if (redondeo > falta)
            {
                valores.Add(redondeo);
            }
        }

        return valores.Take(6);
    }

    private void UsarValor(decimal valor) => _amount = valor;

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

            // El cambio se muestra ya y la caja queda lista para el siguiente cliente; el tiquete se imprime aparte (si el agente no
            // responde, el aviso llega después, sin bloquear la caja).
            _ = ImprimirAsync(receipt.Ticket);
        });
    }

    // ------------------------------------------------------------------------------------------ Sistecrédito (Fase 7-B)

    private static bool EsSistecredito(PaymentMethod? method) => method is not null && EnumText.Same(method.Kind, "SISTECREDITO");

    /// <summary>Documento del cliente asignado a la venta, si es de un tipo que acepta Sistecrédito.</summary>
    private void PrellenarDocumento()
    {
        if (_sc is null && string.IsNullOrWhiteSpace(_scDocumento) && _sale?.Customer is { Identification: { Length: > 0 } documento } cliente
            && TiposDocumento.Contains(cliente.IdentificationType))
        {
            _scTipo = cliente.IdentificationType;
            _scDocumento = documento;
        }
    }

    /// <summary>Deja el panel listo para un cobro nuevo (deja de consultar el anterior: si el cliente igual aprueba, la revisión automática lo ve).</summary>
    private void NuevoSistecredito()
    {
        _scCts?.Cancel();
        _scCts?.Dispose();
        CancelarPesada();
        _scCts = null;
        _sc = null;
        _scQr = null;
        _scDocumento = null;
        _scTipo = "CC";
        PrellenarDocumento();
    }

    private async Task GenerarSistecreditoAsync()
    {
        if (_sale is null || _methodSelected is null || _amount <= 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_scDocumento))
        {
            Snackbar.Add("Escriba el número de documento del cliente.", Severity.Warning);
            return;
        }

        _busy = true;
        try
        {
            _sc = await Api.PostAsync<SistecreditoPaymentDto>("api/v1/cash/sistecredito/payments", new
            {
                saleId = _sale.Id, amount = _amount, documentType = _scTipo, documentNumber = _scDocumento,
            });
            _offline = false;
            await AplicarSistecreditoAsync();
            if (_sc is { Status: "PENDING" } pendiente)
            {
                _scCts = new CancellationTokenSource();
                _ = VigilarSistecreditoAsync(pendiente.Id, _scCts.Token);
            }
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Texto, ex.Status == HttpStatusCode.UnprocessableEntity ? Severity.Warning : Severity.Error);
        }
        catch (HttpRequestException)
        {
            _offline = true;
            Snackbar.Add("Se perdió la conexión con el servidor de la tienda. Reintente.", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Estado en vivo: pregunta cada 3 s hasta que el cliente aprueba, Sistecrédito rechaza o la cajera cancela.</summary>
    private async Task VigilarSistecreditoAsync(Guid attemptId, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(IntervaloSistecredito, token);
                SistecreditoPaymentDto estado;
                try
                {
                    estado = await Api.PostAsync<SistecreditoPaymentDto>($"api/v1/cash/sistecredito/payments/{attemptId}/check");
                }
                catch (Exception ex) when (ex is ApiException or HttpRequestException)
                {
                    continue; // se reintenta en la siguiente vuelta
                }

                if (token.IsCancellationRequested || _sc?.Id != attemptId)
                {
                    return;
                }

                await InvokeAsync(async () =>
                {
                    _sc = estado;
                    await AplicarSistecreditoAsync();
                    StateHasChanged();
                });
                if (estado.Status != "PENDING")
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Se canceló el cobro o se cerró el panel.
        }
        catch (ObjectDisposedException)
        {
            // La pantalla se cerró.
        }
    }

    /// <summary>Muestra el QR cuando llega la URL; si el cliente aprobó, agrega el pago y sigue con el cobro normal.</summary>
    private async Task AplicarSistecreditoAsync()
    {
        if (_sc is not { } cobro)
        {
            return;
        }

        if (cobro.Status == "PENDING" && cobro.RedirectUrl is not null && _scQr is null)
        {
            try
            {
                var (_, type, content) = await Api.DownloadAsync($"api/v1/cash/sistecredito/payments/{cobro.Id}/qr");
                _scQr = $"data:{type};base64,{Convert.ToBase64String(content)}";
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                // Sin QR se reintenta en la siguiente consulta.
            }
        }

        if (cobro.Status != "APPROVED" || cobro.CompletedSaleId is not null || _methods.FirstOrDefault(EsSistecredito) is not { } medio)
        {
            return;
        }

        if (!_payments.Any(p => p.Reference == cobro.TransactionId))
        {
            _payments.Add(new PaymentLine(medio.Id, cobro.Amount, cobro.TransactionId));
        }

        Snackbar.Add($"Sistecrédito aprobó el crédito{(cobro.Installments is { } cuotas ? $" a {cuotas} cuotas" : "")}.", Severity.Success);
        NuevoSistecredito();
        if (_sale is not null && Pagado >= _sale.AmountDue)
        {
            await CobrarAsync();
        }
        else if (_sale is not null)
        {
            // Pago mixto: falta una parte, que se cobra con otro medio.
            _methodSelected = _methods.FirstOrDefault(m => m.AffectsCashDrawer) ?? _methodSelected;
            _amount = Math.Max(0, _sale.AmountDue - Pagado);
        }
    }

    private async Task CancelarSistecreditoAsync()
    {
        if (_sc is not { } cobro)
        {
            return;
        }

        _busy = true;
        try
        {
            _scCts?.Cancel();
            _sc = await Api.PostAsync<SistecreditoPaymentDto>($"api/v1/cash/sistecredito/payments/{cobro.Id}/cancel");
            if (_sc.Status == "APPROVED")
            {
                await AplicarSistecreditoAsync();
            }
            else
            {
                Snackbar.Add(
                    _sc.Status == "REVIEW_REQUIRED" ? _sc.Message ?? "Cobro cancelado: la administración debe revisarlo." : "Cobro de Sistecrédito cancelado.",
                    Severity.Info);
                NuevoSistecredito();
            }
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Texto, Severity.Warning);
        }
        catch (HttpRequestException)
        {
            _offline = true;
            Snackbar.Add("Se perdió la conexión con el servidor de la tienda. Reintente.", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }


    private async Task ImprimirAsync(JsonElement ticket)
    {
        if (await Printer.PrintAsync(Session.Me!.PosTerminalId!.Value, ticket) is { } problem)
        {
            await InvokeAsync(() =>
            {
                Snackbar.Add(problem, Severity.Warning);
                StateHasChanged();
            });
        }
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
            else
            {
                Snackbar.Add("Tiquete enviado a la impresora.", Severity.Info);
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
            Snackbar.Add("No hay un medio de pago en efectivo activo. Pida a la administración que lo active.", Severity.Error);
            return;
        }

        await RunAsync(async _ =>
        {
            _session = await Api.PostAsync<CashSession>("api/v1/cash/sessions", new
            {
                openingFloat = Total(_opening),
                openingCount = _opening.Where(c => c.Value > 0).Select(c => new { paymentMethodId = cash.Id, denominationId = c.Key, quantity = c.Value }),
            });
            Snackbar.Add($"Turno {_session.Number} abierto.", Severity.Success);
            await ContarSuspendidasAsync();
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

        // El reporte Z sale impreso al cerrar (se guarda con el efectivo); si la impresora falla, se puede reimprimir.
        if (_zReport is not null)
        {
            await ImprimirZAsync();
        }
    }

    private async Task ImprimirZAsync()
    {
        if (_zReport is null || Session.Me?.PosTerminalId is not { } caja)
        {
            return;
        }

        var problema = await Printer.PrintTextAsync(caja, $"Reporte Z {_zReport.Number}", _zReport.Text);
        Snackbar.Add(problema ?? "Reporte Z enviado a la impresora.", problema is null ? Severity.Success : Severity.Warning);
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
        CancelarPesada();
        NuevoSistecredito();
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
        catch (HttpRequestException)
        {
            // Sin conexión: la sesión vence sola en el servidor.
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
            _offline = false;
        }
        catch (ApiException ex) when (ex.NeedsSupervisor)
        {
            _pending = new PendingAction(ex, action);
            _supervisorCode = _supervisorPin = _supervisorReason = null;
            _panel = Panel.Supervisor;
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Texto, ex.Status == HttpStatusCode.UnprocessableEntity ? Severity.Warning : Severity.Error);
        }
        catch (HttpRequestException)
        {
            _offline = true;
            Snackbar.Add("Se perdió la conexión con el servidor de la tienda. La venta en curso no se pierde: reintente.", Severity.Error);
        }
        finally
        {
            _busy = false;
        }

        if (_panel == Panel.None)
        {
            // Primero se pinta (el escáner estaba deshabilitado mientras la acción corría) y luego se le devuelve el foco.
            StateHasChanged();
            await Task.Delay(1);
            await FocusScannerAsync();
        }
    }

    private async Task SupervisorTeclaAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await AutorizarAsync();
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
            _supervisorPin = null;
            Snackbar.Add(ex.Texto, Severity.Error);
        }
        catch (HttpRequestException)
        {
            _offline = true;
            Snackbar.Add("Sin conexión con el servidor de la tienda. Reintente.", Severity.Error);
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

    // ------------------------------------------------------------------------------------------ Presentación

    private static string IconoMedio(PaymentMethod method) => method.Kind.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant() switch
    {
        "CASH" => Icons.Material.Outlined.Payments,
        "DEBITCARD" or "CREDITCARD" => Icons.Material.Outlined.CreditCard,
        "TRANSFER" => Icons.Material.Outlined.AccountBalance,
        "WALLET" => Icons.Material.Outlined.QrCode2,
        "VOUCHER" => Icons.Material.Outlined.CardGiftcard,
        "SISTECREDITO" => Icons.Material.Outlined.CreditScore,
        _ => method.AffectsCashDrawer ? Icons.Material.Outlined.Payments : Icons.Material.Outlined.Wallet,
    };

    /// <summary>Estado del documento fiscal de la última venta, en palabras de la cajera.</summary>
    private (string Texto, string Tono, string Icono)? EstadoFiscal()
    {
        if (_lastReceipt is not { DocumentType: { } tipo } receipt || tipo == "INTERNAL_RECEIPT")
        {
            return null;
        }

        var documento = tipo == "POS_ELECTRONIC" ? "Documento equivalente POS" : "Factura electrónica";
        return receipt.DocumentStatus switch
        {
            "ACCEPTED" => ($"{documento} {receipt.FiscalNumber} validada por la DIAN", "exito", Icons.Material.Outlined.VerifiedUser),
            "PENDING" or "SUBMITTING" or null => ($"{documento} en proceso: se envía a la DIAN en segundo plano", "info", Icons.Material.Outlined.HourglassTop),
            "CONTINGENCY" => ($"{documento} en contingencia: se enviará cuando haya conexión", "alerta", Icons.Material.Outlined.CloudOff),
            "REJECTED" or "ERROR" => ($"{documento} con problema: la administración debe revisarla", "error", Icons.Material.Outlined.ErrorOutline),
            _ => ($"{documento}: {receipt.DocumentStatus}", "info", Icons.Material.Outlined.Description),
        };
    }

    /// <summary>Abreviatura de la unidad de peso (kg, g, lb).</summary>
    private static string UnidadPeso(string? unitCode) => Pos.SharedKernel.Measurement.WeightUnits.Label(unitCode);

    private static string EstadoLicencia(LicenseSummary license) => license.State switch
    {
        "DEMO" => $"Demostración · {license.DaysLeft} días",
        "GRACE" => $"Licencia en gracia · {license.DaysLeft} días",
        _ => "Licencia restringida",
    };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _stop.Dispose();
        _scCts?.Cancel();
        _scCts?.Dispose();
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
