using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.Licensing.Domain;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Pasarela simulada (sin red): verifica los eventos y firma el checkout con las MISMAS reglas de Wompi
/// (<see cref="WompiProtocol"/>) usando secretos de prueba, y "consulta" transacciones guardadas en memoria. Nunca usa llaves reales.
/// </summary>
public sealed class FakePaymentGateway : IPaymentGateway
{
    public const string EventsSecret = "secreto-eventos-de-prueba";
    public const string IntegritySecret = "secreto-integridad-de-prueba";
    public const string PublicKey = "llave-publica-de-prueba";
    public const string CheckoutUrl = "https://checkout.wompi.co/p/";

    private static readonly string[] SignedProperties = ["transaction.id", "transaction.status", "transaction.amount_in_cents"];

    private readonly ConcurrentDictionary<string, GatewayTransaction> _transactions = new(StringComparer.Ordinal);

    public string Name => PaymentRules.Gateway;

    public bool IsConfigured => true;

    public string Environment => "Sandbox";

    public string? CheckoutOrigin => "https://checkout.wompi.co";

    /// <summary>Simula que la pasarela no responde a las consultas.</summary>
    public bool Unavailable { get; set; }

    public void Set(GatewayTransaction transaction) => _transactions[transaction.Id] = transaction;

    public string BuildCheckoutUrl(CheckoutRequest request) => WompiProtocol.CheckoutUrl(CheckoutUrl, PublicKey, IntegritySecret, request);

    public GatewayEvent ParseEvent(string body, string? checksumHeader) => WompiProtocol.ParseEvent(body, checksumHeader, EventsSecret);

    public Task<GatewayTransaction?> GetTransactionAsync(string transactionId, CancellationToken cancellationToken) =>
        Task.FromResult(Unavailable ? null : _transactions.GetValueOrDefault(transactionId));

    public Task<IReadOnlyList<GatewayTransaction>?> FindByReferenceAsync(string reference, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GatewayTransaction>?>(Unavailable ? null : [.. _transactions.Values.Where(t => t.Reference == reference)]);

    /// <summary>Cuerpo de un evento <c>transaction.updated</c> firmado como lo hace Wompi (o con otro secreto, para probar el rechazo).</summary>
    public static string SignedEvent(GatewayTransaction transaction, string secret = EventsSecret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var checksum = WompiProtocol.Sha256Hex(string.Concat(
            transaction.Id, transaction.Status, transaction.AmountInCents.ToString(CultureInfo.InvariantCulture), timestamp.ToString(CultureInfo.InvariantCulture), secret));
        return JsonSerializer.Serialize(new
        {
            @event = "transaction.updated",
            data = new
            {
                transaction = new
                {
                    id = transaction.Id,
                    amount_in_cents = transaction.AmountInCents,
                    reference = transaction.Reference,
                    currency = transaction.Currency,
                    payment_method_type = transaction.PaymentMethodType,
                    status = transaction.Status,
                },
            },
            environment = "test",
            signature = new { properties = SignedProperties, checksum },
            timestamp,
            sent_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
    }
}

/// <summary>Servidor de la nube con el pago en línea "configurado" sobre la pasarela simulada.</summary>
public sealed class PaymentsCloudServerFactory : CloudServerFactory
{
    public FakePaymentGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(Gateway);
        });
    }
}

/// <summary>Un servidor con pagos simulados por clase de pruebas, con el superadministrador ya dentro.</summary>
public sealed class PaymentsCloudFixture : IAsyncLifetime
{
    public PaymentsCloudServerFactory Factory { get; } = new();

    public HttpClient Superadmin { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Factory.StartAsync();
        (Superadmin, _) = await PortalApi.BootstrapSuperadminAsync(Factory);
    }

    public async ValueTask DisposeAsync()
    {
        Superadmin.Dispose();
        await Factory.DisposeAsync();
    }
}
