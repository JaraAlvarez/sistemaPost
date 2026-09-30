using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pos.Infrastructure.Security;

namespace Pos.Server.Updater;

/// <summary>
/// El equipo real (D13-03): versiones lado a lado en <c>{InstallRoot}\app\{versión}</c> con el enlace <c>app\current</c> (unión de NTFS) al que
/// apuntan los servicios; el migrador de cada versión hace el backup, las migraciones y la restauración.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsUpdateHost(UpdaterSettings settings, ILogger<WindowsUpdateHost> logger) : IUpdateHost
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private string AppDirectory => Path.Combine(settings.InstallRoot, "app");

    private string CurrentLink => Path.Combine(AppDirectory, "current");

    private string CurrentFile => Path.Combine(AppDirectory, "current.json");

    public string CurrentVersion
    {
        get
        {
            try
            {
                return File.Exists(CurrentFile)
                    ? JsonDocument.Parse(File.ReadAllText(CurrentFile)).RootElement.GetProperty("version").GetString() ?? "0.0.0"
                    : "0.0.0";
            }
            catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException)
            {
                return "0.0.0";
            }
        }
    }

    public bool IsStaged(string version) => Directory.Exists(Path.Combine(AppDirectory, version));

    public async Task StageAsync(string version, string packagePath, CancellationToken cancellationToken)
    {
        var target = Path.Combine(AppDirectory, version);
        var temporary = target + ".tmp";
        if (Directory.Exists(temporary))
        {
            Directory.Delete(temporary, recursive: true);
        }

        await ZipFile.ExtractToDirectoryAsync(packagePath, temporary, overwriteFiles: true, cancellationToken);
        Directory.Move(temporary, target);
    }

    public async Task<int> OpenCashSessionsAsync(CancellationToken cancellationToken)
    {
        if (ProtectedSecret.Reveal(settings.ConnectionString) is not { Length: > 0 } connectionString)
        {
            return 0;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*)::int FROM cash.cash_sessions WHERE status <> 'CLOSED'", connection);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<string> BackupAsync(string version, CancellationToken cancellationToken)
    {
        var output = Path.Combine(settings.DataRoot, "updates", "backups", DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(output);
        List<string> arguments = ["backup", "--kind", "PRE_UPDATE", "--data-root", settings.DataRoot, "--output", output];
        if (settings.PgBinPath is { Length: > 0 } pgBin)
        {
            arguments.AddRange(["--pg-bin", pgBin]);
        }

        if (await RunMigratorAsync(version, arguments, cancellationToken) != 0)
        {
            throw new InvalidOperationException("el migrador no pudo hacer el backup PRE_UPDATE");
        }

        return Directory.GetFiles(output, "*.posbak").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new InvalidOperationException("el backup PRE_UPDATE no dejó ningún archivo");
    }

    public async Task<bool> MigrateAsync(string version, CancellationToken cancellationToken) =>
        await RunMigratorAsync(version, ["migrate", "--no-backup", "--data-root", settings.DataRoot], cancellationToken) == 0;

    public async Task<bool> RestoreAsync(string version, string backupPath, CancellationToken cancellationToken) =>
        await RunMigratorAsync(version, ["restore", "--file", backupPath, "--data-root", settings.DataRoot, "--yes"], cancellationToken) == 0;

    public async Task StopServicesAsync(CancellationToken cancellationToken)
    {
        foreach (var name in settings.ServiceNames)
        {
            using var service = Find(name);
            if (service is null || service.Status == ServiceControllerStatus.Stopped)
            {
                continue;
            }

            service.Stop();
            await WaitAsync(service, ServiceControllerStatus.Stopped, cancellationToken);
        }
    }

    public async Task StartServicesAsync(CancellationToken cancellationToken)
    {
        foreach (var name in settings.ServiceNames.Reverse())
        {
            using var service = Find(name);
            if (service is null || service.Status == ServiceControllerStatus.Running)
            {
                continue;
            }

            try
            {
                service.Start();
                await WaitAsync(service, ServiceControllerStatus.Running, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                LogServiceFailed(logger, name, ex.Message);
            }
        }
    }

    public void Activate(string version)
    {
        // Unión de NTFS: los servicios apuntan a app\current\… y el cambio no toca sus rutas.
        if (Directory.Exists(CurrentLink))
        {
            Directory.Delete(CurrentLink);
        }

        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{CurrentLink}\" \"{Path.Combine(AppDirectory, version)}\"")
               {
                   CreateNoWindow = true,
                   UseShellExecute = false,
               })!)
        {
            mklink.WaitForExit();
            if (mklink.ExitCode != 0)
            {
                throw new InvalidOperationException($"No se pudo crear el enlace a la versión {version}.");
            }
        }

        File.WriteAllText(CurrentFile + ".tmp", JsonSerializer.Serialize(new { version }));
        File.Move(CurrentFile + ".tmp", CurrentFile, overwrite: true);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var url = settings.Mode == UpdaterMode.Terminal
            ? $"http://localhost:{settings.AgentPort}/status"
            : $"http://localhost:{settings.ServerPort}/health/ready";
        var deadline = DateTime.UtcNow.AddSeconds(settings.HealthTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await Http.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Todavía arrancando.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return false;
    }

    public void CleanupOldVersions(int keep)
    {
        var current = CurrentVersion;
        var versions = Directory.GetDirectories(AppDirectory)
            .Select(Path.GetFileName)
            .Where(n => n is not null && n != "current" && !n.EndsWith(".tmp", StringComparison.Ordinal))
            .Select(n => (Name: n!, Ok: Pos.Updates.Contracts.SemanticVersion.TryParse(n, out var v), Version: v))
            .Where(v => v.Ok)
            .OrderByDescending(v => v.Version)
            .ToList();
        foreach (var old in versions.Skip(keep).Where(v => v.Name != current))
        {
            try
            {
                Directory.Delete(Path.Combine(AppDirectory, old.Name), recursive: true);
            }
            catch (IOException ex)
            {
                LogServiceFailed(logger, old.Name, ex.Message);
            }
        }
    }

    private async Task<int> RunMigratorAsync(string version, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var exe = Path.Combine(AppDirectory, version, "migrator", "Pos.Server.Migrator.exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"No se pudo ejecutar {exe}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var (stdout, stderr) = ((await output).Trim(), (await error).Trim());
        if (logger.IsEnabled(LogLevel.Information))
        {
            LogMigrator(logger, version, start.ArgumentList[0], process.ExitCode, stdout, stderr);
        }
        return process.ExitCode;
    }

    private static ServiceController? Find(string name) =>
        ServiceController.GetServices().FirstOrDefault(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));

    private static async Task WaitAsync(ServiceController service, ServiceControllerStatus status, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            service.Refresh();
            if (service.Status == status)
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException($"El servicio {service.ServiceName} no llegó a {status}.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrador {Version} {Command}: código {ExitCode}. {Output} {Error}")]
    private static partial void LogMigrator(ILogger logger, string version, string command, int exitCode, string output, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name}: {Error}")]
    private static partial void LogServiceFailed(ILogger logger, string name, string error);
}
