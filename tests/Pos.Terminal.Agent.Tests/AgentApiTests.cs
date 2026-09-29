using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Modules.Organization.Contracts;
using Pos.Printing;
using Pos.Terminal.Agent.Api;
using Pos.Terminal.Agent.Printing;
using static Pos.Terminal.Agent.Tests.AgentFactory;

namespace Pos.Terminal.Agent.Tests;

/// <summary>Endpoints del agente con la impresora de archivo o de red local: los bytes que llegan a la "impresora" se comparan uno a uno.</summary>
public class AgentApiTests
{
    internal static readonly ReceiptPrinterSettings FilePrinter = new();

    /// <summary>JSON del servidor (ServerSetup: web + enumeraciones como texto).</summary>
    private static readonly JsonSerializerOptions ServerJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    internal static TicketDocument Sale() => new(
        "Venta S01C01-000123",
        [
            new TextLine("LA ECONOMÍA", TicketAlign.Center, Bold: true, DoubleSize: true),
            new SeparatorLine(),
            new ColumnsLine("Arroz 500 g", TicketLayout.Money(2_900m)),
            new ColumnsLine("TOTAL", TicketLayout.Money(2_900m), Bold: true),
            new BarcodeElement("S01C01-000123"),
            new QrElement("https://example.test/v/123"),
            new FeedElement(2),
        ],
        Cut: true,
        OpenDrawer: true);

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task ShouldFailAsync(Task<HttpResponseMessage> call, HttpStatusCode status, string code)
    {
        var response = await call;
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        (await CodeAsync(response)).ShouldBe(code);
    }

    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Imprime_el_tiquete_neutro_en_ESC_POS_byte_a_byte()
    {
        await using var agent = new AgentFactory();

        var response = await agent.CreateClient().PostAsync("/print", Body(new PrintRequest(Sale(), FilePrinter)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var outcome = (await response.Content.ReadFromJsonAsync<PrintOutcome>(Json, Ct))!;
        outcome.Succeeded.ShouldBeTrue();
        outcome.Kind.ShouldBe("print");
        outcome.Destination!.ShouldEndWith(".escpos");
        Path.GetDirectoryName(outcome.Destination).ShouldBe(agent.OutputDirectory);
        var expected = EscPosEncoder.Encode(Sale(), new PrinterOptions(42, PrinterCodePage.Pc850, AutoCut: true, DrawerPin.Pin2));
        var printed = agent.SingleOutput();
        printed.ShouldBe(expected);
        outcome.Bytes.ShouldBe(expected.Length);
        printed[..5].ShouldBe(new byte[] { 0x1B, 0x40, 0x1B, 0x74, 0x02 });
        printed[^5..].ShouldBe(EscPosEncoder.OpenDrawer(DrawerPin.Pin2));
    }

    [Fact]
    public async Task Acepta_la_impresora_tal_como_la_devuelve_el_servidor()
    {
        await using var agent = new AgentFactory();
        var dto = new ReceiptPrinterDto(Guid.CreateVersion7(), Configured: true, "FILE", null, 58, "ASCII", AutoCut: false, DrawerConnected: true, "PIN5");
        var json = $$"""{ "ticket": {{JsonSerializer.Serialize(Sale(), Json)}}, "printer": {{JsonSerializer.Serialize(dto, ServerJson)}} }""";

        var response = await agent.CreateClient().PostAsync("/print", Raw(json), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        agent.SingleOutput().ShouldBe(EscPosEncoder.Encode(Sale(), new PrinterOptions(32, PrinterCodePage.Ascii, AutoCut: false, DrawerPin.Pin5)));
    }

    [Fact]
    public async Task El_discriminador_del_elemento_puede_venir_en_cualquier_posicion()
    {
        await using var agent = new AgentFactory();
        const string json = """
            { "printer": { "connection": "file" },
              "ticket": { "title": "x", "cut": false, "elements": [ { "text": "Hola", "align": "center", "type": "text" } ] } }
            """;

        var response = await agent.CreateClient().PostAsync("/print", Raw(json), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        agent.SingleOutput().ShouldBe(
            EscPosEncoder.Encode(new TicketDocument("x", [new TextLine("Hola", TicketAlign.Center)], Cut: false), new PrinterOptions()));
    }

    [Fact]
    public async Task Sin_cajon_conectado_se_omite_el_pulso_y_no_se_abre()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateClient();
        var noDrawer = FilePrinter with { DrawerConnected = false };

        await ShouldFailAsync(client.PostAsync("/drawer/open", Body(new DrawerRequest(noDrawer)), Ct), HttpStatusCode.Conflict, PrintOutcome.NoDrawer);

        (await client.PostAsync("/print", Body(new PrintRequest(Sale(), noDrawer)), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var printed = agent.SingleOutput();
        printed[^EscPosEncoder.Cut.Length..].ShouldBe(EscPosEncoder.Cut);
        Contains(printed, EscPosEncoder.OpenDrawer(DrawerPin.Pin2)).ShouldBeFalse();
    }

    [Fact]
    public async Task Abre_el_cajon_con_el_pulso_del_pin_configurado()
    {
        await using var agent = new AgentFactory();

        var response = await agent.CreateClient().PostAsync("/drawer/open", Body(new DrawerRequest(FilePrinter with { DrawerPin = "PIN5" })), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        agent.SingleOutput().ShouldBe(new byte[] { 0x1B, 0x40, 0x1B, 0x70, 0x01, 0x19, 0xFA });
    }

    [Fact]
    public async Task Imprime_por_red_en_un_puerto_TCP_local()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = TransportTests.ReceiveAsync(listener);
        await using var agent = new AgentFactory();
        var printer = new ReceiptPrinterSettings { Connection = "NETWORK", Address = $"127.0.0.1:{port}" };

        var response = await agent.CreateClient().PostAsync("/print", Body(new PrintRequest(Sale(), printer)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<PrintOutcome>(Json, Ct))!.Destination.ShouldBe($"127.0.0.1:{port}");
        (await received).ShouldBe(EscPosEncoder.Encode(Sale(), new PrinterOptions()));
        Directory.Exists(agent.OutputDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Impresora_de_red_apagada_responde_503_y_queda_en_el_estado()
    {
        await using var agent = new AgentFactory(new Dictionary<string, string?> { ["Agent:NetworkConnectTimeout"] = "00:00:02" });
        var client = agent.CreateClient();
        var printer = new ReceiptPrinterSettings { Connection = "NETWORK", Address = $"127.0.0.1:{ClosedPort()}" };

        await ShouldFailAsync(client.PostAsync("/drawer/open", Body(new DrawerRequest(printer)), Ct), HttpStatusCode.ServiceUnavailable, PrintOutcome.PrinterUnavailable);

        var status = (await client.GetFromJsonAsync<AgentStatusDto>("/status", Json, Ct))!;
        status.LastJob!.Kind.ShouldBe("drawer");
        status.LastJob.Succeeded.ShouldBeFalse();
        status.LastJob.ErrorCode.ShouldBe(PrintOutcome.PrinterUnavailable);
    }

    [Fact]
    public async Task Rechaza_impresoras_invalidas()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateClient();
        ReceiptPrinterSettings[] invalid =
        [
            new() { Connection = "NETWORK" },
            new() { Connection = "NETWORK", Address = "10.0.0.5:99999" },
            new() { Connection = "SERIAL", Address = "LPT1" },
            new() { Connection = "SERIAL", Address = "COM3:12345" },
            new() { Connection = "WINDOWS_SPOOLER" },
            new() { Connection = "USB" },
            new() { PaperWidthMm = 70 },
            new() { CodePage = "UTF8" },
            new() { DrawerPin = "PIN9" },
            new() { Address = Path.Combine(Path.GetTempPath(), "otra-carpeta") },
        ];

        foreach (var printer in invalid)
        {
            await ShouldFailAsync(client.PostAsync("/print", Body(new PrintRequest(Sale(), printer)), Ct), HttpStatusCode.BadRequest, AgentEndpoints.InvalidPrinter);
        }

        await ShouldFailAsync(client.PostAsync("/print", Body(new PrintRequest(Sale(), null)), Ct), HttpStatusCode.BadRequest, AgentEndpoints.InvalidPrinter);
        await ShouldFailAsync(client.PostAsync("/drawer/open", Raw("{}"), Ct), HttpStatusCode.BadRequest, AgentEndpoints.InvalidPrinter);
        await ShouldFailAsync(client.PostAsync("/test-page", Raw("""{ "printer": { "paperWidthMm": 0 } }"""), Ct), HttpStatusCode.BadRequest, AgentEndpoints.InvalidPrinter);
        Directory.Exists(agent.OutputDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Carpeta_de_archivo_permitida_en_la_configuracion_del_agente()
    {
        var allowed = TempDirectory();
        await using var agent = new AgentFactory(new Dictionary<string, string?> { ["Agent:AllowedFileDirectories:0"] = allowed });

        var response = await agent.CreateClient().PostAsync("/drawer/open", Body(new DrawerRequest(FilePrinter with { Address = allowed + Path.DirectorySeparatorChar })), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        Directory.GetFiles(allowed).ShouldHaveSingleItem();
        Directory.Delete(allowed, recursive: true);
    }

    [Fact]
    public async Task Rechaza_tiquetes_invalidos_JSON_mal_formado_grandes_o_sin_JSON()
    {
        await using var agent = new AgentFactory(new Dictionary<string, string?> { ["Agent:MaxRequestBytes"] = "4096" });
        var client = agent.CreateClient();

        TicketDocument?[] invalid =
        [
            null,
            new TicketDocument("x", []),
            new TicketDocument("x", [new BarcodeElement("")]),
            new TicketDocument("x", [new BarcodeElement("CÓDIGO")]),
            new TicketDocument("x", [new FeedElement(20)]),
            new TicketDocument("x", [new SeparatorLine('\n')]),
            new TicketDocument("x", [new TextLine("a", (TicketAlign)9)]),
        ];
        foreach (var ticket in invalid)
        {
            await ShouldFailAsync(client.PostAsync("/print", Body(new PrintRequest(ticket, FilePrinter)), Ct), HttpStatusCode.BadRequest, AgentEndpoints.InvalidDocument);
        }

        await ShouldFailAsync(client.PostAsync("/print", Raw("""{ "ticket": { "title": "x", "elements": [ { "type": "beep" } ] }, "printer": {} }"""), Ct),
            HttpStatusCode.BadRequest, AgentEndpoints.InvalidRequest);
        await ShouldFailAsync(client.PostAsync("/print", Raw("{ esto no es JSON"), Ct), HttpStatusCode.BadRequest, AgentEndpoints.InvalidRequest);
        await ShouldFailAsync(client.PostAsync("/print", Raw("null"), Ct), HttpStatusCode.BadRequest, AgentEndpoints.InvalidRequest);
        await ShouldFailAsync(client.PostAsync("/drawer/open", new StringContent("{}", Encoding.UTF8, "text/plain"), Ct),
            HttpStatusCode.UnsupportedMediaType, AgentEndpoints.UnsupportedMediaType);
        await ShouldFailAsync(client.PostAsync("/test-page", null, Ct), HttpStatusCode.UnsupportedMediaType, AgentEndpoints.UnsupportedMediaType);
        await ShouldFailAsync(client.PostAsync("/test-page", Body(new TestPageRequest(FilePrinter, new string('c', 61))), Ct),
            HttpStatusCode.BadRequest, AgentEndpoints.InvalidRequest);

        var big = JsonSerializer.Serialize(new PrintRequest(new TicketDocument("x", [new TextLine(new string('x', 5000))]), FilePrinter), Json);
        await ShouldFailAsync(client.PostAsync("/print", Raw(big), Ct), HttpStatusCode.RequestEntityTooLarge, AgentEndpoints.RequestTooLarge);

        // Sin Content-Length (envío por partes): el límite se aplica al leer.
        var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big)));
        chunked.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/print") { Content = chunked };
        request.Headers.TransferEncodingChunked = true;
        await ShouldFailAsync(client.SendAsync(request, Ct), HttpStatusCode.RequestEntityTooLarge, AgentEndpoints.RequestTooLarge);

        Directory.Exists(agent.OutputDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Solo_atiende_al_propio_equipo_y_a_los_origenes_permitidos()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateClient();

        using var remote = new HttpRequestMessage(HttpMethod.Post, "/drawer/open") { Content = Body(new DrawerRequest(FilePrinter)) };
        remote.Headers.Add(SimulatedRemoteIpHeader, "192.168.1.30");
        await ShouldFailAsync(client.SendAsync(remote, Ct), HttpStatusCode.Forbidden, LocalRequestGuard.LocalOnly);

        using var remoteStatus = new HttpRequestMessage(HttpMethod.Get, "/status");
        remoteStatus.Headers.Add(SimulatedRemoteIpHeader, "10.0.0.8");
        await ShouldFailAsync(client.SendAsync(remoteStatus, Ct), HttpStatusCode.Forbidden, LocalRequestGuard.LocalOnly);

        using var rebinding = new HttpRequestMessage(HttpMethod.Post, "/drawer/open") { Content = Body(new DrawerRequest(FilePrinter)) };
        rebinding.Headers.Host = "atacante.example";
        await ShouldFailAsync(client.SendAsync(rebinding, Ct), HttpStatusCode.Forbidden, LocalRequestGuard.InvalidHost);

        // Una página web cualquiera no puede abrir el cajón desde el navegador de la caja.
        using var web = new HttpRequestMessage(HttpMethod.Post, "/drawer/open") { Content = Body(new DrawerRequest(FilePrinter)) };
        web.Headers.Add("Origin", "https://tienda-falsa.example");
        await ShouldFailAsync(client.SendAsync(web, Ct), HttpStatusCode.Forbidden, LocalRequestGuard.OriginNotAllowed);
        Directory.Exists(agent.OutputDirectory).ShouldBeFalse();

        using var ui = new HttpRequestMessage(HttpMethod.Get, "/status");
        ui.Headers.Add("Origin", UiOrigin);
        var allowed = await client.SendAsync(ui, Ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        allowed.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([UiOrigin]);

        // Preflight del navegador (CORS + Private Network Access) desde la interfaz de caja.
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/print");
        preflight.Headers.Add("Origin", UiOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        preflight.Headers.Add("Access-Control-Request-Private-Network", "true");
        var preflightResponse = await client.SendAsync(preflight, Ct);
        preflightResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([UiOrigin]);
        preflightResponse.Headers.GetValues("Access-Control-Allow-Private-Network").ShouldBe(["true"]);

        using var ipv6 = new HttpRequestMessage(HttpMethod.Get, "/status");
        ipv6.Headers.Host = "[::1]:5490";
        ipv6.Headers.Add(SimulatedRemoteIpHeader, "::1");
        (await client.SendAsync(ipv6, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pagina_de_prueba_con_tildes_codigos_QR_corte_y_cajon()
    {
        await using var agent = new AgentFactory();

        var response = await agent.CreateClient().PostAsync("/test-page", Body(new TestPageRequest(FilePrinter, "Caja 1")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var bytes = agent.SingleOutput();
        Contains(bytes, EscPosEncoder.Text("PÁGINA DE PRUEBA", PrinterCodePage.Pc850)).ShouldBeTrue();
        Contains(bytes, EscPosEncoder.Text("ñ Ñ á é í ó ú", PrinterCodePage.Pc850)).ShouldBeTrue();
        Contains(bytes, EscPosEncoder.Text("Caja 1", PrinterCodePage.Pc850)).ShouldBeTrue();
        Contains(bytes, [0x1D, 0x6B, 73]).ShouldBeTrue();                                   // CODE128
        Contains(bytes, [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30]).ShouldBeTrue();   // imprimir QR
        Contains(bytes, EscPosEncoder.Cut).ShouldBeTrue();
        bytes[^5..].ShouldBe(EscPosEncoder.OpenDrawer(DrawerPin.Pin2));
    }

    [Fact]
    public async Task Estado_del_agente_con_el_ultimo_trabajo()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateClient();

        var initial = (await client.GetFromJsonAsync<AgentStatusDto>("/status", Json, Ct))!;
        initial.LastJob.ShouldBeNull();
        initial.Port.ShouldBe(5490);
        initial.FileOutputDirectory.ShouldBe(agent.OutputDirectory);
        initial.Connections.ShouldContain("FILE");
        initial.Connections.ShouldContain("NETWORK");
        initial.Connections.ShouldContain("SERIAL");
        initial.Version.ShouldNotBeNullOrWhiteSpace();

        (await client.PostAsync("/drawer/open", Body(new DrawerRequest(FilePrinter)), Ct)).EnsureSuccessStatusCode();

        var status = (await client.GetFromJsonAsync<AgentStatusDto>("/status", Json, Ct))!;
        status.LastJob!.Kind.ShouldBe("drawer");
        status.LastJob.Succeeded.ShouldBeTrue();
        status.LastJob.Bytes.ShouldBe(7);
        status.LastJob.Printer!.ShouldStartWith("Archivo ");
    }

    internal static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
