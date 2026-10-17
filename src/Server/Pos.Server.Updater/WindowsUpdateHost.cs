using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pos.Infrastructure.Security;
using Pos.Updates.Contracts;

namespace Pos.Server.Updater;

/// <summary>
/// El equipo real (D13-03): versiones lado a lado en <c>{InstallRoot}\app\{versión}</c> con el enlace <c>app\current</c> (unión de NTFS) al que
/// apuntan los servicios y los accesos directos de la ventana (<c>app\current\desktop\BusinessPost.exe</c>, ver
/// <see cref="VentanaEscritorio"/>); el migrador de cada versión hace el backup, las migraciones y la restauración.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsUpdateHost(UpdaterSettings settings, ILogger<WindowsUpdateHost> logger) : IUpdateHost
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private string AppDirectory => Path.Combine(settings.InstallRoot, "app");

    private string CurrentLink => Path.Combine(AppDirectory, "current");

    private string CurrentFile => Path.Combine(AppDirectory, "current.json");

    /// <summary>
    /// Versión activa según <c>app\current.json</c>. Si falta o está dañado devuelve <see cref="string.Empty"/> (no "0.0.0": eso hacía que
    /// cualquier versión pareciera más nueva). El actualizador no actualiza un equipo cuya versión no conoce y lo avisa.
    /// </summary>
    public string CurrentVersion
    {
        get
        {
            try
            {
                return File.Exists(CurrentFile)
                    && JsonDocument.Parse(File.ReadAllText(CurrentFile)).RootElement.GetProperty("version").GetString() is { } version
                    && SemanticVersion.TryParse(version, out _)
                        ? version
                        : string.Empty;
            }
            catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// U1: la versión está preparada si su carpeta tiene el manifiesto de huellas que dejó <see cref="StageAsync"/> (del paquete verificado)
    /// y TODOS sus archivos coinciden, sin enlaces. Una carpeta que solo existe (a medio copiar, o alterada) no cuenta.
    /// </summary>
    public bool IsStaged(string version) => StagedContents.Verify(Path.Combine(AppDirectory, version)) is null;

    /// <summary>
    /// TOCTOU (R5): el paquete se abre UNA vez, en exclusiva (nadie puede cambiarlo mientras tanto); con ese mismo handle se comprueban
    /// tamaño y huella del manifiesto firmado (U2) y se extrae. Lo extraído queda con su manifiesto de huellas (U1) y recién entonces se
    /// renombra a <c>app\{versión}</c>.
    /// </summary>
    public async Task StageAsync(string version, string packagePath, UpdateManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var target = Path.Combine(AppDirectory, version);
        var temporary = target + ".tmp";
        foreach (var leftover in new[] { temporary, target })
        {
            if (SecurityCheckedDelete(leftover) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
        }

        await using (var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            if (package.Length != manifest.PackageSize
                || !string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(package, cancellationToken)), manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("El paquete no coincide con la huella del manifiesto firmado: no se instala.");
            }

            package.Position = 0;
            await using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            await StagedContents.ExtractAsync(zip, temporary, manifest.PackageSha256, cancellationToken);
        }

        Directory.Move(temporary, target);
        await PrecalentarAsync(target, cancellationToken);
    }

    /// <summary>Borra una carpeta que dejó un intento anterior, sin seguir enlaces (si es un enlace, se quita el enlace y nada más).</summary>
    private static string? SecurityCheckedDelete(string directory)
    {
        if (!Directory.Exists(directory) && !File.Exists(directory))
        {
            return null;
        }

        if (UpdaterSecurity.IsReparsePoint(directory))
        {
            Directory.Delete(directory);
            return null;
        }

        return StagedContents.DeleteWithoutFollowingLinks(directory);
    }

    /// <summary>
    /// Corre los programas de la versión nueva en modo <c>--precalentar</c> (D17C-02): el antivirus revisa sus .exe y .dll ahora, con la
    /// versión anterior todavía vendiendo, y no durante el cambio (en pruebas, más de 4 minutos sin firma de código). Incluye la ventana
    /// <c>BusinessPost.exe</c>: así la primera apertura después de actualizar es rápida (su <c>--precalentar</c> no abre ventanas y no
    /// choca con la que el cajero tenga abierta). Mejor esfuerzo: si falla o tarda más de ⚙️ 15 minutos, la actualización sigue igual.
    /// </summary>
    private async Task PrecalentarAsync(string versionDirectory, CancellationToken cancellationToken)
    {
        foreach (var (folder, exe) in new[]
                 {
                     ("server", "Pos.Server.Host.exe"), ("migrator", "Pos.Server.Migrator.exe"), ("agent", "Pos.Terminal.Agent.exe"),
                     (VentanaEscritorio.Carpeta, VentanaEscritorio.Programa),
                 })
        {
            var path = Path.Combine(versionDirectory, folder, exe);
            if (!File.Exists(path))
            {
                continue;
            }

            // R5: solo programas que salieron del paquete verificado (su huella está en el manifiesto de la carpeta) y sin enlaces.
            if (StagedContents.VerifyFile(versionDirectory, Path.Combine(folder, exe)) is { } problema)
            {
                LogServiceFailed(logger, exe, $"no se precalienta: {problema}");
                continue;
            }

            var reloj = Stopwatch.StartNew();
            try
            {
                using var process = Process.Start(new ProcessStartInfo(path, "--precalentar")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path)!,
                })!;
                using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limite.CancelAfter(TimeSpan.FromMinutes(15));
                try
                {
                    await process.WaitForExitAsync(limite.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                }

                LogPrecalentado(logger, exe, reloj.Elapsed.TotalSeconds);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                LogServiceFailed(logger, exe, ex.Message);
            }
        }
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

    /// <summary>
    /// U3: cambio de versión como en el instalador (IN9): la unión nueva se crea aparte (<c>app\current.nueva</c>) y solo entonces reemplaza
    /// a la anterior. Si algo falla, <c>app\current</c> sigue (o vuelve a quedar) apuntando a la versión anterior y se lanza la excepción.
    /// </summary>
    public void Activate(string version)
    {
        var target = Path.Combine(AppDirectory, version);
        if (!Directory.Exists(target) || UpdaterSecurity.IsReparsePoint(target))
        {
            throw new InvalidOperationException($"La carpeta de la versión {version} no existe o es un enlace.");
        }

        var staged = CurrentLink + ".nueva";
        if (Directory.Exists(staged))
        {
            if (!UpdaterSecurity.IsReparsePoint(staged))
            {
                throw new InvalidOperationException($"{staged} no es una unión: no se borra.");
            }

            Directory.Delete(staged);
        }

        Junction(staged, target);
        var previous = Directory.Exists(CurrentLink) ? ReadJunctionTarget(CurrentLink) : null;
        if (Directory.Exists(CurrentLink))
        {
            if (!UpdaterSecurity.IsReparsePoint(CurrentLink))
            {
                Directory.Delete(staged);
                throw new InvalidOperationException($"{CurrentLink} no es una unión: no se reemplaza.");
            }

            Directory.Delete(CurrentLink);
        }

        try
        {
            Directory.Move(staged, CurrentLink);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Se vuelve a dejar la unión anterior (o, si no se puede, la nueva directamente).
            TryDeleteLink(staged);
            if (previous is not null && Directory.Exists(previous))
            {
                Junction(CurrentLink, previous);
            }

            throw new InvalidOperationException($"No se pudo activar la versión {version} ({ex.Message}); app\\current sigue en la anterior.", ex);
        }

        File.WriteAllText(CurrentFile + ".tmp", JsonSerializer.Serialize(new { version }));
        File.Move(CurrentFile + ".tmp", CurrentFile, overwrite: true);

        // Ventana: los accesos directos nuevos ya apuntan a app\current; los de instalaciones anteriores, a {InstallRoot}\desktop. La
        // ventana abierta no se cierra: sigue funcionando y la versión nueva se usa la próxima vez que se abra (también en la vuelta atrás).
        foreach (var aviso in VentanaEscritorio.RefrescarCopiaAnterior(settings.InstallRoot, Path.Combine(AppDirectory, version)))
        {
            LogServiceFailed(logger, VentanaEscritorio.Programa, aviso);
        }
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
            .Where(n => n is not null && n != "current" && n != "current.nueva" && !n.EndsWith(".tmp", StringComparison.Ordinal))
            .Select(n => (Name: n!, Ok: Pos.Updates.Contracts.SemanticVersion.TryParse(n, out var v), Version: v))
            .Where(v => v.Ok)
            .OrderByDescending(v => v.Version)
            .ToList();
        foreach (var old in versions.Skip(keep).Where(v => v.Name != current))
        {
            if (VentanaEscritorio.EnUso(Path.Combine(AppDirectory, old.Name)))
            {
                // La ventana sigue abierta desde esa versión: no se borra a medias; se intenta en la próxima actualización.
                LogServiceFailed(logger, old.Name, "la ventana BusinessPost.exe de esta versión sigue abierta; se borrará en la próxima limpieza");
                continue;
            }

            try
            {
                if (SecurityCheckedDelete(Path.Combine(AppDirectory, old.Name)) is { } problema)
                {
                    LogServiceFailed(logger, old.Name, problema);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        if (mklink.ExitCode != 0)
        {
            throw new InvalidOperationException($"No se pudo crear la unión {link}.");
        }
    }

    private static string? ReadJunctionTarget(string link) =>
        new DirectoryInfo(link).LinkTarget is { } target ? Path.GetFullPath(target.Replace(@"\??\", string.Empty, StringComparison.Ordinal)) : null;

    private static void TryDeleteLink(string link)
    {
        try
        {
            if (Directory.Exists(link) && UpdaterSecurity.IsReparsePoint(link))
            {
                Directory.Delete(link);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mejor esfuerzo.
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Versión nueva precalentada: {Program} en {Seconds:0.0} s.")]
    private static partial void LogPrecalentado(ILogger logger, string program, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name}: {Error}")]
    private static partial void LogServiceFailed(ILogger logger, string name, string error);
}
