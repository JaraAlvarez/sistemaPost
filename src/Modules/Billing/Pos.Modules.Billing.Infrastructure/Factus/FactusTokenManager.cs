using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>
/// Token OAuth2 de Factus en memoria (singleton). Fuentes: https://developers.factus.com.co/autenticacion/auth y
/// https://developers.factus.com.co/autenticacion/refresh-token — POST <c>oauth/token</c> como formulario con
/// grant_type=password (client_id, client_secret, username, password) o grant_type=refresh_token (client_id, client_secret,
/// refresh_token). Respuesta <c>{token_type:"Bearer", expires_in, access_token, refresh_token}</c>; el token dura 1 hora
/// (los ejemplos muestran expires_in 600 y 3600: se respeta el valor recibido).
/// <para>Nunca se registran ni exponen los tokens ni las credenciales.</para>
/// </summary>
internal sealed partial class FactusTokenManager(IHttpClientFactory httpClientFactory, TimeProvider time, ILogger<FactusTokenManager> logger) : IDisposable
{

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _credentialKey;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    /// <summary>Token vigente, o un fallo tipado (credenciales o transitorio).</summary>
    public async Task<TokenLease> GetAsync(FactusOptions options, bool forcePasswordGrant, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_credentialKey != options.CredentialKey || forcePasswordGrant)
                Reset(options.CredentialKey);

            if (_accessToken is not null && time.GetUtcNow() < _expiresAt - options.TokenRenewalMargin)
                return new TokenLease(_accessToken, _expiresAt, null);

            if (_refreshToken is not null)
            {
                var refreshed = await RequestAsync(options, refresh: true, cancellationToken);
                if (refreshed.Token is not null || refreshed.Failure?.Outcome == FactusOutcome.TransientError)
                    return refreshed;
                LogRefreshRejected(logger, refreshed.Failure?.HttpStatus);
                _refreshToken = null;
            }

            return await RequestAsync(options, refresh: false, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Marca como vencido el token que recibió un 401 (si otro hilo ya lo renovó, no hace nada).</summary>
    public async Task InvalidateAsync(string rejectedToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken == rejectedToken)
            {
                _accessToken = null;
                _expiresAt = DateTimeOffset.MinValue;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private void Reset(string credentialKey)
    {
        _credentialKey = credentialKey;
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    private async Task<TokenLease> RequestAsync(FactusOptions options, bool refresh, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", refresh ? "refresh_token" : "password"),
            new("client_id", options.ClientId),
            new("client_secret", options.ClientSecret),
        };
        if (refresh)
        {
            form.Add(new("refresh_token", _refreshToken!));
        }
        else
        {
            form.Add(new("username", options.Username));
            form.Add(new("password", options.Password));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.EffectiveBaseUrl, "oauth/token"));
        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // La página de refresh token pide también el Bearer anterior en Authorization.
        if (refresh && _accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(FactusServiceCollectionExtensions.HttpClientName);
            response = await client.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(FactusResult.Transient("Factus no respondió a la autenticación a tiempo."));
        }
        catch (HttpRequestException ex)
        {
            return Fail(FactusResult.Transient($"Sin conexión con Factus ({ex.HttpRequestError})."));
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!TryReadToken(body, out var access, out var refreshToken, out var expiresIn))
                    return Fail(FactusResult.Transient("Respuesta de autenticación de Factus inválida.", status));

                _accessToken = access;
                _refreshToken = refreshToken ?? _refreshToken;
                _expiresAt = time.GetUtcNow().AddSeconds(expiresIn > 0 ? expiresIn : 3600);
                LogTokenIssued(logger, refresh ? "refresh_token" : "password", _expiresAt);
                return new TokenLease(_accessToken, _expiresAt, null);
            }

            if (FactusApiClient.IsTransient(response.StatusCode))
                return Fail(FactusResult.Transient($"Factus no disponible para autenticar (HTTP {status}).", status, FactusApiClient.RetryAfterOf(response, time)));

            // 400 invalid_grant / 401 invalid_client / 403: credenciales o refresh token inválidos.
            return Fail(FactusResult.Credentials(
                refresh ? $"Factus rechazó el refresh token (HTTP {status})." : $"Factus rechazó las credenciales (HTTP {status}).", status));
        }
    }

    private static TokenLease Fail(FactusResult failure) => new(null, default, failure);

    private static bool TryReadToken(string body, out string access, out string? refresh, out int expiresIn)
    {
        access = string.Empty;
        refresh = null;
        expiresIn = 0;
        using var document = FactusResponseReader.Parse(body);
        if (document is null)
            return false;

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || FactusResponseReader.String(root, "access_token") is not { Length: > 0 } token)
            return false;
        access = token;
        refresh = FactusResponseReader.String(root, "refresh_token");
        expiresIn = int.TryParse(FactusResponseReader.String(root, "expires_in"), System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Factus: token obtenido por {GrantType}; vence {ExpiresAt:O}.")]
    private static partial void LogTokenIssued(ILogger logger, string grantType, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Factus: el refresh token fue rechazado (HTTP {Status}); se autentica con usuario y clave.")]
    private static partial void LogRefreshRejected(ILogger logger, int? status);
}

/// <summary>Token prestado para una petición, o el fallo que impidió obtenerlo.</summary>
internal readonly record struct TokenLease(string? Token, DateTimeOffset ExpiresAt, FactusResult? Failure);
