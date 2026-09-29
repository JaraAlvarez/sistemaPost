using System.Net.Http.Json;
using System.Text.Json;
using Pos.Licensing.Contracts;

namespace Pos.License.Simulator;

/// <summary>Lo que un POS declara de sí mismo: instalación (sucursal), huella del equipo, rol, NIT de la empresa y versión.</summary>
public sealed record SimulatorIdentity(
    Guid InstallationId,
    string Fingerprint,
    string DeviceRole,
    string OrganizationNit,
    string AppVersion = "1.0.0",
    string? BranchName = "Principal",
    string? DeviceName = "SIMULADOR",
    string? OperatingSystem = "Windows 11 (simulado)")
{
    /// <summary>Instalación nueva con una huella calculada a partir de seriales simulados (como lo hace el POS).</summary>
    public static SimulatorIdentity Create(
        string organizationNit, string deviceRole = DeviceRoles.StoreServer, string board = "SIM-PLACA", string disk = "SIM-DISCO", string machine = "SIM-MAQUINA") =>
        new(Guid.CreateVersion7(), DeviceFingerprint.FromHardware(board, disk, machine).ToString(), deviceRole, organizationNit);
}

/// <summary>Cómo quedaría el POS con el último token verificado (resumen de las reglas que aplicará el POS en la Fase 12-B).</summary>
public enum SimulatedLicenseMode
{
    /// <summary>Sin token: el POS pide la clave.</summary>
    NotActivated,

    /// <summary>Suscripción vigente (o en prueba).</summary>
    Licensed,

    /// <summary>Vencida pero dentro de la gracia: funciona con avisos.</summary>
    Grace,

    /// <summary>Suspendida, cancelada o vencida sin gracia: modo restringido.</summary>
    Restricted,
}

/// <summary>Resultado de una operación contra el servidor. Un token recibido solo se acepta si su firma y su contenido verifican.</summary>
public sealed record SimulatorResult(
    bool Succeeded,
    int StatusCode,
    string? ErrorCode,
    string? ErrorMessage,
    LicenseClaims? Claims = null,
    LicenseTokenStatus? TokenStatus = null,
    IReadOnlyList<LicenseMessage>? Messages = null)
{
    /// <summary>El token llegó, pero no pasó la verificación local (firma, clave desconocida o datos de otro equipo).</summary>
    public const string TokenRejectedCode = "SIMULATOR.TOKEN_REJECTED";

    /// <summary>No se pudo hablar con el servidor.</summary>
    public const string UnreachableCode = "SIMULATOR.SERVER_UNREACHABLE";
}

/// <summary>Estado persistible del simulador (la consola lo guarda en un archivo JSON entre comandos).</summary>
public sealed record SimulatorState(SimulatorIdentity Identity, string? Token, IReadOnlyList<PublicKeyDto> TrustedKeys, bool LicenseRevoked = false);

