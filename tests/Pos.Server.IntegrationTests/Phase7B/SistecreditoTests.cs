using System.Collections.Concurrent;
using System.Net;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pos.Application.Abstractions.Licensing;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;
using Pos.Modules.Cash.Infrastructure.Sistecredito;
using Pos.Modules.Identity.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7B;

/// <summary>
/// Fase 7-B: cobrar con Sistecrédito en la caja de punta a punta (API, BD real, revisión automática) con un Sistecrédito simulado: nunca se
/// llama a la API real. La licencia se puede poner RESTRINGIDA para comprobar que el cobro sigue funcionando (RN-LIC-01).
/// </summary>
public class SistecreditoTests
{
    // Llaves de mentira con la etiqueta que se arrastra al copiar el correo: se guardan limpias y nunca vuelven en la API.
    private static readonly object Settings = new
    {
        subscriptionKey = "Subscription Key  " + new string('k', 28) + "9876",
        applicationKey = "Vendor ID " + new string('a', 24),
        applicationToken = new string('t', 24),
        environment = "STAGING",
        enabled = true,
    };

    [Fact]
    public async Task Solo_un_cobro_aprobado_completa_la_venta_una_vez_y_el_cierre_lo_cuenta_aparte_aun_con_la_licencia_restringida()
    {
        await using var factory = new SistecreditoServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 2);
        var due = sale.AmountDue;

