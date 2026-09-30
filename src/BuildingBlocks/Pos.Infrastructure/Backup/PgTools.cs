using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npgsql;

namespace Pos.Infrastructure.Backup;

/// <summary>Volcado y restauración de PostgreSQL (D11-01). Se abstrae para poder ejecutarlo en otro lugar (p. ej. el contenedor de pruebas).</summary>
public interface IPgTools
{
    /// <summary>Volcado en formato custom comprimido, opcionalmente dentro de una foto exportada (<c>pg_export_snapshot</c>).</summary>
    Task DumpAsync(string connectionString, string outputFile, string? snapshot, CancellationToken cancellationToken);

    /// <summary>Lista el contenido del volcado (<c>pg_restore --list</c>): prueba que es legible. Devuelve el número de entradas.</summary>
    Task<int> ListAsync(string dumpFile, CancellationToken cancellationToken);

    /// <summary>Restaura sin dueños ni privilegios; con <paramref name="role"/> los objetos quedan a nombre de ese rol.</summary>
    Task RestoreAsync(string connectionString, string dumpFile, string? role, CancellationToken cancellationToken);
}

/// <summary>Sección <c>Pos:Backup</c>.</summary>
public sealed class BackupToolsOptions
{
    public const string SectionName = "Pos:Backup";

    /// <summary>Carpeta de los binarios de PostgreSQL 18 (pg_dump, pg_restore). Vacío = se busca en la instalación estándar y en el PATH.</summary>
    public string? PgBinPath { get; set; }
}

/// <summary>Ejecuta los binarios de PostgreSQL instalados en el equipo (el instalador de la Fase 13 los incluye).</summary>
public sealed class PgClientTools(string? binPath) : IPgTools
{
    public Task DumpAsync(string connectionString, string outputFile, string? snapshot, CancellationToken cancellationToken)
    {
        List<string> args = ["--format=custom", "--compress=6", "--no-owner", "--no-privileges", $"--file={outputFile}"];
        if (snapshot is not null)
        {
            args.Add($"--snapshot={snapshot}");
        }

        args.Add($"--dbname={ConnInfo(connectionString)}");
        return RunAsync("pg_dump", args, Password(connectionString), cancellationToken);
    }

    public async Task<int> ListAsync(string dumpFile, CancellationToken cancellationToken)
    {
        var output = await RunAsync("pg_restore", ["--list", dumpFile], null, cancellationToken);
        return output.Split('\n').Count(l => l.Length > 0 && !l.StartsWith(';'));
    }

    public Task RestoreAsync(string connectionString, string dumpFile, string? role, CancellationToken cancellationToken)
    {
        List<string> args = ["--no-owner", "--no-privileges", "--disable-triggers", "--exit-on-error", "--single-transaction", $"--dbname={ConnInfo(connectionString)}"];
        if (role is not null)
        {
            args.Add($"--role={role}");
        }

        args.Add(dumpFile);
        return RunAsync("pg_restore", args, Password(connectionString), cancellationToken);
    }

    /// <summary>Cadena libpq (clave=valor) a partir de la de Npgsql; la contraseña va por PGPASSWORD, nunca en la línea de comandos.</summary>
    public static string ConnInfo(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var parts = new List<string>
        {
            $"host={b.Host}",
            string.Create(CultureInfo.InvariantCulture, $"port={b.Port}"),
            $"dbname={b.Database}",
            $"user={b.Username}",
        };
        return string.Join(' ', parts);
    }

    private static string? Password(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).Password;

    private string Resolve(string tool)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(binPath))
        {
            candidates.Add(Path.Combine(binPath, exe));
        }

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "18", "bin", exe));
        }

        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p, exe)));
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                $"No se encontró {exe} de PostgreSQL 18. Configure Pos:Backup:PgBinPath con la carpeta bin de PostgreSQL.");
    }

    private async Task<string> RunAsync(string tool, IReadOnlyList<string> args, string? password, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(Resolve(tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        if (password is not null)
        {
            info.Environment["PGPASSWORD"] = password;
        }

        info.Environment["PGCLIENTENCODING"] = "UTF8";
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"No se pudo iniciar {tool}.");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
            // El proceso ya terminó.
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} terminó con código {process.ExitCode}: {(await stderr).Trim()}");
        }

        return await stdout;
    }
}

/// <summary>Rutas y conexión que usan los backups en este equipo (las registra el servidor al arrancar).</summary>
public sealed record BackupEnvironment(string DataRoot, string ServerConfigFile, string AppVersion, string? BackupConnectionString)
{
    public string BackupsDirectory => Path.Combine(DataRoot, "backups");

    /// <summary>Carpeta del destino LOCAL: aquí se crea cada paquete antes de copiarlo a los demás destinos.</summary>
    public string LocalDirectory => Path.Combine(BackupsDirectory, "local");

    public string KeyFile => Path.Combine(DataRoot, "config", "backup.key");
}
