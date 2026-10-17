using System.Security.Cryptography;
using System.Text;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using Pos.SharedKernel.Measurement;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7C;

/// <summary>
/// Hace de agente de la caja en las pruebas del servidor (D3): crea su clave, la registra en el servidor como lo hace "Vincular báscula"
/// en la pantalla de venta (sesión de la caja con autorización del supervisor, BB2) y firma las lecturas con el mismo formato que el agente
/// real: hora del reloj (por defecto la del servidor), arranque y secuencia creciente.
/// </summary>
public sealed class ScaleAgentDouble : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private long _seq;
    private long? _lastEmptySeq;

    public ScaleAgentDouble(Guid terminalId) => TerminalId = terminalId;

    public Guid TerminalId { get; }

    public Guid KeyId { get; } = Guid.NewGuid();

    /// <summary>Arranque del agente (cambiarlo simula un reinicio: la secuencia empieza de nuevo).</summary>
    public Guid BootId { get; private set; } = Guid.NewGuid();

    /// <summary>Número de arranque (BQ3): sube con cada reinicio.</summary>
    public long BootCounter { get; private set; } = 1;

    /// <summary>Desfase del reloj del agente frente al reloj real (salto del reloj del equipo).</summary>
    public TimeSpan ClockOffset { get; set; }

    /// <summary>Configura la báscula de la caja (administración) y la vincula desde la caja con el supervisor.</summary>
    public static async Task<ScaleAgentDouble> PairAsync(SalesScenario shop)
    {
        ArgumentNullException.ThrowIfNull(shop);
        var terminal = (await GetAsync<List<TerminalDto>>(shop.Owner, "/api/v1/organization/terminals")).Single();
        await SendAsync<TerminalScaleDto>(shop.Owner, HttpMethod.Put, $"/api/v1/organization/terminals/{terminal.Id}/scale", new
        {
            enabled = true, driver = "TOLEDO_8217", port = "COM3", stableWaitSeconds = 3, emptyWaitSeconds = 5, allowManualWeight = true,
        });
        var agent = new ScaleAgentDouble(terminal.Id);
        await agent.RegisterKeyAsync(shop);
        return agent;
    }

    /// <summary>Cuerpo de <c>PUT …/scale/reading-key</c> con la clave pública de este agente.</summary>
    public object KeyBody => new { keyId = KeyId, publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()) };

    /// <summary>Registra la clave pública desde la caja (sesión de caja; el cajero pide la autorización del supervisor).</summary>
    public async Task RegisterKeyAsync(SalesScenario shop)
    {
        ArgumentNullException.ThrowIfNull(shop);
        var linked = await shop.AuthorizedAsync<TerminalScaleDto>(HttpMethod.Put, $"/api/v1/organization/terminals/{TerminalId}/scale/reading-key", KeyBody);
        linked.ReadingKeyId.ShouldBe(KeyId);
    }

    /// <summary>La caja de las pruebas envía la versión de la interfaz que firma las lecturas (BN10).</summary>
    public static void UseCurrentClient(HttpClient cashier)
    {
        ArgumentNullException.ThrowIfNull(cashier);
        cashier.DefaultRequestHeaders.Remove(PosClientBuild.Header);
        cashier.DefaultRequestHeaders.Add(PosClientBuild.Header, PosClientBuild.SignedScaleReadings.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>El agente vio la báscula vacía (una lectura con el bruto en la banda de cero).</summary>
    public void Empty() => _lastEmptySeq = ++_seq;

    /// <summary>Se reinstaló el agente o se restauró el equipo: el número de arranque vuelve a 1 (BS1).</summary>
    public void Reinstall()
    {
        Restart();
        BootCounter = 1;
    }

    /// <summary>El agente se reinició: otro arranque, la secuencia empieza de nuevo y todavía no ha visto la báscula vacía.</summary>
    public void Restart()
    {
        BootId = Guid.NewGuid();
        BootCounter++;
        _seq = 0;
        _lastEmptySeq = null;
    }

    /// <summary>Una lectura firmada (cuerpo <c>scale</c> de la línea) y su contenido.</summary>
    public (object Body, ScaleReadingPayload Payload) Read(
        decimal grossKg, decimal tareKg = 0m, bool net = false, bool stable = true, DateTimeOffset? at = null, Guid? terminalId = null, ECDsa? key = null,
        long? bootCounter = null, Guid? bootId = null)
    {
        var payload = new ScaleReadingPayload(
            terminalId ?? TerminalId, Guid.CreateVersion7(), at ?? DateTimeOffset.UtcNow + ClockOffset, bootId ?? BootId, bootCounter ?? BootCounter, ++_seq, grossKg, tareKg, grossKg - tareKg, stable,
            net, stable ? "OK" : "UNSTABLE", _lastEmptySeq, true);
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
