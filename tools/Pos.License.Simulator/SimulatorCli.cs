using System.Globalization;
using System.Text.Json;
using Pos.Licensing.Contracts;

namespace Pos.License.Simulator;

/// <summary>Consola del simulador. Códigos de salida: 0 correcto · 1 rechazado por el servidor o token inválido · 2 uso incorrecto.</summary>
public static class SimulatorCli
{
    private const string Help = """
        Simulador de POS para el servidor de licencias.
          keys       --server <url>                                   Claves públicas publicadas (kid, estado).
          activate   --server <url> --key <clave> --nit <NIT-DV> [--role STORE_SERVER|ALL_IN_ONE] [--branch <sucursal>] [--version 1.0.0]
          checkin    --server <url> [--terminals <n>]                 Check-in con el último token (recibe suspensión, renovación…).
          deactivate --server <url> [--reason "<motivo>"]             Libera este equipo desde el POS.
          status                                                      Muestra el último token verificado y el modo del POS.
        Opción común: --state <archivo.json> (por defecto simulador-pos.json).
        """;

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Ejecuta un comando. <paramref name="client"/> permite usar un servidor en memoria (pruebas).</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            await output.WriteLineAsync(Help);
            return args.Length == 0 ? 2 : 0;
        }

        Dictionary<string, string> options;
        try
        {
            options = ParseOptions(args[1..]);
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 2;
        }

        var statePath = options.GetValueOrDefault("state") ?? "simulador-pos.json";
        var ownsClient = client is null;
        if (client is null)
        {
            if (!options.TryGetValue("server", out var server) || !Uri.TryCreate(server, UriKind.Absolute, out var baseAddress))
            {
                await error.WriteLineAsync("Falta --server <url del servidor de licencias>, p. ej. --server https://licencias.midominio.co");
                return 2;
            }

            client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        }

        try
        {
            return args[0] switch
            {
                "keys" => await KeysAsync(client, output),
                "activate" => await ActivateAsync(client, options, statePath, output, error),
                "checkin" or "deactivate" or "status" => await WithStateAsync(args[0], client, options, statePath, output, error),
                _ => await UnknownAsync(args[0], output, error),
            };
        }
        catch (HttpRequestException ex)
        {
            await error.WriteLineAsync($"No se pudo conectar con el servidor: {ex.Message}");
            return 1;
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    private static async Task<int> KeysAsync(HttpClient client, TextWriter output)
    {
        var pos = new SimulatedPos(client, SimulatorIdentity.Create("0-0"));
        foreach (var key in await pos.RefreshPublicKeysAsync())
        {
            await output.WriteLineAsync($"{key.Kid}  {key.Status,-8} {key.X}");
        }

        return 0;
    }

    private static async Task<int> ActivateAsync(HttpClient client, Dictionary<string, string> options, string statePath, TextWriter output, TextWriter error)
    {
        if (!options.TryGetValue("key", out var licenseKey) || !options.TryGetValue("nit", out var nit))
        {
            await error.WriteLineAsync("Uso: activate --key POS-XXXXX-XXXXX-XXXXX-XXXXX --nit 900123456-8 [--role STORE_SERVER|ALL_IN_ONE]");
            return 2;
        }

        var identity = SimulatorIdentity.Create(nit, options.GetValueOrDefault("role") ?? DeviceRoles.StoreServer) with
        {
            AppVersion = options.GetValueOrDefault("version") ?? "1.0.0",
            BranchName = options.GetValueOrDefault("branch") ?? "Principal",
        };
        var pos = new SimulatedPos(client, identity);
        await pos.RefreshPublicKeysAsync();
        var result = await pos.ActivateAsync(licenseKey);
        await PrintAsync(result, pos, output, error);
        if (result.Succeeded)
        {
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(pos.Export(), StateJson));
            await output.WriteLineAsync($"Estado guardado en {Path.GetFullPath(statePath)}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static async Task<int> WithStateAsync(
        string command, HttpClient client, Dictionary<string, string> options, string statePath, TextWriter output, TextWriter error)
    {
        if (!File.Exists(statePath))
        {
            await error.WriteLineAsync($"No existe {statePath}: active primero (activate).");
            return 2;
        }

        var state = JsonSerializer.Deserialize<SimulatorState>(await File.ReadAllTextAsync(statePath), StateJson)!;
        var pos = SimulatedPos.Restore(client, state);
        if (command == "status")
        {
            await PrintAsync(new SimulatorResult(true, 0, null, null, pos.Claims), pos, output, error);
            return 0;
        }

        var result = command == "checkin"
            ? await pos.CheckinAsync(int.Parse(options.GetValueOrDefault("terminals") ?? "1", CultureInfo.InvariantCulture))
            : await pos.DeactivateAsync(options.GetValueOrDefault("reason"));
        await PrintAsync(result, pos, output, error);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(pos.Export(), StateJson));
        return result.Succeeded ? 0 : 1;
    }

    private static async Task<int> UnknownAsync(string command, TextWriter output, TextWriter error)
    {
        await error.WriteLineAsync($"Comando desconocido: {command}");
        await output.WriteLineAsync(Help);
        return 2;
    }

    private static async Task PrintAsync(SimulatorResult result, SimulatedPos pos, TextWriter output, TextWriter error)
    {
        if (!result.Succeeded)
        {
            var status = result.StatusCode == 0 ? "sin HTTP" : result.StatusCode.ToString(CultureInfo.InvariantCulture);
            await error.WriteLineAsync($"RECHAZADO ({status}): {result.ErrorCode} · {result.ErrorMessage}");
            return;
        }

        if (result.Claims is not { } claims)
        {
            await output.WriteLineAsync($"Correcto. Modo del POS: {pos.Mode}.");
            return;
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"""
            Token verificado ({result.TokenStatus ?? LicenseTokenStatus.Valid}) · licencia {claims.LicenseId}
              Empresa:      {claims.OrganizationName} (NIT {claims.OrganizationNit})
              Instalación:  {claims.InstallationId} · equipo {claims.DeviceRole}
              Edición:      {claims.Edition} · suscripción {claims.SubscriptionStatus}
              Vigencia:     hasta {claims.ValidUntil:yyyy-MM-dd HH:mm} UTC + {claims.GraceDays} días de gracia · renovar después de {claims.RefreshAfter:yyyy-MM-dd HH:mm} UTC
              Modo del POS: {pos.Mode}
            """));
        foreach (var message in claims.Messages)
        {
            await output.WriteLineAsync($"  [{message.Severity}] {message.Code}: {message.Text}");
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Length; i++)
        {
            if (!arguments[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= arguments.Length)
            {
                throw new ArgumentException($"Opción inválida: {arguments[i]}");
            }

            result[arguments[i][2..]] = arguments[++i];
        }

        return result;
    }
}
