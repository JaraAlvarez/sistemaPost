using System.Security.Cryptography;
using System.Text;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.SharedKernel.Measurement;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7C;

/// <summary>
/// Hace de agente de la caja en las pruebas del servidor (D3): crea su clave, la registra en el servidor como lo hace "Guardar" en
/// Configuración → Cajas y firma las lecturas con el mismo formato que el agente real. Su reloj avanza un segundo por lectura.
/// </summary>
public sealed class ScaleAgentDouble : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private DateTimeOffset _clock = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset? _lastEmpty;

    public ScaleAgentDouble(Guid terminalId) => TerminalId = terminalId;

    public Guid TerminalId { get; }

    public Guid KeyId { get; } = Guid.NewGuid();

    public DateTimeOffset Clock => _clock;

    /// <summary>Configura la báscula de la caja y registra la clave (vincular).</summary>
    public static async Task<ScaleAgentDouble> PairAsync(HttpClient owner)
    {
        var terminal = (await GetAsync<List<TerminalDto>>(owner, "/api/v1/organization/terminals")).Single();
        var url = $"/api/v1/organization/terminals/{terminal.Id}/scale";
        await SendAsync<TerminalScaleDto>(owner, HttpMethod.Put, url, new
        {
            enabled = true, driver = "TOLEDO_8217", port = "COM3", stableWaitSeconds = 3, emptyWaitSeconds = 5, allowManualWeight = true,
        });
        var agent = new ScaleAgentDouble(terminal.Id);
        await agent.RegisterKeyAsync(owner);
        return agent;
    }

    /// <summary>Registra la clave pública en el servidor (lo que hace "Guardar" en el computador de la caja).</summary>
    public async Task RegisterKeyAsync(HttpClient owner)
    {
        var linked = await SendAsync<TerminalScaleDto>(owner, HttpMethod.Put, $"/api/v1/organization/terminals/{TerminalId}/scale/reading-key", new
        {
            keyId = KeyId, publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
        });
        linked.ReadingKeyId.ShouldBe(KeyId);
    }

    /// <summary>La caja de las pruebas envía la versión de la interfaz que firma las lecturas (BN10).</summary>
    public static void UseCurrentClient(HttpClient cashier)
    {
        cashier.DefaultRequestHeaders.Remove(PosClientBuild.Header);
        cashier.DefaultRequestHeaders.Add(PosClientBuild.Header, PosClientBuild.SignedScaleReadings.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>La báscula quedó vacía (el agente lo vio) en este instante.</summary>
    public void Empty()
    {
        _clock = _clock.AddMilliseconds(500);
        _lastEmpty = _clock;
    }

    /// <summary>Una lectura firmada (cuerpo <c>scale</c> de la línea) y su contenido.</summary>
    public (object Body, ScaleReadingPayload Payload) Read(
        decimal grossKg, decimal tareKg = 0m, bool net = false, bool stable = true, DateTimeOffset? at = null, Guid? terminalId = null, ECDsa? key = null)
    {
        _clock = _clock.AddSeconds(1);
        var payload = new ScaleReadingPayload(
            terminalId ?? TerminalId, Guid.CreateVersion7(), at ?? _clock, grossKg, tareKg, grossKg - tareKg, stable, net, stable ? "OK" : "UNSTABLE", _lastEmpty,
            true);
        return (Sign(payload, key), payload);
    }

    public object Sign(ScaleReadingPayload payload, ECDsa? key = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var text = payload.Format();
        var signature = (key ?? _key).SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new { payload = text, signature = Convert.ToBase64String(signature) };
    }

    public void Dispose() => _key.Dispose();
}
