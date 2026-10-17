using System.Net;
using System.Text;
using System.Text.Json;
using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Domain;
using Pos.Modules.Cash.Infrastructure.Sistecredito;

namespace Pos.Modules.Cash.UnitTests;

/// <summary>
/// Protocolo de Sistecrédito (Fase 7-B, D7B-01) contra un HttpMessageHandler simulado: nunca se llama a la API real. Mismos casos que las
/// pruebas de tutiendanueva (<c>sistecredito-verify.test.js</c>) más la creación, la consulta, la espera de la URL y la traducción de errores.
/// </summary>
public class SistecreditoGatewayTests
{
    // Llaves de mentira (nunca reales): solo sirven para ver que viajan en las cabeceras correctas.
    private static readonly SistecreditoCredentials Keys = new(new string('k', 32), new string('a', 24), new string('t', 24));

    private static readonly SistecreditoCreateRequest Request = new(
        "BP-0123", "Compra en tienda física", 125_000m, "CC", "1020304050", "https://businesspost.test/", "https://businesspost.test/confirmacion");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Crear_envia_las_cabeceras_y_el_cuerpo_del_protocolo_y_devuelve_el_id()
    {
        var (gateway, fake) = Gateway((_, _) => Json(new { errorCode = 0, data = new { _id = "6650abc", transactionStatus = "Pending" } }, HttpStatusCode.Created));

        var created = await gateway.CreateAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Request, Ct);

        created.TransactionId.ShouldBe("6650abc");
        created.TransactionStatus.ShouldBe("Pending");
        var call = fake.Calls.Single();
        call.Method.ShouldBe(HttpMethod.Post);
        call.Uri.ShouldBe("https://api.credinet.co/pay/create");
        call.Headers["SCLocation"].ShouldBe("0,0");
        call.Headers["SCOrigen"].ShouldBe("Production");
        call.Headers["country"].ShouldBe("co");
        call.Headers["Ocp-Apim-Subscription-Key"].ShouldBe(Keys.SubscriptionKey);
        call.Headers["ApplicationKey"].ShouldBe(Keys.ApplicationKey);
        call.Headers["ApplicationToken"].ShouldBe(Keys.ApplicationToken);
        call.ContentType.ShouldBe("application/json");

