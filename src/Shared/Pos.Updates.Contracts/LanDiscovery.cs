using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Updates.Contracts;

/// <summary>
/// Descubrimiento del servidor de la tienda en la LAN (D13-07): la caja envía una difusión UDP y cada servidor Multicaja responde con su
/// nombre, dirección, puerto HTTPS y la huella de su certificado (que se compara con la que muestra el servidor antes de emparejar).
/// </summary>
public static class LanDiscovery
{
    public const int Port = 5444;

    public const string Request = "POS-DISCOVER/1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] RequestBytes => Encoding.ASCII.GetBytes(Request);

    public static bool IsRequest(ReadOnlySpan<byte> datagram) => datagram.SequenceEqual(RequestBytes);

    public static byte[] Serialize(DiscoveryResponse response) => JsonSerializer.SerializeToUtf8Bytes(response, Json);

    public static DiscoveryResponse? Parse(ReadOnlySpan<byte> datagram)
    {
        try
        {
            return JsonSerializer.Deserialize<DiscoveryResponse>(datagram, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Envía la difusión y recoge las respuestas durante <paramref name="wait"/>.</summary>
    public static async Task<IReadOnlyList<DiscoveredServer>> FindAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        await client.SendAsync(RequestBytes, new IPEndPoint(IPAddress.Broadcast, Port), cancellationToken);
        var found = new Dictionary<string, DiscoveredServer>(StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var received = await client.ReceiveAsync(timeout.Token);
                if (Parse(received.Buffer) is { } response)
                {
                    var address = received.RemoteEndPoint.Address.ToString();
                    found[address + response.CertificateThumbprint] = new DiscoveredServer(address, response);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Terminó la espera.
        }

        return [.. found.Values];
    }
}

/// <summary>Lo que responde un servidor Multicaja a la difusión.</summary>
public sealed record DiscoveryResponse(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("storeName")] string? StoreName,
    [property: JsonPropertyName("branchName")] string? BranchName,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("httpsPort")] int HttpsPort,
    [property: JsonPropertyName("certificateThumbprint")] string CertificateThumbprint,
    [property: JsonPropertyName("version")] string Version);

public sealed record DiscoveredServer(string Address, DiscoveryResponse Response)
{
    public string Url => $"https://{Address}:{Response.HttpsPort}/";
}