        // Sin configurar no hay cobro; la configuración es de la administración.
        await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/cash/sistecredito/payments", Start(sale.Id, due))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_NOT_CONFIGURED");
        await RawAsync(shop.Cashier, HttpMethod.Put, "/api/v1/cash/sistecredito/settings", Settings)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        var methods = await GetAsync<List<PaymentMethodDto>>(shop.Owner, "/api/v1/cash/payment-methods?includeInactive=true");
        methods.Single(m => m.Code == "SISTECREDITO").Status.ShouldBe("INACTIVE");

        var settings = await SendAsync<SistecreditoSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/cash/sistecredito/settings", Settings);
        settings.IsEnabled.ShouldBeTrue();
        settings.HasCredentials.ShouldBeTrue();
        settings.KeyHint.ShouldBe("9876");
        settings.PaymentMethodActive.ShouldBeTrue();
        var raw = await (await shop.Owner.GetAsync("/api/v1/cash/sistecredito/settings", Ct)).Content.ReadAsStringAsync(Ct);
        raw.ShouldNotContain(new string('a', 24));
        raw.ShouldNotContain(new string('t', 24));
        var verification = await PostAsync<SistecreditoVerificationDto>(shop.Owner, "/api/v1/cash/sistecredito/settings/verify");
        verification.Ok.ShouldBeTrue();
        factory.Gateway.LastConnection!.Credentials.SubscriptionKey.ShouldBe(new string('k', 28) + "9876"); // limpia, sin la etiqueta
        factory.Gateway.LastConnection.Credentials.ApplicationKey.ShouldBe(new string('a', 24));
        factory.Gateway.LastConnection.Environment.ShouldBe(SistecreditoEnvironment.Staging);
        var sistecredito = (await GetAsync<List<PaymentMethodDto>>(shop.Owner, "/api/v1/cash/payment-methods")).Single(m => m.Code == "SISTECREDITO");
        sistecredito.Kind.ShouldBe("SISTECREDITO");
        sistecredito.AffectsCashDrawer.ShouldBeFalse();
        sistecredito.RequiresReference.ShouldBeTrue();

        // Licencia RESTRINGIDA: vender y cobrar con Sistecrédito sigue funcionando; administrar no.
        factory.License.Restricted = true;
        await RawAsync(shop.Owner, HttpMethod.Put, "/api/v1/cash/sistecredito/settings", Settings)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "LICENSE.RESTRICTED");

        // Sin un cobro aprobado la venta no se completa.
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/complete", new { payments = new[] { Pay(sistecredito.Id, due, "inventado") } })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_NOT_APPROVED");

        var payment = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, due, "1.020.304.050"));
        payment.Status.ShouldBe("PENDING");
        payment.RedirectUrl.ShouldNotBeNull();
        payment.MaskedDocument.ShouldBe("••••4050");
        payment.TransactionId.ShouldNotBeNull();
        factory.Gateway.Created.Last().DocumentNumber.ShouldBe("1020304050");
        factory.Gateway.Created.Last().Amount.ShouldBe(due);
        using (var qr = await shop.Cashier.GetAsync($"/api/v1/cash/sistecredito/payments/{payment.Id}/qr", Ct))
        {
            qr.StatusCode.ShouldBe(HttpStatusCode.OK);
            qr.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
            (await qr.Content.ReadAsStringAsync(Ct)).ShouldContain("<svg");
        }

        // Más de lo que falta no se cobra; pendiente tampoco completa la venta.
        await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/cash/sistecredito/payments", Start(sale.Id, due + 1_000m))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_AMOUNT_ABOVE_DUE");
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/complete", new { payments = new[] { Pay(sistecredito.Id, due, payment.TransactionId) } })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_NOT_APPROVED");
        (await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}/check")).Status.ShouldBe("PENDING");

        // El cliente aprueba en su celular: la caja lo ve en la siguiente consulta.
        factory.Gateway.SetStatus(payment.TransactionId!, "Approved");
        var approved = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}/check");
        approved.Status.ShouldBe("APPROVED");
        approved.Installments.ShouldBe(6);

        // Otro valor no sirve; el valor aprobado completa la venta y la referencia es el id de la transacción.
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/complete",
                new { payments = new[] { Pay(sistecredito.Id, due - 1_000m, payment.TransactionId), Pay(shop.Cash, 1_000m) } })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CASH.SISTECREDITO_AMOUNT_MISMATCH");
        var receipt = await shop.CompleteAsync(sale.Id, [Pay(sistecredito.Id, due, payment.TransactionId)]);
        receipt.Sale.Status.ShouldBe("COMPLETED");
        var paid = receipt.Sale.Payments.Single();
        paid.MethodCode.ShouldBe("SISTECREDITO");
        paid.Reference.ShouldBe(payment.TransactionId);
        receipt.OpenDrawer.ShouldBeFalse();
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{payment.Id}")).CompletedSaleId.ShouldBe(sale.Id);

        // El mismo cobro no paga otra venta.
        var other = await shop.StartAsync();
        other = await shop.AddAsync(other.Id, shop.Rice, 2);
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{other.Id}/complete", new { payments = new[] { Pay(sistecredito.Id, due, payment.TransactionId) } })
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "CASH.SISTECREDITO_ALREADY_USED");
        await shop.CompleteAsync(other.Id, [Pay(shop.Cash, other.AmountDue)]);

        // Cierre por medio: Sistecrédito aparece aparte, no es efectivo, con su transacción.
        var session = await GetAsync<CashSessionDto>(shop.Owner, $"/api/v1/cash/sessions/{receipt.Sale.CashSessionId}");
        var total = session.Totals.Single(t => t.Code == "SISTECREDITO");
        total.AffectsCashDrawer.ShouldBeFalse();
        total.Expected.ShouldBe(due);
        total.Transactions.ShouldBe(1);

        // La bitácora guarda el documento enmascarado (solo los últimos 4).
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        var summaries = (await connection.QueryAsync<string>("SELECT summary FROM audit.audit_log WHERE action LIKE 'SISTECREDITO_%'")).ToList();
        summaries.ShouldContain(s => s.Contains("••••4050", StringComparison.Ordinal));
        summaries.ShouldAllBe(s => !s.Contains("1020304050", StringComparison.Ordinal));
        (await shop.Catalog.ScalarAsync<string>("SELECT summary FROM audit.audit_log WHERE action = 'SISTECREDITO_SETTINGS_CHANGED'"))!
            .ShouldNotContain(new string('t', 24));
    }

    [Fact]
    public async Task Pago_mixto_rechazo_sin_internet_cancelar_y_la_revision_automatica_marca_los_aprobados_sin_venta()
    {
        await using var factory = new SistecreditoServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await SendAsync<SistecreditoSettingsDto>(shop.Owner, HttpMethod.Put, "/api/v1/cash/sistecredito/settings", Settings);
        var sistecredito = (await GetAsync<List<PaymentMethodDto>>(shop.Owner, "/api/v1/cash/payment-methods")).Single(m => m.Code == "SISTECREDITO").Id;
        await shop.OpenSessionAsync();

        // Pago mixto: 5.000 con Sistecrédito y el resto en efectivo con cambio.
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 3);
        sale.AmountDue.ShouldBe(9_000m);
        var part = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(sale.Id, 5_000m));
        factory.Gateway.SetStatus(part.TransactionId!, "Approved");
        (await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{part.Id}/check")).Status.ShouldBe("APPROVED");
        var receipt = await shop.CompleteAsync(sale.Id, [Pay(sistecredito, 5_000m, part.TransactionId), Pay(shop.Cash, 10_000m)]);
        receipt.Sale.Payments.Count.ShouldBe(2);
        receipt.Sale.ChangeTotal.ShouldBe(6_000m);

        // Rechazo: la caja ve el motivo en palabras simples.
        var second = await shop.StartAsync();
        second = await shop.AddAsync(second.Id, shop.Rice, 1);
        var rejected = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(second.Id, second.AmountDue));
        factory.Gateway.SetStatus(rejected.TransactionId!, "Rejected");
        rejected = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{rejected.Id}/check");
        rejected.Status.ShouldBe("REJECTED");
        rejected.Message!.ShouldContain("no aprobó");

        // Errores de Sistecrédito traducidos.
        factory.Gateway.CreateErrorCode = 739;
        var problem = await ProblemDetailAsync(
            await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/cash/sistecredito/payments", Start(second.Id, second.AmountDue)), "CASH.SISTECREDITO_REJECTED");
        problem.ShouldContain("no está disponible para este cliente");
        factory.Gateway.CreateErrorCode = null;

        // Sin internet: aviso claro al crear; consultar un cobro pendiente no falla (sigue intentando).
        var pending = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(second.Id, second.AmountDue));
        factory.Gateway.Offline = true;
        problem = await ProblemDetailAsync(
            await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/cash/sistecredito/payments", Start(second.Id, second.AmountDue)), "CASH.SISTECREDITO_OFFLINE");
        problem.ShouldStartWith("Sistecrédito necesita internet");
        var offline = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{pending.Id}/check");
        offline.Status.ShouldBe("PENDING");
        offline.Warning.ShouldNotBeNull();

        // Cancelar en la caja (sin internet igual se cancela); si el cliente aprueba después, la revisión automática lo marca.
        var cancelled = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{pending.Id}/cancel");
        cancelled.Status.ShouldBe("CANCELLED");
        factory.Gateway.Offline = false;
        factory.Gateway.SetStatus(pending.TransactionId!, "Approved");

        // Un cobro aprobado que nadie usó: si se vuelve a generar por el mismo valor, se reutiliza (no se cobra dos veces).
        var forgotten = await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(second.Id, 2_000m));
        factory.Gateway.SetStatus(forgotten.TransactionId!, "Approved");
        (await PostAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{forgotten.Id}/check")).Status.ShouldBe("APPROVED");
        (await PostAsync<SistecreditoPaymentDto>(shop.Cashier, "/api/v1/cash/sistecredito/payments", Start(second.Id, 2_000m))).Id.ShouldBe(forgotten.Id);

        // La caja olvida el cobro: 20 minutos después la revisión automática lo deja "Requiere revisión" con aviso para el dueño.
        (await ReconcileAsync(factory, DateTimeOffset.UtcNow.AddMinutes(20))).Changed.ShouldBe(2);
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{forgotten.Id}")).Status.ShouldBe("REVIEW_REQUIRED");
        (await GetAsync<SistecreditoPaymentDto>(shop.Cashier, $"/api/v1/cash/sistecredito/payments/{pending.Id}")).Status.ShouldBe("REVIEW_REQUIRED");
        (await GetAsync<MeDto>(shop.Owner, "/api/v1/auth/me")).SistecreditoReviews.ShouldBe(2);
        (await GetAsync<MeDto>(shop.Cashier, "/api/v1/auth/me")).SistecreditoReviews.ShouldBe(0);
        var reviews = await GetAsync<List<SistecreditoPaymentDto>>(shop.Owner, "/api/v1/cash/sistecredito/payments?status=REVIEW_REQUIRED");
        reviews.Count.ShouldBe(2);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'SISTECREDITO_REVIEW_REQUIRED'")).ShouldBe(2);

        // La administración lo cierra con una nota (p. ej. reversado en el portal de Sistecrédito).
        await RawAsync(shop.Owner, HttpMethod.Post, $"/api/v1/cash/sistecredito/payments/{forgotten.Id}/resolve", new { note = "ok" })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CASH.SISTECREDITO_REVIEW_NOTE_REQUIRED");
        foreach (var review in reviews)
        {
            (await PostAsync<SistecreditoPaymentDto>(shop.Owner, $"/api/v1/cash/sistecredito/payments/{review.Id}/resolve",
                new { note = "Reversado en el portal de Sistecrédito" })).ReviewedAt.ShouldNotBeNull();
        }

        (await GetAsync<MeDto>(shop.Owner, "/api/v1/auth/me")).SistecreditoReviews.ShouldBe(0);

        // Los cobros nunca se borran.
        await Should.ThrowAsync<PostgresException>(() => shop.Catalog.ExecuteAsync("DELETE FROM cash.sistecredito_attempts"));
    }

    internal static object Start(Guid saleId, decimal amount, string document = "1020304050") =>
        new { saleId, amount, documentType = "CC", documentNumber = document };

    /// <summary>Lo que hace el proceso en segundo plano, a la hora indicada.</summary>
    internal static async Task<(int Checked, int Changed)> ReconcileAsync(SistecreditoServerFactory factory, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SistecreditoReconciler>().RunAsync(now, Ct);
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        await context.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return result;
    }

    internal static async Task<string> ProblemDetailAsync(HttpResponseMessage response, string code)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        var problem = await Phase3.SecurityScenario.ProblemAsync(response);
        problem.GetProperty("code").GetString().ShouldBe(code);
        return problem.GetProperty("detail").GetString()!;
    }
}

