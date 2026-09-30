using System.Net;
using System.Text;

namespace Pos.Modules.Billing.FactusFake;

/// <summary>Manejador HTTP en memoria que entrega cada petición al <see cref="FactusFakeServer"/>.</summary>
internal sealed class FactusFakeHandler(FactusFakeServer server) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = await server.HandleAsync(
            request.Method.Method,
            request.RequestUri!.PathAndQuery.TrimStart('/'),
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType,
            body,
            cancellationToken);

        if (response.Drop)
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Conexión cerrada por el Factus simulado.");

        var message = new HttpResponseMessage((HttpStatusCode)response.Status) { RequestMessage = request };
        if (response.Json is not null)
            message.Content = new StringContent(response.Json, Encoding.UTF8, "application/json");
        foreach (var (name, value) in response.Headers)
            message.Headers.TryAddWithoutValidation(name, value);
        return message;
    }
}