        using var body = JsonDocument.Parse(call.Body);
        var root = body.RootElement;
        root.GetProperty("invoice").GetString().ShouldBe("BP-0123");
        root.GetProperty("description").GetString().ShouldBe("Compra en tienda física");
        root.GetProperty("paymentMethod").GetProperty("paymentMethodId").GetInt32().ShouldBe(2);
        root.GetProperty("currency").GetString().ShouldBe("COP");
        root.GetProperty("value").GetRawText().ShouldBe("125000"); // pesos enteros, sin decimales
        root.GetProperty("sandbox").GetProperty("isActive").GetBoolean().ShouldBeFalse();
        root.GetProperty("urlResponse").GetString().ShouldBe("https://businesspost.test/");
        root.GetProperty("urlConfirmation").GetString().ShouldBe("https://businesspost.test/confirmacion");
        root.GetProperty("methodConfirmation").GetString().ShouldBe("POST");
        root.GetProperty("client").GetProperty("docType").GetString().ShouldBe("CC");
        root.GetProperty("client").GetProperty("document").GetString().ShouldBe("1020304050");
    }

    [Fact]
    public async Task El_ambiente_de_pruebas_va_en_SCOrigen_Staging()
    {
        var (gateway, fake) = Gateway((_, _) => Json(new { data = new { _id = "x1" } }));
        await gateway.CreateAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Staging), Request, Ct);
        fake.Calls.Single().Headers["SCOrigen"].ShouldBe("Staging");
    }

    [Theory]
    [InlineData(615, "demasiado bajo")]
    [InlineData(739, "no está disponible para este cliente")]
    [InlineData(738, "Ya existe una transacción activa con esa factura")]
    [InlineData(685, "no reconoce las credenciales")]
    [InlineData(690, "ApplicationKey no tiene el formato correcto")]
    [InlineData(694, "ApplicationToken no es correcto")]
    [InlineData(627, "Falta el número de documento")]
    public async Task Los_errores_de_Sistecredito_se_traducen(int errorCode, string text)
    {
        var (gateway, _) = Gateway((_, _) => Json(new { errorCode, message = "ValidationError" }, HttpStatusCode.BadRequest));

        var error = await Should.ThrowAsync<SistecreditoGatewayException>(
            () => gateway.CreateAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Request, Ct));

        error.Failure.ShouldBe(SistecreditoFailure.Provider);
        error.ErrorCode.ShouldBe(errorCode);
        error.Message.ShouldContain(text);
        error.Message.ShouldNotContain(Keys.ApplicationToken);
    }

    [Fact]
    public async Task Un_error_sin_traduccion_usa_el_mensaje_de_Sistecredito_y_un_500_sin_cuerpo_da_un_aviso_simple()
    {
        var (gateway, _) = Gateway((_, _) => Json(new { errorCode = 999, message = "Algo raro" }, HttpStatusCode.BadRequest));
        (await Should.ThrowAsync<SistecreditoGatewayException>(
                () => gateway.CreateAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Request, Ct)))
            .Message.ShouldBe("Sistecrédito respondió: Algo raro");

        var (down, _) = Gateway((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("<html>") });
        var error = await Should.ThrowAsync<SistecreditoGatewayException>(
            () => down.CreateAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Request, Ct));
        error.Failure.ShouldBe(SistecreditoFailure.Provider);
        error.Message.ShouldContain("código 503");
    }

    [Fact]
    public async Task Sin_internet_la_falla_es_de_red()
    {
        var (gateway, _) = Gateway((_, _) => throw new HttpRequestException("No such host is known."));

        var error = await Should.ThrowAsync<SistecreditoGatewayException>(
            () => gateway.CreateAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Request, Ct));

        error.Failure.ShouldBe(SistecreditoFailure.Network);
    }

    [Fact]
    public async Task Consultar_lee_estado_url_motivo_y_cuotas()
    {
        var (gateway, fake) = Gateway((_, _) => Json(new
        {
            errorCode = 0,
            data = new
            {
                _id = "abc/1",
                transactionStatus = "Approved",
                paymentMethodResponse = new { paymentRedirectUrl = "https://pay.sistecredito.test/x", description = "Aprobado", installments = 6 },
            },
        }));

        var transaction = await gateway.GetTransactionAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Staging), "abc/1", Ct);

        transaction.Status.ShouldBe("Approved");
        transaction.RedirectUrl.ShouldBe("https://pay.sistecredito.test/x");
        transaction.Description.ShouldBe("Aprobado");
        transaction.Installments.ShouldBe(6);
        var call = fake.Calls.Single();
        call.Method.ShouldBe(HttpMethod.Get);
        call.Uri.ShouldBe("https://api.credinet.co/pay/GetTransactionResponse?transactionId=abc%2F1");
        call.Headers["SCOrigen"].ShouldBe("Staging");
        call.Headers["ApplicationToken"].ShouldBe(Keys.ApplicationToken);
    }

    [Fact]
    public async Task Espera_la_URL_de_pago_consultando_hasta_que_aparece()
    {
        var calls = 0;
        var (gateway, fake) = Gateway((_, _) => ++calls < 3
            ? Json(new { data = new { _id = "t1", transactionStatus = "PendingForPaymentMethod" } })
            : Json(new { data = new { _id = "t1", transactionStatus = "PendingForPaymentMethod", paymentMethodResponse = new { paymentRedirectUrl = "https://pay.test/t1" } } }));

        var transaction = await gateway.WaitForRedirectUrlAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Staging), "t1", 8, TimeSpan.Zero, Ct);

        transaction.RedirectUrl.ShouldBe("https://pay.test/t1");
        fake.Calls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task La_espera_se_corta_con_un_estado_de_fallo_o_al_agotar_los_intentos()
    {
        var (rejected, rejectedCalls) = Gateway((_, _) => Json(new
        {
            data = new { _id = "t2", transactionStatus = "Rejected", paymentMethodResponse = new { description = "Cupo insuficiente" } },
        }));
        var failed = await rejected.WaitForRedirectUrlAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Staging), "t2", 8, TimeSpan.Zero, Ct);
        failed.Status.ShouldBe("Rejected");
        failed.Description.ShouldBe("Cupo insuficiente");
        rejectedCalls.Calls.Count.ShouldBe(1);

        var (slow, slowCalls) = Gateway((_, _) => Json(new { data = new { _id = "t3", transactionStatus = "Pending" } }));
        var last = await slow.WaitForRedirectUrlAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Staging), "t3", 3, TimeSpan.Zero, Ct);
        last.RedirectUrl.ShouldBeNull();
        slowCalls.Calls.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("Approved", SistecreditoAttemptStatus.Approved)]
    [InlineData("Rejected", SistecreditoAttemptStatus.Rejected)]
    [InlineData("Failed", SistecreditoAttemptStatus.Rejected)]
    [InlineData("Expired", SistecreditoAttemptStatus.Expired)]
    [InlineData("Cancelled", SistecreditoAttemptStatus.Cancelled)]
    [InlineData("Abandoned", SistecreditoAttemptStatus.Cancelled)]
    [InlineData("PendingForPaymentMethod", SistecreditoAttemptStatus.Pending)]
    [InlineData(null, SistecreditoAttemptStatus.Pending)]
    public void Estados_de_Sistecredito_a_estados_de_la_caja(string? providerStatus, SistecreditoAttemptStatus expected) =>
        SistecreditoProtocol.Map(providerStatus).ShouldBe(expected);

    [Fact]
    public void Las_llaves_se_limpian_de_la_etiqueta_que_se_arrastra_al_copiar_el_correo()
    {
        SistecreditoCredentials.Clean("Key " + new string('a', 32)).ShouldBe(new string('a', 32));
        SistecreditoCredentials.Clean("ID  " + new string('b', 24) + " ").ShouldBe(new string('b', 24));
        SistecreditoCredentials.Clean("\n" + new string('c', 24)).ShouldBe(new string('c', 24));
        SistecreditoCredentials.Clean("Subscription Key\t" + new string('d', 32)).ShouldBe(new string('d', 32));
        SistecreditoCredentials.Clean(null).ShouldBe(string.Empty);
        SistecreditoCredentials.Clean("   ").ShouldBe(string.Empty);
        Keys.ToString().ShouldNotContain(Keys.SubscriptionKey);
    }

    [Fact]
    public async Task Verificar_no_usa_el_simulador_y_manda_el_ambiente_elegido()
    {
        var (gateway, fake) = Gateway((_, _) => Json(new { data = new { _id = "abc" } }));

        var result = await gateway.VerifyCredentialsAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Ct);

        result.Ok.ShouldBeTrue();
        var call = fake.Calls.Single();
        call.Headers["SCOrigen"].ShouldBe("Production");
        using var body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("sandbox").GetProperty("isActive").GetBoolean().ShouldBeFalse(); // con el simulador responde 618 antes de mirar las llaves
        body.RootElement.GetProperty("invoice").GetString()!.ShouldStartWith("VERIF-");
    }

    [Fact]
    public async Task Verificar_con_685_en_los_dos_ambientes_da_el_mensaje_de_credenciales()
    {
        var (gateway, fake) = Gateway((_, _) => Json(new { errorCode = 685, message = "ImpossibleToRetrieveConfiguration error code: 601" }));

        var result = await gateway.VerifyCredentialsAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Ct);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("no reconoce las credenciales");
        result.Message.ShouldContain("ApplicationToken");
        result.WorkingEnvironment.ShouldBeNull();
        fake.Calls.Count.ShouldBe(2); // prueba el ambiente elegido y también el otro
    }

    [Theory]
    [InlineData(690)]
    [InlineData(691)]
    [InlineData(694)]
    public async Task Verificar_un_error_de_formato_de_las_llaves_se_avisa(int errorCode)
    {
        var (gateway, _) = Gateway((_, _) => Json(new { errorCode, message = "WrongFormat" }, HttpStatusCode.BadRequest));
        (await gateway.VerifyCredentialsAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Ct)).Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task Verificar_con_otro_error_las_llaves_si_se_reconocen()
    {
        var (gateway, fake) = Gateway((_, _) => Json(new { errorCode = 627, message = "DocumentRequired" }, HttpStatusCode.BadRequest));
        (await gateway.VerifyCredentialsAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Ct)).Ok.ShouldBeTrue();
        fake.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Verificar_sin_internet_no_dice_que_la_conexion_esta_bien()
    {
        var (gateway, _) = Gateway((_, _) => throw new HttpRequestException("ECONNRESET"));
        var result = await gateway.VerifyCredentialsAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Ct);
        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("No se pudo conectar con Sistecrédito");
    }

    [Fact]
    public async Task Verificar_detecta_llaves_del_otro_ambiente_y_dice_que_cambiar()
    {
        // Guardadas como Pruebas (Staging), pero son llaves de Producción.
        var (gateway, fake) = Gateway((headers, _) => headers["SCOrigen"] == "Staging"
            ? Json(new { errorCode = 685, message = "ImpossibleToRetrieveConfiguration error code: 601" })
            : Json(new { errorCode = 0, data = new { _id = "abc", transactionStatus = "Pending" } }, HttpStatusCode.Created));

        var result = await gateway.VerifyCredentialsAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Staging), Ct);

        result.Ok.ShouldBeFalse();
        result.WorkingEnvironment.ShouldBe(SistecreditoEnvironment.Production);
        result.Message.ShouldContain("NO funcionan en pruebas (Staging)");
        result.Message.ShouldContain("SÍ en producción");
        result.Message.ShouldContain("Cambie el ambiente a Producción");
        fake.Calls.Select(c => c.Headers["SCOrigen"]).ShouldBe(["Staging", "Production"]);
    }

    // Revisión de código M2: "Conectar" no puede dar por buenas unas llaves solo porque la respuesta no traiga errorCode.

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Verificar_con_401_o_403_de_la_puerta_de_enlace_son_llaves_rechazadas(HttpStatusCode status)
    {
        var (gateway, fake) = Gateway((_, _) => Json(new { statusCode = (int)status, message = "Access denied due to invalid subscription key." }, status));

        var result = await gateway.VerifyCredentialsAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Ct);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("no reconoce las credenciales");
        result.WorkingEnvironment.ShouldBeNull();
        fake.Calls.Count.ShouldBe(2); // también prueba el otro ambiente
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<html>Service Unavailable</html>")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.OK, "<html>portal cautivo</html>")]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Resource not found\"}")]
    public async Task Verificar_sin_una_respuesta_reconocible_de_Sistecredito_no_queda_conectado(HttpStatusCode status, string body)
    {
        var (gateway, fake) = Gateway((_, _) => new HttpResponseMessage(status) { Content = new StringContent(body) });

        var result = await gateway.VerifyCredentialsAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Ct);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("No se pudo verificar");
        result.Message.ShouldContain("Intente de nuevo");
        fake.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Verificar_con_401_en_el_ambiente_elegido_y_respuesta_valida_en_el_otro_dice_que_cambiar()
    {
        var (gateway, _) = Gateway((headers, _) => headers["SCOrigen"] == "Production"
            ? Json(new { statusCode = 401, message = "Access denied" }, HttpStatusCode.Unauthorized)
            : Json(new { errorCode = 0, data = new { _id = "abc" } }, HttpStatusCode.Created));

        var result = await gateway.VerifyCredentialsAsync(new SistecreditoConnection(Keys, SistecreditoEnvironment.Production), Ct);

        result.Ok.ShouldBeFalse();
        result.WorkingEnvironment.ShouldBe(SistecreditoEnvironment.Staging);
        result.Message.ShouldContain("Cambie el ambiente a Pruebas (Staging)");
    }

    // ─────────────────────────────── Simulador HTTP ───────────────────────────────

    private static (SistecreditoHttpGateway Gateway, FakeHandler Fake) Gateway(Func<IReadOnlyDictionary<string, string>, string, HttpResponseMessage> respond)
    {
        var fake = new FakeHandler(respond);
#pragma warning disable CA2000 // El HttpClient vive lo que dura la prueba.
        var gateway = new SistecreditoHttpGateway(new HttpClient(fake), new SistecreditoOptions(), TimeProvider.System);
#pragma warning restore CA2000
        return (gateway, fake);
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    internal sealed record Call(HttpMethod Method, string Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, string Body);

    internal sealed class FakeHandler(Func<IReadOnlyDictionary<string, string>, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Calls.Add(new Call(request.Method, request.RequestUri!.AbsoluteUri, headers, request.Content?.Headers.ContentType?.ToString(), body));
            return respond(headers, body);
        }
    }
}