/// <summary>Caja Única con Sistecrédito simulado, sin la revisión automática en segundo plano y con la licencia controlable.</summary>
public sealed class SistecreditoServerFactory : PosServerFactory
{
    public FakeSistecreditoGateway Gateway { get; } = new();

    public SwitchableLicenseGate License { get; } = new();

    protected override string Edition => "SINGLE";

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Pos:Sistecredito:Worker", "false"),
        new("Pos:Sistecredito:RedirectPollIntervalMilliseconds", "0"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISistecreditoGateway>();
            services.AddSingleton<ISistecreditoGateway>(Gateway);
            services.RemoveAll<ILicenseGate>();
            services.AddSingleton<ILicenseGate>(License);
        });
    }
}

public sealed class SwitchableLicenseGate : ILicenseGate
{
    public bool Restricted { get; set; }

    public LicenseGateState Current => Restricted ? new LicenseGateState(true, false, "Licencia restringida en la prueba.") : LicenseGateState.Unrestricted;
}

/// <summary>Sistecrédito simulado: crea transacciones, devuelve la URL de pago y el estado que la prueba fija.</summary>
public sealed class FakeSistecreditoGateway : ISistecreditoGateway
{
    private readonly ConcurrentDictionary<string, string> _statuses = new();
    private int _next;

