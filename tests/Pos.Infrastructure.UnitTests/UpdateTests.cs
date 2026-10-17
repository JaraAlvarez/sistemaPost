using Pos.Licensing.Contracts;
using Pos.Server.Updater;
using Pos.Updates.Contracts;

namespace Pos.Infrastructure.UnitTests;

/// <summary>Fase 13: manifiesto firmado, versiones, descubrimiento en la LAN y orquestador de la actualización con vuelta atrás.</summary>
public sealed class UpdateTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly LicenseSigningKey _key = LicenseSigningKey.Generate();
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "pos-update-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _key.Dispose();
        // La unión NTFS app\current se quita sola (sin recorrerla), como hace el actualizador al cambiar de versión.
        var enlace = Path.Combine(_dataRoot, "install", "app", "current");
        if (Directory.Exists(enlace))
        {
            Directory.Delete(enlace);
        }

        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private static UpdateManifest Manifest(string version = "1.4.0", string product = "BusinessPost") =>
        new(product, UpdateChannels.Stable, version, $"BusinessPost-{version}.zip", new string('a', 64), 1234, "1.4.0", DateTimeOffset.UtcNow, "Novedades");

    [Fact]
    public void El_manifiesto_firmado_verifica_y_cualquier_cambio_lo_invalida()
    {
        var ring = new LicenseKeyRing([_key.PublicKey]);
        var signed = UpdateSigning.Sign(Manifest(), _key);

        UpdateSigning.Verify(UpdateSigning.Deserialize(UpdateSigning.Serialize(signed)), ring, "BusinessPost", out var manifest).ShouldBe(ManifestStatus.Valid);
        manifest!.Version.ShouldBe("1.4.0");

        var tampered = UpdateSigning.Sign(Manifest("9.9.9"), _key) with { Signature = signed.Signature };
        UpdateSigning.Verify(tampered, ring, "BusinessPost", out _).ShouldBe(ManifestStatus.BadSignature);
        using var other = LicenseSigningKey.Generate();
        UpdateSigning.Verify(UpdateSigning.Sign(Manifest(), other), ring, "BusinessPost", out _).ShouldBe(ManifestStatus.UnknownKey);
        UpdateSigning.Verify(UpdateSigning.Sign(Manifest(product: "Otro"), _key), ring, "BusinessPost", out _).ShouldBe(ManifestStatus.WrongProduct);
        UpdateSigning.Verify(UpdateSigning.Deserialize("{ no es json"), ring, "BusinessPost", out _).ShouldBe(ManifestStatus.Malformed);
    }

    [Theory]
    [InlineData("1.4.0", "1.3.9", 1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0-beta.1", "2.0.0", -1)]
    [InlineData("1.4.0+abc123", "1.4.0", 0)]
    public void Las_versiones_se_comparan_por_SemVer(string left, string right, int expected)
    {
        SemanticVersion.TryParse(left, out var a).ShouldBeTrue();
        SemanticVersion.TryParse(right, out var b).ShouldBeTrue();
        Math.Sign(a.CompareTo(b)).ShouldBe(expected);
        SemanticVersion.TryParse("1.4", out _).ShouldBeFalse();
    }

    [Fact]
    public void El_descubrimiento_en_la_LAN_solo_responde_a_su_solicitud()
    {
        LanDiscovery.IsRequest(LanDiscovery.RequestBytes).ShouldBeTrue();
        LanDiscovery.IsRequest("OTRA COSA"u8).ShouldBeFalse();
        var response = new DiscoveryResponse("BusinessPost", "La Economía", "Centro", "SERVIDOR", 5443, "ab12", "1.4.0");
        LanDiscovery.Parse(LanDiscovery.Serialize(response)).ShouldBe(response);
        new DiscoveredServer("192.168.1.10", response).Url.ShouldBe("https://192.168.1.10:5443/");
    }

    [Fact]
    public async Task Actualizacion_correcta_backup_migracion_cambio_de_version_y_salud()
    {
        var host = new FakeUpdateHost("1.3.0");
        var result = await Orchestrator(host).ApplyAsync(Manifest(), "paquete.zip", force: false, Ct);

        result.ShouldBe(UpdateResult.Applied);
        host.CurrentVersion.ShouldBe("1.4.0");
        host.Steps.ShouldBe(["stage 1.4.0", "backup 1.3.0", "stop", "migrate 1.4.0", "activate 1.4.0", "start", "health", "cleanup"]);
        UpdateFiles.ReadHistory(_dataRoot).ShouldHaveSingleItem().Outcome.ShouldBe(UpdateOutcomes.Applied);
    }

    [Fact]
    public async Task Con_jornadas_abiertas_espera_salvo_que_pidan_instalar_ahora()
    {
        var host = new FakeUpdateHost("1.3.0") { OpenSessions = 2 };
        (await Orchestrator(host).ApplyAsync(Manifest(), "paquete.zip", force: false, Ct)).ShouldBe(UpdateResult.Deferred);
        host.CurrentVersion.ShouldBe("1.3.0");
        (await Orchestrator(host).ApplyAsync(Manifest(), "paquete.zip", force: true, Ct)).ShouldBe(UpdateResult.Applied);
    }

    [Fact]
    public async Task Si_falla_la_migracion_sigue_la_version_anterior_sin_cambios()
    {
        var host = new FakeUpdateHost("1.3.0") { MigrateSucceeds = false };
        (await Orchestrator(host).ApplyAsync(Manifest(), "paquete.zip", force: false, Ct)).ShouldBe(UpdateResult.FailedNoChanges);
        host.CurrentVersion.ShouldBe("1.3.0");
        host.Steps.ShouldNotContain("activate 1.4.0");
        host.Steps[^1].ShouldBe("start");
        UpdateFiles.ReadHistory(_dataRoot).ShouldHaveSingleItem().Outcome.ShouldBe(UpdateOutcomes.Failed);
    }

    [Fact]
    public async Task Si_la_version_nueva_no_arranca_vuelve_a_la_anterior_y_restaura_el_backup()
    {
        var host = new FakeUpdateHost("1.3.0") { HealthyVersions = ["1.3.0"] };
        (await Orchestrator(host).ApplyAsync(Manifest(), "paquete.zip", force: false, Ct)).ShouldBe(UpdateResult.RolledBack);
        host.CurrentVersion.ShouldBe("1.3.0");
        host.Steps.ShouldContain("restore 1.3.0 backup-1.3.0.posbak");
        UpdateFiles.ReadHistory(_dataRoot).Select(h => h.Outcome).ShouldBe([UpdateOutcomes.Failed, UpdateOutcomes.RolledBack]);
    }

    [Fact]
    public async Task En_la_caja_no_hay_backup_ni_migracion()
    {
        var host = new FakeUpdateHost("1.3.0") { OpenSessions = 5 };
        var result = await new UpdateOrchestrator(host, UpdaterMode.Terminal, _dataRoot, TimeProvider.System).ApplyAsync(Manifest(), "paquete.zip", false, Ct);
        result.ShouldBe(UpdateResult.Applied);
        host.Steps.ShouldBe(["stage 1.4.0", "stop", "activate 1.4.0", "start", "health", "cleanup"]);
    }

    // ---- Ventana BusinessPost.exe dentro del paquete (app\<versión>\desktop): equipo real con uniones NTFS (solo Windows) ----

    [Fact]
    public async Task El_paquete_trae_la_ventana_y_el_cambio_de_version_y_la_vuelta_atras_la_cambian()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows (uniones NTFS).");
            return;
        }

        var install = Path.Combine(_dataRoot, "install");
        CrearVersion(install, "1.3.0");
        var anterior = Directory.CreateDirectory(Path.Combine(install, "desktop")).FullName; // instalación previa: accesos a {app}\desktop
        await File.WriteAllTextAsync(Path.Combine(anterior, "BusinessPost.exe"), "ventana 1.3.0", Ct);
        var host = RealHost(install);
        host.Activate("1.3.0");

        var paquete = Path.Combine(_dataRoot, "BusinessPost-1.4.0.zip");
        CrearPaquete(paquete, "1.4.0");
        await host.StageAsync("1.4.0", paquete, Ct);
        host.IsStaged("1.4.0").ShouldBeTrue();
        File.Exists(Path.Combine(install, "app", "1.4.0", "desktop", "LICENSE-WebView2.txt")).ShouldBeTrue();
        Ventana(install).ShouldBe("ventana 1.3.0"); // preparada pero todavía no activa

        host.Activate("1.4.0");
        host.CurrentVersion.ShouldBe("1.4.0");
        Ventana(install).ShouldBe("ventana 1.4.0");
        (await File.ReadAllTextAsync(Path.Combine(anterior, "BusinessPost.exe"), Ct)).ShouldBe("ventana 1.4.0");
        File.Exists(Path.Combine(anterior, "LICENSE-WebView2.txt")).ShouldBeTrue();

        host.Activate("1.3.0"); // vuelta atrás
        Ventana(install).ShouldBe("ventana 1.3.0");
        (await File.ReadAllTextAsync(Path.Combine(anterior, "BusinessPost.exe"), Ct)).ShouldBe("ventana 1.3.0");
    }

    [Fact]
    public async Task Con_la_ventana_abierta_no_se_cierra_y_la_nueva_queda_para_la_proxima_apertura()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows.");
            return;
        }

        var install = Path.Combine(_dataRoot, "install");
        CrearVersion(install, "1.4.0");
        var anterior = Directory.CreateDirectory(Path.Combine(install, "desktop")).FullName;
        var abierta = Path.Combine(anterior, "BusinessPost.exe");
        using var ventana = Abrir(abierta);
        try
        {
            var avisos = VentanaEscritorio.RefrescarCopiaAnterior(install, Path.Combine(install, "app", "1.4.0"));

            avisos.ShouldBeEmpty();
            ventana.HasExited.ShouldBeFalse(); // el programa abierto sigue corriendo (renombrado)
            (await File.ReadAllTextAsync(abierta, Ct)).ShouldBe("ventana 1.4.0");
            Directory.GetFiles(anterior, "*.anterior").ShouldHaveSingleItem();
        }
        finally
        {
            Cerrar(ventana);
        }

        // Ya cerrada: el próximo cambio de versión borra el renombrado.
        VentanaEscritorio.RefrescarCopiaAnterior(install, Path.Combine(install, "app", "1.4.0")).ShouldBeEmpty();
        Directory.GetFiles(anterior, "*.anterior").ShouldBeEmpty();
    }

    [Fact]
    public void La_limpieza_no_borra_la_version_con_la_ventana_abierta()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows (uniones NTFS).");
            return;
        }

        var install = Path.Combine(_dataRoot, "install");
        foreach (var version in new[] { "1.2.0", "1.3.0", "1.4.0" })
        {
            CrearVersion(install, version);
        }

        var host = RealHost(install);
        host.Activate("1.4.0");
        var vieja = Path.Combine(install, "app", "1.2.0");
        using var ventana = Abrir(Path.Combine(vieja, "desktop", "BusinessPost.exe"));
        try
        {
            VentanaEscritorio.EnUso(vieja).ShouldBeTrue();
            host.CleanupOldVersions(2);
            File.Exists(Path.Combine(vieja, "server", "Pos.Server.Host.exe")).ShouldBeTrue(); // no quedó a medias
        }
        finally
        {
            Cerrar(ventana);
        }

        VentanaEscritorio.EnUso(vieja).ShouldBeFalse();
        host.CleanupOldVersions(2);
        Directory.Exists(vieja).ShouldBeFalse();
        Directory.Exists(Path.Combine(install, "app", "1.3.0")).ShouldBeTrue();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private WindowsUpdateHost RealHost(string install) =>
        new(new UpdaterSettings("BusinessPost", UpdaterMode.Server, _dataRoot, install, null, UpdateChannels.Stable, null, null, 6, 2, 5480, 5490, 10,
            null, null, [], []), Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsUpdateHost>.Instance);

    private static string Ventana(string install) => File.ReadAllText(Path.Combine(install, "app", "current", "desktop", "BusinessPost.exe"));

    /// <summary>Carpeta de una versión ya instalada, con archivos de texto en lugar de los programas.</summary>
    private static void CrearVersion(string install, string version)
    {
        foreach (var (carpeta, archivo) in Contenido)
        {
            var ruta = Path.Combine(install, "app", version, carpeta, archivo);
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            File.WriteAllText(ruta, carpeta == "desktop" && archivo == "BusinessPost.exe" ? $"ventana {version}" : $"{archivo} {version}");
        }
    }

    /// <summary>Paquete de actualización como lo arma build-installer.ps1: server, migrator, agent y desktop en la raíz del ZIP.</summary>
    private static void CrearPaquete(string zip, string version)
    {
        var origen = Path.Combine(Path.GetDirectoryName(zip)!, "paquete-" + version);
        CrearVersion(origen, version);
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(origen, "app", version), zip);
    }

    private static readonly (string Carpeta, string Archivo)[] Contenido =
    [
        ("server", "Pos.Server.Host.exe"), ("migrator", "Pos.Server.Migrator.exe"), ("agent", "Pos.Terminal.Agent.exe"),
        ("desktop", "BusinessPost.exe"), ("desktop", "LICENSE-WebView2.txt"),
    ];

    /// <summary>Simula la ventana abierta: una copia de PING.EXE con el nombre BusinessPost.exe, corriendo sin ventana.</summary>
    private static System.Diagnostics.Process Abrir(string ruta)
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ruta, overwrite: true);
        return System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ruta, "-n 120 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
    }

    private static void Cerrar(System.Diagnostics.Process proceso)
    {
        if (!proceso.HasExited)
        {
            proceso.Kill();
        }

        proceso.WaitForExit();
    }

    private UpdateOrchestrator Orchestrator(FakeUpdateHost host) => new(host, UpdaterMode.Server, _dataRoot, TimeProvider.System);

    private sealed class FakeUpdateHost(string version) : IUpdateHost
    {
        private readonly HashSet<string> _staged = [version];

        public List<string> Steps { get; } = [];

        public int OpenSessions { get; init; }

        public bool MigrateSucceeds { get; init; } = true;

        public string[]? HealthyVersions { get; init; }

        public string CurrentVersion { get; private set; } = version;

        public bool IsStaged(string v) => _staged.Contains(v);

        public Task StageAsync(string v, string packagePath, CancellationToken cancellationToken) => Step($"stage {v}", () => _staged.Add(v));

        public Task<int> OpenCashSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(OpenSessions);

        public Task<string> BackupAsync(string v, CancellationToken cancellationToken)
        {
            Steps.Add($"backup {v}");
            return Task.FromResult($"backup-{v}.posbak");
        }

        public Task<bool> MigrateAsync(string v, CancellationToken cancellationToken)
        {
            Steps.Add($"migrate {v}");
            return Task.FromResult(MigrateSucceeds);
        }

        public Task<bool> RestoreAsync(string v, string backupPath, CancellationToken cancellationToken)
        {
            Steps.Add($"restore {v} {backupPath}");
            return Task.FromResult(true);
        }

        public Task StopServicesAsync(CancellationToken cancellationToken) => Step("stop");

        public Task StartServicesAsync(CancellationToken cancellationToken) => Step("start");

        public void Activate(string v)
        {
            Steps.Add($"activate {v}");
            CurrentVersion = v;
        }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            Steps.Add("health");
            return Task.FromResult(HealthyVersions is null || HealthyVersions.Contains(CurrentVersion));
        }

        public void CleanupOldVersions(int keep) => Steps.Add("cleanup");

        private Task Step(string name, Action? action = null)
        {
            Steps.Add(name);
            action?.Invoke();
            return Task.CompletedTask;
        }
    }
}
