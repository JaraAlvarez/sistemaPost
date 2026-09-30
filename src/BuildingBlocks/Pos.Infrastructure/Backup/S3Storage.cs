using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Pos.Infrastructure.Backup;

/// <summary>Almacenamiento compatible S3 (MinIO en el VPS del propietario, Backblaze B2, Cloudflare R2…). Direccionamiento por ruta.</summary>
public sealed record S3Settings(string Endpoint, string Region, string Bucket, string AccessKey, string SecretKey);

/// <summary>
/// Firma AWS Signature Version 4 (encabezado Authorization). Función pura para poder probarla con los vectores publicados por AWS.
/// </summary>
public static class S3Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    public static string Authorization(
        string method,
        string canonicalUri,
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers,
        string payloadHash,
        DateTimeOffset now,
        string region,
        string accessKey,
        string secretKey)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(headers);
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var sortedHeaders = headers.Select(h => (Name: h.Key.ToLowerInvariant(), Value: h.Value.Trim())).OrderBy(h => h.Name, StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(sortedHeaders.Select(h => $"{h.Name}:{h.Value}\n"));
        var signedHeaders = string.Join(';', sortedHeaders.Select(h => h.Name));
        var canonicalQuery = string.Join('&', query
            .Select(q => (Name: Encode(q.Key), Value: Encode(q.Value)))
            .OrderBy(q => q.Name, StringComparer.Ordinal)
            .ThenBy(q => q.Value, StringComparer.Ordinal)
            .Select(q => $"{q.Name}={q.Value}"));
        var canonicalRequest = $"{method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var scope = $"{date}/{region}/s3/aws4_request";
        var stringToSign = $"{Algorithm}\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), "s3"), "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));
        return $"{Algorithm} Credential={accessKey}/{scope},SignedHeaders={signedHeaders},Signature={signature}";
    }

    /// <summary>Codificación URI de S3 (RFC 3986): todo salvo A-Z a-z 0-9 - _ . ~; con <paramref name="keepSlash"/> se conserva '/'.</summary>
    public static string Encode(string value, bool keepSlash = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' || (keepSlash && c == '/'))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return builder.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>Cliente S3 mínimo (subir, bajar, listar, borrar) con firma V4, sin dependencias externas.</summary>
public sealed class S3Storage(HttpClient http, S3Settings settings, TimeProvider time)
{
    public async Task PutAsync(string key, string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = stream.Length;
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(HttpMethod.Put, key, new Dictionary<string, string>(), content, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    public async Task GetAsync(string key, Stream destination, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, key, new Dictionary<string, string>(), null, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        await response.Content.CopyToAsync(destination, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, new Dictionary<string, string>(), null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureAsync(response, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<(string Key, long Size)>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var result = new List<(string, long)>();
        string? token = null;
        do
        {
            var query = new Dictionary<string, string> { ["list-type"] = "2", ["prefix"] = prefix };
            if (token is not null)
            {
                query["continuation-token"] = token;
            }

            using var response = await SendAsync(HttpMethod.Get, null, query, null, cancellationToken);
            await EnsureAsync(response, cancellationToken);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            XNamespace ns = xml.Root!.Name.Namespace;
            result.AddRange(xml.Root.Elements(ns + "Contents").Select(c =>
                (c.Element(ns + "Key")!.Value, long.Parse(c.Element(ns + "Size")!.Value, CultureInfo.InvariantCulture))));
            token = xml.Root.Element(ns + "IsTruncated")?.Value == "true" ? xml.Root.Element(ns + "NextContinuationToken")?.Value : null;
        }
        while (token is not null);
        return result;
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string? key, Dictionary<string, string> query, HttpContent? content, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(settings.Endpoint.TrimEnd('/') + "/");
        var path = "/" + S3Signer.Encode(settings.Bucket) + (key is null ? "/" : "/" + S3Signer.Encode(key, keepSlash: true));
        var basePath = endpoint.AbsolutePath.TrimEnd('/');
        var canonicalUri = basePath + path;
        var queryString = string.Join('&', query.Select(q => $"{S3Signer.Encode(q.Key)}={S3Signer.Encode(q.Value)}"));
        var uri = new Uri($"{endpoint.Scheme}://{endpoint.Authority}{canonicalUri}{(queryString.Length > 0 ? "?" + queryString : string.Empty)}");
        var now = time.GetUtcNow();
        var headers = new Dictionary<string, string>
        {
            ["host"] = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}",
            ["x-amz-content-sha256"] = S3Signer.UnsignedPayload,
            ["x-amz-date"] = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
        };
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", headers["x-amz-content-sha256"]);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation("Authorization", S3Signer.Authorization(
            method.Method, canonicalUri, query, headers, S3Signer.UnsignedPayload, now, settings.Region, settings.AccessKey, settings.SecretKey));
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"El almacenamiento S3 respondió {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }
    }
}