    public ConcurrentQueue<SistecreditoCreateRequest> Created { get; } = new();

    public SistecreditoConnection? LastConnection { get; private set; }

    public bool Offline { get; set; }

    public int? CreateErrorCode { get; set; }

    /// <summary>Transacciones que Sistecrédito "ya no encuentra" (error 708).</summary>
    public ConcurrentDictionary<string, bool> NotFound { get; } = new();

    /// <summary>Consultas recibidas: transacción y ambiente (SCOrigen) con que se preguntó.</summary>
    public ConcurrentQueue<(string TransactionId, SistecreditoEnvironment Environment)> Queries { get; } = new();

    /// <summary>Se ejecuta justo antes de crear la transacción (p. ej. para mirar la base de datos en ese momento).</summary>
    public Func<SistecreditoCreateRequest, Task>? BeforeCreate { get; set; }

    public void SetStatus(string transactionId, string status) => _statuses[transactionId] = status;

    public async Task<SistecreditoCreated> CreateAsync(SistecreditoConnection connection, SistecreditoCreateRequest request, CancellationToken cancellationToken)
    {
        if (BeforeCreate is { } before)
        {
            await before(request);
        }

        Guard(connection);
        if (CreateErrorCode is { } code)
        {
            throw new SistecreditoGatewayException(SistecreditoHttpGateway.ErrorMessageFor(code, "Error"), SistecreditoFailure.Provider, code);
        }

        Created.Enqueue(request);
        var id = $"tx-{Interlocked.Increment(ref _next):D4}";
        _statuses[id] = "PendingForPaymentMethod";
        return new SistecreditoCreated(id, "Pending");
    }

    public Task<SistecreditoTransaction> GetTransactionAsync(SistecreditoConnection connection, string transactionId, CancellationToken cancellationToken)
    {
        Guard(connection);
        Queries.Enqueue((transactionId, connection.Environment));
        if (NotFound.ContainsKey(transactionId))
        {
            throw new SistecreditoGatewayException(
                SistecreditoHttpGateway.ErrorMessageFor(SistecreditoProtocol.NotFoundErrorCode, "Error"), SistecreditoFailure.Provider, SistecreditoProtocol.NotFoundErrorCode);
        }

        var status = _statuses.TryGetValue(transactionId, out var current) ? current : "Pending";
        return Task.FromResult(new SistecreditoTransaction(
            transactionId, status, $"https://pagos.sistecredito.test/{transactionId}", status == "Rejected" ? "Cupo insuficiente" : null,
            status == "Approved" ? 6 : null));
    }

    public Task<SistecreditoTransaction> WaitForRedirectUrlAsync(
        SistecreditoConnection connection, string transactionId, int attempts, TimeSpan interval, CancellationToken cancellationToken) =>
        GetTransactionAsync(connection, transactionId, cancellationToken);

    public Task<SistecreditoVerification> VerifyCredentialsAsync(SistecreditoConnection connection, CancellationToken cancellationToken)
    {
        LastConnection = connection;
        return Task.FromResult(new SistecreditoVerification(true, "Conexión correcta.", null));
    }

    private void Guard(SistecreditoConnection connection)
    {
        LastConnection = connection;
        if (Offline)
        {
            throw new SistecreditoGatewayException("Sin conexión con Sistecrédito.", SistecreditoFailure.Network, innerException: new HttpRequestException("sin red"));
        }
    }
}
