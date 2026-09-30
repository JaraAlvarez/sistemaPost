using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pos.Modules.Billing.FactusFake;
using Pos.Modules.Billing.Infrastructure.Factus;

namespace Pos.Modules.Billing.Factus.Tests;

public class FactusApiClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Token ----

    [Fact]
    public async Task Autentica_una_vez_y_reutiliza_el_token_en_memoria()
    {
        await using var h = FactusHarness.Create();

        (await h.Api.CreateBillAsync(FactusSamples.Bill("ref-1"), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);
        (await h.Api.CreateBillAsync(FactusSamples.Bill("ref-2"), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);

        (h.Server.PasswordGrants, h.Server.RefreshGrants).ShouldBe((1, 0));
        h.Server.Requests.Where(r => r.PathAndQuery.StartsWith("v2/", StringComparison.Ordinal)).ShouldAllBe(r => r.HadBearerToken);
    }

    [Fact]
    public async Task Renueva_con_el_refresh_token_antes_de_que_venza()
    {
        await using var h = FactusHarness.Create();
        await h.Api.CreateBillAsync(FactusSamples.Bill("ref-1"), Ct);

        h.Time.Advance(TimeSpan.FromMinutes(59)); // dentro del margen de 2 minutos antes del vencimiento
        (await h.Api.CreateBillAsync(FactusSamples.Bill("ref-2"), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);

        (h.Server.PasswordGrants, h.Server.RefreshGrants).ShouldBe((1, 1));
    }

    [Fact]
    public async Task Si_el_refresh_token_es_rechazado_vuelve_a_autenticar_con_usuario_y_clave()
    {
        await using var h = FactusHarness.Create();
        await h.Api.CreateBillAsync(FactusSamples.Bill("ref-1"), Ct);
        h.Server.RevokeRefreshTokens();
        h.Time.Advance(TimeSpan.FromHours(2));

        (await h.Api.CreateBillAsync(FactusSamples.Bill("ref-2"), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);

        (h.Server.PasswordGrants, h.Server.RefreshGrants).ShouldBe((2, 0));
    }

    [Fact]
    public async Task Un_401_renueva_el_token_y_reintenta_una_sola_vez_sin_duplicar()
    {
        await using var h = FactusHarness.Create();
        await h.Api.CreateBillAsync(FactusSamples.Bill("ref-1"), Ct);
        h.Server.ExpireAllTokens(); // el cliente cree que su token sigue vigente

        var result = await h.Api.CreateBillAsync(FactusSamples.Bill("ref-2"), Ct);

        result.Outcome.ShouldBe(FactusOutcome.Accepted);
        h.Server.RefreshGrants.ShouldBe(1);
        h.Server.Requests.Count(r => r.PathAndQuery == "v2/bills/validate").ShouldBe(3); // ref-1, ref-2 con 401, ref-2 de nuevo
        h.Server.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Un_401_persistente_es_error_de_credenciales_tras_un_unico_reintento()
    {
        await using var h = FactusHarness.Create(s => s.RejectAllTokens = true);

        var result = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        result.Outcome.ShouldBe(FactusOutcome.CredentialsError);
        result.HttpStatus.ShouldBe(401);
        h.Server.Requests.Count(r => r.PathAndQuery == "v2/bills/validate").ShouldBe(2);
    }

    [Fact]
    public async Task Credenciales_invalidas_no_llegan_a_la_API()
    {
        await using var h = FactusHarness.Create(options: FactusHarness.Options(FactusFakeServer.InMemoryBaseUrl, password: "clave-equivocada"));

        var result = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);
        var auth = await h.Api.AuthenticateAsync(Ct);

        result.Outcome.ShouldBe(FactusOutcome.CredentialsError);
        auth.Status.ShouldBe(FactusQueryStatus.CredentialsError);
        h.Server.Requests.ShouldNotContain(r => r.PathAndQuery.StartsWith("v2/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Autenticar_devuelve_el_vencimiento_sin_exponer_el_token()
    {
        await using var h = FactusHarness.Create();

        var auth = await h.Api.AuthenticateAsync(Ct);

        auth.Status.ShouldBe(FactusQueryStatus.Found);
        auth.Value!.ExpiresAt.ShouldBe(h.Time.GetUtcNow().AddHours(1));
    }

    [Fact]
    public async Task Sin_configuracion_responde_error_de_credenciales()
    {
        await using var h = FactusHarness.Create(configured: false);

        var result = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        result.Outcome.ShouldBe(FactusOutcome.CredentialsError);
        result.Detail!.ShouldContain("no está configurado");
        h.Server.Requests.ShouldBeEmpty();
    }

    // ---- Emisión e idempotencia ----

    [Fact]
    public async Task Factura_aceptada_trae_numero_CUFE_QR_y_notificaciones_DIAN()
    {
        await using var h = FactusHarness.Create();

        var result = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        result.Outcome.ShouldBe(FactusOutcome.Accepted);
        result.IsSuccess.ShouldBeTrue();
        result.HttpStatus.ShouldBe(201);
        var document = result.Document!;
        document.Number.ShouldBe("SETP990000001");
        document.ReferenceCode.ShouldBe(FactusSamples.SaleReference);
        document.Cufe!.Length.ShouldBe(96);
        document.QrUrl!.ShouldContain(document.Cufe);
        document.IsValidated.ShouldBeTrue();
        document.ValidatedAt.ShouldNotBeNull();
        // FAJ44b es una notificación: no invalida la factura.
        document.DianMessages.ShouldContain(m => m.Code == "FAJ44b" && !m.IsRejection);
        document.RawJson!.ShouldContain("\"number\":\"SETP990000001\"");
    }

    [Fact]
    public async Task Reenviar_el_mismo_reference_code_devuelve_el_documento_existente()
    {
        await using var h = FactusHarness.Create();
        var first = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        var second = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        second.Outcome.ShouldBe(FactusOutcome.Duplicate);
        second.IsSuccess.ShouldBeTrue();
        (second.Document!.Number, second.Document.Cufe).ShouldBe((first.Document!.Number, first.Document.Cufe));
        h.Server.Documents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Un_409_por_reference_code_repetido_consulta_y_devuelve_el_existente()
    {
        await using var h = FactusHarness.Create(s => s.DuplicateBehavior = DuplicateReferenceBehavior.Conflict);
        var first = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        var second = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        second.Outcome.ShouldBe(FactusOutcome.Duplicate);
        second.HttpStatus.ShouldBe(409);
        second.Document!.Number.ShouldBe(first.Document!.Number);
        second.Document.IsValidated.ShouldBeTrue();
        h.Server.Requests.ShouldContain(r => r.PathAndQuery == $"v2/bills?filter[reference_code]={FactusSamples.SaleReference}");
        h.Server.Documents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Rechazo_DIAN_trae_los_mensajes_bloquea_y_se_corrige_eliminando_y_reenviando()
    {
        await using var h = FactusHarness.Create();
        h.Server.RejectNext("FAK24", "No está informado el DV del NIT");

        var rejected = await h.Api.CreateBillAsync(FactusSamples.Bill("venta-1"), Ct);

        rejected.Outcome.ShouldBe(FactusOutcome.Rejected);
        rejected.BlocksFurtherSubmissions.ShouldBeTrue();
        rejected.Messages.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            m => m.Code.ShouldBe("FAK24"),
            m => m.IsRejection.ShouldBeTrue(),
            m => m.Message.ShouldContain("No está informado el DV del NIT"));

        // Mientras exista el rechazado, Factus bloquea los envíos siguientes (409 "pendiente por enviar a la DIAN").
        var blocked = await h.Api.CreateBillAsync(FactusSamples.Bill("venta-2"), Ct);
        blocked.Outcome.ShouldBe(FactusOutcome.Rejected);
        blocked.HttpStatus.ShouldBe(409);
        blocked.BlocksFurtherSubmissions.ShouldBeTrue();
        blocked.Messages.Single().Message.ShouldContain("pendiente por enviar a la DIAN");

        (await h.Api.DeleteUnvalidatedAsync(FactusDocumentKind.Bill, "venta-1", Ct)).IsFound.ShouldBeTrue();
        (await h.Api.CreateBillAsync(FactusSamples.Bill("venta-1"), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);
        (await h.Api.CreateBillAsync(FactusSamples.Bill("venta-2"), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);
    }

    [Fact]
    public async Task Reenviar_un_rechazado_sin_eliminarlo_devuelve_el_rechazo_existente()
    {
        await using var h = FactusHarness.Create(s => s.RejectionStatusCode = 422);
        h.Server.RejectNext("FAD06", "Número de documento duplicado");

        var first = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);
        var again = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        first.Outcome.ShouldBe(FactusOutcome.Rejected);
        first.HttpStatus.ShouldBe(422);
        first.Messages.Single().Code.ShouldBe("FAD06");
        again.Outcome.ShouldBe(FactusOutcome.Rejected);
        again.HttpStatus.ShouldBe(409);
        again.Document!.ReferenceCode.ShouldBe(FactusSamples.SaleReference);
        again.Messages.ShouldContain(m => m.Message.Contains("FAD06", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Error_de_validacion_422_devuelve_los_campos_con_problema()
    {
        await using var h = FactusHarness.Create();
        var customer = FactusSamples.FinalConsumer() with { Names = null };

        var result = await h.Api.CreateBillAsync(FactusSamples.Bill(customer: customer), Ct);

        result.Outcome.ShouldBe(FactusOutcome.Rejected);
        result.HttpStatus.ShouldBe(422);
        result.BlocksFurtherSubmissions.ShouldBeFalse();
        result.Messages.ShouldContain(m => m.Code == "customer.names");
        h.Server.Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task DIAN_demorada_queda_pendiente_y_el_reenvio_con_los_mismos_datos_la_valida()
    {
        await using var h = FactusHarness.Create();
        h.Server.DelayValidationNext();

        var pending = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);
        var retried = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        pending.Outcome.ShouldBe(FactusOutcome.Pending);
        pending.Document!.IsValidated.ShouldBeFalse();
        pending.Document.Number.ShouldNotBeNull();
        retried.IsSuccess.ShouldBeTrue();
        retried.Document!.Number.ShouldBe(pending.Document.Number);
        h.Server.Documents.Count.ShouldBe(1);
    }

    // ---- Errores transitorios ----

    [Fact]
    public async Task Un_429_es_transitorio_con_Retry_After_y_el_reintento_no_duplica()
    {
        await using var h = FactusHarness.Create();
        h.Server.EnqueueFault(FakeFault.TooManyRequests(TimeSpan.FromSeconds(30)));

        var throttled = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);
        var retried = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        throttled.Outcome.ShouldBe(FactusOutcome.TransientError);
        throttled.HttpStatus.ShouldBe(429);
        throttled.RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
        retried.Outcome.ShouldBe(FactusOutcome.Accepted);
        h.Server.Documents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task El_limite_de_80_por_minuto_responde_429_hasta_que_pasa_el_minuto()
    {
        await using var h = FactusHarness.Create(s => s.RateLimitPerMinute = 3);
        for (var i = 0; i < 3; i++)
            (await h.Api.CreateBillAsync(FactusSamples.Bill($"ref-{i}"), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);

        var limited = await h.Api.CreateBillAsync(FactusSamples.Bill("ref-3"), Ct);
        limited.Outcome.ShouldBe(FactusOutcome.TransientError);
        limited.RetryAfter.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(1));

        h.Time.Advance(TimeSpan.FromMinutes(1));
        (await h.Api.CreateBillAsync(FactusSamples.Bill("ref-3"), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Los_5xx_son_transitorios(int status)
    {
        await using var h = FactusHarness.Create();
        h.Server.EnqueueFault(FakeFault.ServerError(status));

        var failed = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        failed.Outcome.ShouldBe(FactusOutcome.TransientError);
        failed.HttpStatus.ShouldBe(status);
        (await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);
    }

    [Fact]
    public async Task Una_caida_de_red_es_transitoria()
    {
        await using var h = FactusHarness.Create();
        h.Server.EnqueueFault(FakeFault.ConnectionDrop());

        var failed = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        failed.Outcome.ShouldBe(FactusOutcome.TransientError);
        failed.Detail!.ShouldContain("Sin conexión");
        (await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct)).Outcome.ShouldBe(FactusOutcome.Accepted);
    }

    [Fact]
    public async Task La_latencia_mayor_al_tiempo_de_espera_es_transitoria()
    {
        await using var h = FactusHarness.Create(
            s => s.Latency = TimeSpan.FromSeconds(5),
            options: FactusHarness.Options(FactusFakeServer.InMemoryBaseUrl, timeout: TimeSpan.FromMilliseconds(200)));

        var result = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        result.Outcome.ShouldBe(FactusOutcome.TransientError);
        result.Detail!.ShouldContain("a tiempo");
    }

    [Fact]
    public async Task La_cancelacion_del_llamador_se_propaga()
    {
        await using var h = FactusHarness.Create(s => s.Latency = TimeSpan.FromSeconds(5));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => h.Api.CreateBillAsync(FactusSamples.Bill(), cts.Token));
    }

    // ---- Nota crédito, documento soporte, rangos, consultas y descargas ----

    [Fact]
    public async Task Nota_credito_referencia_la_factura_aceptada()
    {
        await using var h = FactusHarness.Create();
        var bill = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        var note = await h.Api.CreateCreditNoteAsync(FactusSamples.CreditNote(bill.Document!.Number!), Ct);
        var orphan = await h.Api.CreateCreditNoteAsync(FactusSamples.CreditNote("SETP999", "otra-nota"), Ct);

        note.Outcome.ShouldBe(FactusOutcome.Accepted);
        note.Document!.Number.ShouldBe("NC1");
        note.Document.Cufe.ShouldNotBeNullOrEmpty(); // CUDE
        note.Document.Kind.ShouldBe(FactusDocumentKind.CreditNote);
        orphan.Outcome.ShouldBe(FactusOutcome.Rejected);
        orphan.Messages.ShouldContain(m => m.Code == "bill_number");
    }

    [Fact]
    public async Task Documento_soporte_aceptado()
    {
        await using var h = FactusHarness.Create();

        var result = await h.Api.CreateSupportDocumentAsync(FactusSamples.SupportDocument(), Ct);

        result.Outcome.ShouldBe(FactusOutcome.Accepted);
        result.Document!.Number.ShouldBe("DS1");
        result.Document.Cufe.ShouldNotBeNullOrEmpty(); // CUDS
        result.Document.Total.ShouldBe(150_000m);
    }

    [Fact]
    public async Task Lista_los_rangos_de_numeracion_con_filtros()
    {
        await using var h = FactusHarness.Create(s => s.NumberingRanges.Add(
            new FakeNumberingRange { Id = 11, Document = "21", Prefix = "SUC2", IsActive = false }));

        var all = await h.Api.GetNumberingRangesAsync(onlyActive: false, null, Ct);
        var activeInvoices = await h.Api.GetNumberingRangesAsync(onlyActive: true, FactusCodes.RangeInvoice, Ct);

        all.Value!.Select(r => r.Id).ShouldBe([8, 9, 10, 11]);
        var range = activeInvoices.Value.ShouldHaveSingleItem();
        (range.Id, range.Prefix, range.From, range.To, range.Current, range.IsActive, range.IsExpired)
            .ShouldBe((8, "SETP", 990000000L, 995000000L, 990000001L, true, false));
        (range.StartDate, range.EndDate).ShouldBe((new DateOnly(2026, 1, 1), new DateOnly(2027, 12, 31)));
        range.ResolutionNumber.ShouldBe("18760000001");
        h.Server.Requests.ShouldContain(r => r.PathAndQuery == "v2/numbering-ranges?filter[is_active]=1&filter[document]=21");
    }

    [Fact]
    public async Task Varios_rangos_activos_exigen_numbering_range_id()
    {
        await using var h = FactusHarness.Create(s => s.NumberingRanges.Add(new FakeNumberingRange { Id = 12, Document = "21", Prefix = "SUC2" }));

        var withoutRange = await h.Api.CreateBillAsync(FactusSamples.Bill() with { NumberingRangeId = null }, Ct);
        var branch2 = await h.Api.CreateBillAsync(FactusSamples.Bill("sucursal-2") with { NumberingRangeId = 12 }, Ct);

        withoutRange.Outcome.ShouldBe(FactusOutcome.Rejected);
        withoutRange.Messages.ShouldContain(m => m.Code == "numbering_range_id");
        branch2.Document!.Number.ShouldBe("SUC21");
    }

    [Fact]
    public async Task Consulta_por_numero_y_por_reference_code()
    {
        await using var h = FactusHarness.Create();
        var created = await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        var byNumber = await h.Api.GetDocumentAsync(FactusDocumentKind.Bill, created.Document!.Number!, Ct);
        var byReference = await h.Api.FindByReferenceAsync(FactusDocumentKind.Bill, FactusSamples.SaleReference, Ct);
        var missing = await h.Api.GetDocumentAsync(FactusDocumentKind.Bill, "SETP1", Ct);
        var missingReference = await h.Api.FindByReferenceAsync(FactusDocumentKind.Bill, "no-existe", Ct);

        byNumber.Value!.Cufe.ShouldBe(created.Document.Cufe);
        byNumber.Value.IsValidated.ShouldBeTrue();
        byReference.Value!.Number.ShouldBe(created.Document.Number);
        byReference.Value.IsValidated.ShouldBeTrue();
        missing.Status.ShouldBe(FactusQueryStatus.NotFound);
        missingReference.Status.ShouldBe(FactusQueryStatus.NotFound);
    }

    [Fact]
    public async Task Descarga_el_PDF_y_el_XML_decodificados()
    {
        await using var h = FactusHarness.Create();
        var number = (await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct)).Document!.Number!;

        var pdf = await h.Api.DownloadPdfAsync(FactusDocumentKind.Bill, number, Ct);
        var xml = await h.Api.DownloadXmlAsync(FactusDocumentKind.Bill, number, Ct);

        pdf.Value!.ContentType.ShouldBe("application/pdf");
        pdf.Value.FileName.ShouldBe($"fv0{number}.pdf");
        Encoding.ASCII.GetString(pdf.Value.Content).ShouldStartWith("%PDF-");
        xml.Value!.FileName.ShouldEndWith(".xml");
        Encoding.UTF8.GetString(xml.Value.Content).ShouldContain($"<ID>{number}</ID>");
    }

    [Fact]
    public async Task No_se_puede_eliminar_un_documento_validado()
    {
        await using var h = FactusHarness.Create();
        await h.Api.CreateBillAsync(FactusSamples.Bill(), Ct);

        var result = await h.Api.DeleteUnvalidatedAsync(FactusDocumentKind.Bill, FactusSamples.SaleReference, Ct);

        result.Status.ShouldBe(FactusQueryStatus.Failed);
        result.HttpStatus.ShouldBe(409);
        h.Server.Documents.Count.ShouldBe(1);
    }

    // ---- Seguridad y transporte real ----

    [Fact]
    public async Task Los_logs_nunca_contienen_credenciales_ni_tokens()
    {
        await using var h = FactusHarness.Create();
        await h.Api.CreateBillAsync(FactusSamples.Bill("ref-1"), Ct);
        h.Server.ExpireAllTokens();
        await h.Api.CreateBillAsync(FactusSamples.Bill("ref-2"), Ct);

        h.Logs.Messages.ShouldNotBeEmpty();
        foreach (var secret in new[] { FactusFakeServer.DefaultPassword, FactusFakeServer.DefaultClientSecret, "fake-access-", "fake-refresh-" })
            h.Logs.Messages.ShouldAllBe(m => !m.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Funciona_contra_el_simulado_en_un_puerto_real()
    {
        await using var server = new FactusFakeServer();
        var baseUrl = await server.StartAsync(Ct);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IFactusOptionsProvider>(new StaticFactusOptionsProvider(FactusHarness.Options(baseUrl)));
        services.AddFactusApi();
        await using var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<IFactusApi>();

        var result = await api.CreateBillAsync(FactusSamples.Bill(), Ct);
        server.EnqueueFault(FakeFault.ConnectionDrop());
        var dropped = await api.CreateBillAsync(FactusSamples.Bill("otra"), Ct);

        baseUrl.Host.ShouldBe("127.0.0.1");
        result.Outcome.ShouldBe(FactusOutcome.Accepted);
        dropped.Outcome.ShouldBe(FactusOutcome.TransientError);
    }
}