/// <summary>
/// Un POS simulado frente al servidor de licencias. Usa solo las rutas, los DTOs y la verificación del contrato compartido: cada
/// token recibido se verifica con las claves públicas de confianza (las embebidas + las publicadas en <c>/v1/public-keys</c>); si
/// el <c>kid</c> es desconocido, vuelve a leer las claves publicadas una vez (rotación). Además, el token debe ser de ESTA
/// instalación, de ESTE equipo (2 de 3) y de ESTA empresa; si no, se descarta como lo haría el POS.
/// </summary>
public sealed class SimulatedPos
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly List<PublicKeyDto> _keys;

    public SimulatedPos(HttpClient http, SimulatorIdentity identity, IEnumerable<PublicKeyDto>? embeddedKeys = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(identity);
        _http = http;
        Identity = identity;
        _time = time ?? TimeProvider.System;
        _keys = [.. embeddedKeys ?? []];
    }

    public SimulatorIdentity Identity { get; private set; }

    /// <summary>Último token aceptado (el que se presenta en el check-in).</summary>
    public string? Token { get; private set; }

    /// <summary>Contenido del último token aceptado.</summary>
    public LicenseClaims? Claims => Token is null ? null : LicenseToken.Verify(Token, TrustedKeys).Claims;

    public IReadOnlyList<PublicKeyDto> KnownKeys => _keys;

    public LicenseKeyRing TrustedKeys =>
        new(_keys.Select(k => LicensePublicKey.TryParse(k.X, out var key) && key.Kid == k.Kid ? key : null).OfType<LicensePublicKey>());

    public SimulatedLicenseMode Mode => ModeAt(_time.GetUtcNow());

    public static SimulatedPos Restore(HttpClient http, SimulatorState state, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new SimulatedPos(http, state.Identity, state.TrustedKeys, time) { Token = state.Token, LicenseRevoked = state.LicenseRevoked };
    }

    public SimulatorState Export() => new(Identity, Token, [.. _keys], LicenseRevoked);

    /// <summary>El servidor respondió en el último check-in que la licencia fue revocada (el POS pasa a restringido).</summary>
    public bool LicenseRevoked { get; private set; }

    public SimulatedLicenseMode ModeAt(DateTimeOffset instant)
    {
        if (Claims is not { } claims)
        {
            return SimulatedLicenseMode.NotActivated;
        }

        if (LicenseRevoked || claims.SubscriptionStatus is SubscriptionStatuses.Suspended or SubscriptionStatuses.Cancelled or SubscriptionStatuses.Expired)
        {
            return SimulatedLicenseMode.Restricted;
        }

        return claims.IsValidAt(instant) ? SimulatedLicenseMode.Licensed
            : claims.IsInGraceAt(instant) ? SimulatedLicenseMode.Grace
            : SimulatedLicenseMode.Restricted;
    }

    /// <summary>Simula un cambio de hardware (p. ej. cambiar el disco: 2 de 3 siguen coincidiendo; otro PC: ninguno).</summary>
    public void ChangeHardware(string? board, string? disk, string? machine) =>
        Identity = Identity with { Fingerprint = DeviceFingerprint.FromHardware(board, disk, machine).ToString() };

    /// <summary>Lee las claves públicas de confianza publicadas por el servidor y las agrega a las conocidas.</summary>
    public async Task<IReadOnlyList<PublicKeyDto>> RefreshPublicKeysAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<PublicKeysResponse>(LicensingRoutes.PublicKeys, Json, cancellationToken)
            ?? throw new InvalidOperationException("El servidor no devolvió claves públicas.");
        foreach (var key in response.Keys)
        {
            _keys.RemoveAll(k => k.Kid == key.Kid);
            _keys.Add(key);
        }

        return response.Keys;
    }

    /// <summary>Activa la instalación con la clave de licencia (asistente inicial del POS).</summary>
    public Task<SimulatorResult> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default) =>
        SendForTokenAsync(
            LicensingRoutes.Activations,
            new ActivationRequest(
                licenseKey, Identity.InstallationId, Identity.Fingerprint, Identity.DeviceRole, Identity.AppVersion, Identity.OrganizationNit,
                Identity.BranchName, Identity.DeviceName, Identity.OperatingSystem),
            cancellationToken);

    /// <summary>Check-in diario con el último token (aunque esté vencido) y la huella actual.</summary>
    public Task<SimulatorResult> CheckinAsync(int activeTerminals = 1, CancellationToken cancellationToken = default) =>
        Token is null
            ? Task.FromResult(new SimulatorResult(false, 0, "SIMULATOR.NOT_ACTIVATED", "Primero active la instalación con la clave."))
            : SendForTokenAsync(
                LicensingRoutes.Checkins,
                new CheckinRequest(Token, Identity.Fingerprint, Identity.AppVersion, activeTerminals, _time.GetUtcNow()),
                cancellationToken);

    /// <summary>Libera el equipo desde el propio POS (antes de pasar a otro computador). Si funciona, olvida el token.</summary>
    public async Task<SimulatorResult> DeactivateAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (Token is null)
        {
            return new SimulatorResult(false, 0, "SIMULATOR.NOT_ACTIVATED", "No hay token que liberar.");
        }

        using var response = await PostAsync(LicensingRoutes.Deactivations, new DeactivationRequest(Token, Identity.Fingerprint, reason), cancellationToken);
        if (response is null)
        {
            return Unreachable();
        }

        if (!response.IsSuccessStatusCode)
        {
            return await ProblemAsync(response, cancellationToken);
        }

        Token = null;
        return new SimulatorResult(true, (int)response.StatusCode, null, null);
    }

    /// <summary>Verifica un token como lo hará el POS: firma con las claves de confianza y datos de esta instalación.</summary>
    public (LicenseTokenStatus Status, LicenseClaims? Claims, string? Problem) Verify(string token)
    {
        var verification = LicenseToken.Verify(token, TrustedKeys);
        if (verification is not { IsValid: true, Claims: { } claims })
        {
            return (verification.Status, null, $"El token no verifica: {verification.Status}.");
        }

        if (claims.InstallationId != Identity.InstallationId)
        {
            return (verification.Status, claims, "El token es de otra instalación.");
        }

        if (!DeviceFingerprint.TryParse(claims.DeviceFingerprint, out var tokenDevice)
            || !DeviceFingerprint.TryParse(Identity.Fingerprint, out var mine) || !tokenDevice.Matches(mine))
        {
            return (verification.Status, claims, "El token es de otro equipo.");
        }

        if (!string.Equals(claims.OrganizationNit, Identity.OrganizationNit, StringComparison.Ordinal))
        {
            return (verification.Status, claims, "El token es de otra empresa (NIT).");
        }

        return (verification.Status, claims, null);
    }

    private async Task<SimulatorResult> SendForTokenAsync<TRequest>(string route, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(route, request, cancellationToken);
        if (response is null)
        {
            return Unreachable();
        }

        if (!response.IsSuccessStatusCode)
        {
            var rejection = await ProblemAsync(response, cancellationToken);
            LicenseRevoked |= rejection.ErrorCode == LicenseErrorCodes.LicenseRevoked;
            return rejection;
        }

        var body = await response.Content.ReadFromJsonAsync<LicenseTokenResponse>(Json, cancellationToken)
            ?? throw new InvalidOperationException("El servidor respondió sin token.");

        var (status, claims, problem) = Verify(body.Token);
        if (status == LicenseTokenStatus.UnknownKey)
        {
            // Rotación: el servidor firma con una clave que este POS aún no conoce; se vuelven a leer las publicadas.
            await RefreshPublicKeysAsync(cancellationToken);
            (status, claims, problem) = Verify(body.Token);
        }

        if (problem is not null)
        {
            return new SimulatorResult(false, (int)response.StatusCode, SimulatorResult.TokenRejectedCode, problem, claims, status);
        }

        Token = body.Token;
        LicenseRevoked = false;
        return new SimulatorResult(true, (int)response.StatusCode, null, null, claims, status, claims!.Messages);
    }

    private async Task<HttpResponseMessage?> PostAsync<TRequest>(string route, TRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.PostAsJsonAsync(route, request, Json, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static SimulatorResult Unreachable() =>
        new(false, 0, SimulatorResult.UnreachableCode, "No se pudo conectar con el servidor de licencias.");

    private static async Task<SimulatorResult> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null;
        string? detail = null;
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("code", out var c))
            {
                code = c.GetString();
            }

            if (document.RootElement.TryGetProperty("detail", out var d))
            {
                detail = d.GetString();
            }
        }
        catch (JsonException)
        {
            // Respuesta sin cuerpo JSON (p. ej. un proxy): queda solo el código HTTP.
        }

        return new SimulatorResult(false, (int)response.StatusCode, code, detail);
    }
}
