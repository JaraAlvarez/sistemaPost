using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Licensing.Contracts;
using Pos.Server.Updater;
using Pos.Updates.Contracts;

namespace Pos.Infrastructure.UnitTests;

/// <summary>
/// Revisión del actualizador (R5, U1-U5 y bajas): descarga y manifiesto (CheckAsync con un servidor simulado), frescura, claves,
/// SemVer, registro de la actualización en curso, espera creciente, y lo que se puede probar del equipo real (WindowsUpdateHost) sin
/// servicios de Windows.
/// </summary>
public sealed class UpdaterSecurityTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly LicenseSigningKey _key = LicenseSigningKey.Generate();
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "pos-updater-tests", Guid.NewGuid().ToString("N"));

    public UpdaterSecurityTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        _key.Dispose();
        var enlace = Path.Combine(_dataRoot, "install", "app", "current");
        if (Directory.Exists(enlace))
        {
            Directory.Delete(enlace);
        }

        if (Directory.Exists(_dataRoot))
        {
            StagedContents.DeleteWithoutFollowingLinks(_dataRoot);
        }
    }

    // ───────────────────────────── CheckAsync (U5) ─────────────────────────────

    private static readonly byte[] Package = Encoding.UTF8.GetBytes("paquete de la versión 1.4.0");

    private static UpdateManifest Manifest(string version = "1.4.0", byte[]? package = null, DateTimeOffset? expires = null, long sequence = 100)
    {
        var bytes = package ?? Package;
        return new UpdateManifest("BusinessPost", UpdateChannels.Stable, version, $"BusinessPost-{version}.zip", Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length, version, DateTimeOffset.UtcNow, "Novedades", expires ?? DateTimeOffset.UtcNow.AddDays(30), sequence);
    }

    private UpdateWorker Worker(UpdateManifest manifest, byte[]? served = null, LicenseKeyRing? ring = null, string current = "1.3.0",
        string manifestUrl = "https://actualizaciones.test/stable.json")
    {
        var settings = Settings(UpdaterMode.Server, manifestUrl);
        var handler = new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(manifest, _key)), served ?? Package);
        return new UpdateWorker(settings, new Equipo(current), ring ?? new LicenseKeyRing([_key.PublicKey]), TimeProvider.System,
            NullLogger<UpdateWorker>.Instance, handler);
    }

    private UpdaterSettings Settings(UpdaterMode mode, string? manifestUrl = "https://actualizaciones.test/stable.json") =>
        new("BusinessPost", mode, _dataRoot, Path.Combine(_dataRoot, "install"), manifestUrl, UpdateChannels.Stable, null, null, 6, 2, 5480, 5490, 10,
            null, null, [], []);

    [Fact]
    public async Task Un_paquete_valido_queda_listo()
    {
        using var worker = Worker(Manifest());
        await worker.CheckAsync(Ct);

        worker.LastError.ShouldBeNull();
        worker.ReadyManifest!.Version.ShouldBe("1.4.0");
    }

    [Fact]
    public async Task Con_la_huella_mala_el_paquete_se_borra_y_no_queda_listo()
    {
        using var worker = Worker(Manifest(), served: Encoding.UTF8.GetBytes("paquete ALTERADO de la versión"));
        await worker.CheckAsync(Ct);

        worker.ReadyManifest.ShouldBeNull();
        worker.LastError!.ShouldContain("no coincide con la huella");
        File.Exists(Path.Combine(UpdateFiles.Directory(_dataRoot), "1.4.0.zip")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("1.3.0")]
    [InlineData("1.2.9")]
    public async Task Una_version_igual_o_anterior_no_se_instala(string offered)
    {
        using var worker = Worker(Manifest(offered));
        await worker.CheckAsync(Ct);

        worker.ReadyManifest.ShouldBeNull();
        worker.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Con_el_llavero_vacio_se_rechaza()
    {
        using var worker = Worker(Manifest(), ring: new LicenseKeyRing([]));
        await worker.CheckAsync(Ct);

        worker.ReadyManifest.ShouldBeNull();
        worker.LastError.ShouldBe($"Manifiesto rechazado ({ManifestStatus.UnknownKey}).");
    }

    [Fact]
    public async Task Un_manifiesto_vencido_o_sin_frescura_se_rechaza()
    {
        using (var vencido = Worker(Manifest(expires: DateTimeOffset.UtcNow.AddMinutes(-1))))
        {
            await vencido.CheckAsync(Ct);
            vencido.LastError.ShouldBe($"Manifiesto rechazado ({ManifestStatus.Expired}).");
        }

        using var viejo = Worker(Manifest(sequence: 0));
        await viejo.CheckAsync(Ct);
        viejo.LastError.ShouldBe($"Manifiesto rechazado ({ManifestStatus.MissingFreshness}).");
    }

    [Fact]
    public async Task Un_manifiesto_con_secuencia_menor_que_la_ultima_vista_se_rechaza()
    {
        using (var nuevo = Worker(Manifest(sequence: 500)))
        {
            await nuevo.CheckAsync(Ct);
            nuevo.ReadyManifest.ShouldNotBeNull();
        }

        // Repetición: el mismo producto firmado, pero de una publicación anterior.
        using var repetido = Worker(Manifest(sequence: 400));
        await repetido.CheckAsync(Ct);
        repetido.ReadyManifest.ShouldBeNull();
        repetido.LastError.ShouldBe($"Manifiesto rechazado ({ManifestStatus.Replayed}).");
    }

    [Fact]
    public async Task El_manifiesto_por_http_no_se_consulta()
    {
        using var worker = Worker(Manifest(), manifestUrl: "http://actualizaciones.test/stable.json");
        await worker.CheckAsync(Ct);

        worker.ReadyManifest.ShouldBeNull();
        worker.LastError!.ShouldContain("https");
    }

    [Fact]
    public async Task Con_current_json_danado_no_se_actualiza_y_se_avisa()
    {
        using var worker = Worker(Manifest(), current: string.Empty);
        await worker.CheckAsync(Ct);

        worker.ReadyManifest.ShouldBeNull();
        worker.LastError!.ShouldContain("current.json");
    }

    [Fact]
    public void En_Release_no_se_usan_claves_de_desarrollo()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pos:Updates:DevelopmentTrustedKeys:0"] = _key.PublicKey.X,
        }).Build();

        UpdaterSettings.From(configuration, "BusinessPost", _dataRoot, _dataRoot, production: true).DevelopmentTrustedKeys.ShouldBeEmpty();
        var fueraDeProduccion = UpdaterSettings.From(configuration, "BusinessPost", _dataRoot, _dataRoot, production: false).DevelopmentTrustedKeys;
        var compiladas = UpdaterSettings.DevelopmentKeysCompiledIn;
        if (compiladas)
        {
            fueraDeProduccion.ShouldHaveSingleItem();
        }
        else
        {
            fueraDeProduccion.ShouldBeEmpty("En una compilación Release nunca se leen claves de desarrollo de la configuración.");
        }
    }

    [Fact]
    public void Las_claves_revocadas_no_se_cargan()
    {
        using var revocada = LicenseSigningKey.Generate();
        var json = $$"""
            { "keys": [
              { "kid": "{{_key.Kid}}", "kty": "OKP", "crv": "Ed25519", "x": "{{_key.PublicKey.X}}", "status": "ACTIVE" },
              { "kid": "{{revocada.Kid}}", "kty": "OKP", "crv": "Ed25519", "x": "{{revocada.PublicKey.X}}", "status": "REVOKED" }
            ] }
            """;

        var ring = UpdateKeys.Parse(json, []);

        ring.Kids.ShouldBe([_key.Kid]);
    }

    [Theory]
    [InlineData("2.0.0-beta.2", "2.0.0-beta.10", -1)]
    [InlineData("2.0.0-alpha", "2.0.0-alpha.1", -1)]
    [InlineData("2.0.0-alpha.beta", "2.0.0-beta", -1)]
    [InlineData("2.0.0-1", "2.0.0-alpha", -1)]
    [InlineData("2.0.0-rc.1", "2.0.0", -1)]
    public void Las_preliminares_se_ordenan_por_SemVer(string left, string right, int expected)
    {
        SemanticVersion.TryParse(left, out var a).ShouldBeTrue();
        SemanticVersion.TryParse(right, out var b).ShouldBeTrue();
        Math.Sign(a.CompareTo(b)).ShouldBe(expected);
        Math.Sign(b.CompareTo(a)).ShouldBe(-expected);
    }

    // ───────────────────────────── Orquestador: registro en curso (U4) y espera creciente ─────────────────────────────

    [Fact]
    public async Task Una_actualizacion_a_medias_se_deshace_al_arrancar()
    {
        var host = new Equipo("1.4.0");
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Server, _dataRoot, TimeProvider.System);
        Directory.CreateDirectory(UpdateFiles.Directory(_dataRoot));
        await File.WriteAllTextAsync(Path.Combine(UpdateFiles.Directory(_dataRoot), UpdateFiles.InProgress),
            """{ "from": "1.3.0", "to": "1.4.0", "step": "Activated", "backup": "pre.posbak", "at": "2026-10-17T07:00:00Z" }""", Ct);

        (await orchestrator.RecoverAsync(Ct)).ShouldBe(UpdateResult.RolledBack);

        host.Steps.ShouldBe(["stop", "activate 1.3.0", "restore 1.3.0 pre.posbak", "start", "health"]);
        orchestrator.ReadJournal().ShouldBeNull();
        UpdateFiles.ReadHistory(_dataRoot).ShouldHaveSingleItem().Outcome.ShouldBe(UpdateOutcomes.RolledBack);
        (await orchestrator.RecoverAsync(Ct)).ShouldBeNull("Ya no queda nada pendiente.");
    }

    [Fact]
    public async Task Si_se_corto_antes_de_detener_los_servicios_solo_se_vuelven_a_arrancar()
    {
        var host = new Equipo("1.3.0");
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Server, _dataRoot, TimeProvider.System);
        Directory.CreateDirectory(UpdateFiles.Directory(_dataRoot));
        await File.WriteAllTextAsync(Path.Combine(UpdateFiles.Directory(_dataRoot), UpdateFiles.InProgress),
            """{ "from": "1.3.0", "to": "1.4.0", "step": "BackedUp", "backup": "pre.posbak", "at": "2026-10-17T07:00:00Z" }""", Ct);

        (await orchestrator.RecoverAsync(Ct)).ShouldBe(UpdateResult.RolledBack);
        host.Steps.ShouldBe(["stop", "start", "health"]);
    }

    [Fact]
    public async Task Durante_la_actualizacion_queda_el_registro_en_curso_y_al_terminar_se_borra()
    {
        var host = new Equipo("1.3.0");
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Server, _dataRoot, TimeProvider.System);
        host.OnMigrate = () => orchestrator.ReadJournal()!.Step.ShouldBe(UpdateStep.Stopped);

        (await orchestrator.ApplyAsync(Manifest(), "paquete.zip", force: true, Ct)).ShouldBe(UpdateResult.Applied);
        orchestrator.ReadJournal().ShouldBeNull();
    }

    [Fact]
    public async Task Si_no_se_detienen_los_servicios_se_reintenta_con_espera_creciente_sin_un_backup_por_minuto()
    {
        var host = new Equipo("1.3.0") { StopFails = true };
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 17, 7, 0, 0, TimeSpan.Zero));
        var settings = Settings(UpdaterMode.Server);
        using var worker = new UpdateWorker(settings, host, new LicenseKeyRing([_key.PublicKey]), reloj, NullLogger<UpdateWorker>.Instance,
            new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(), _key)), Package));
        var pedido = Path.Combine(UpdateFiles.Directory(_dataRoot), UpdateFiles.InstallNowRequest);

        Directory.CreateDirectory(UpdateFiles.Directory(_dataRoot));
        await File.WriteAllTextAsync(pedido, string.Empty, Ct);
        for (var minuto = 0; minuto < 5; minuto++)
        {
            // Solo el primer minuto con "instalar ahora"; después, la ventana de las 02:00 (hora local) con la espera vigente.
            await worker.TickAsync(Ct);
            reloj.Avanzar(TimeSpan.FromMinutes(1));
        }

        host.Steps.Count(s => s.StartsWith("backup", StringComparison.Ordinal)).ShouldBe(1, "Un solo backup PRE_UPDATE; después espera.");
        (worker.RetryAt - reloj.GetUtcNow()).ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);

        // Pasada la espera se reintenta una vez más (otro backup) y la espera siguiente es mayor (5 → 10 minutos).
        reloj.Avanzar(worker.RetryAt - reloj.GetUtcNow());
        await File.WriteAllTextAsync(pedido, string.Empty, Ct);
        await worker.TickAsync(Ct);
        host.Steps.Count(s => s.StartsWith("backup", StringComparison.Ordinal)).ShouldBe(2);
        (worker.RetryAt - reloj.GetUtcNow()).ShouldBe(TimeSpan.FromMinutes(10));
        UpdateFiles.ReadHistory(_dataRoot)[^1].Detail.ShouldContain("no se pudieron detener los servicios");
    }

    [Fact]
    public async Task En_la_caja_no_se_instala_fuera_de_la_ventana_salvo_que_lo_pidan()
    {
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
        var host = new Equipo("1.3.0");
        var settings = Settings(UpdaterMode.Terminal) with
        {
            ServerUrl = "https://principal.test:5443/",
            WindowHour = (TimeZoneInfo.ConvertTime(reloj.GetUtcNow(), TimeZoneInfo.Local).Hour + 3) % 24,
        };
        using var worker = new UpdateWorker(settings, host, new LicenseKeyRing([_key.PublicKey]), reloj, NullLogger<UpdateWorker>.Instance,
            new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(), _key)), Package));
        await worker.TickAsync(Ct);

        worker.ReadyManifest.ShouldNotBeNull();
        host.Steps.ShouldNotContain("stop", "El agente no se detiene en plena venta: se espera la madrugada.");

        Directory.CreateDirectory(UpdateFiles.Directory(_dataRoot));
        await File.WriteAllTextAsync(Path.Combine(UpdateFiles.Directory(_dataRoot), UpdateFiles.InstallNowRequest), string.Empty, Ct);
        await worker.TickAsync(Ct);
        host.Steps.ShouldContain("activate 1.4.0", "Con \"instalar ahora\" sí.");
    }

    // ───────────────────────────── Frescura del manifiesto ─────────────────────────────

    [Fact]
    public void La_caja_no_exige_la_caducidad_pero_si_la_secuencia()
    {
        var vencido = Manifest(expires: DateTimeOffset.UtcNow.AddDays(-90), sequence: 10);
        ManifestFreshness.Check(vencido, DateTimeOffset.UtcNow, 5, requireNotExpired: false).ShouldBe(ManifestStatus.Valid);
        ManifestFreshness.Check(vencido, DateTimeOffset.UtcNow, 5, requireNotExpired: true).ShouldBe(ManifestStatus.Expired);
        ManifestFreshness.Check(vencido, DateTimeOffset.UtcNow, 11, requireNotExpired: false).ShouldBe(ManifestStatus.Replayed);
    }

    // ───────────────────────────── Equipo real (sin servicios de Windows) ─────────────────────────────

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Preparar_verifica_la_huella_con_el_handle_exclusivo_y_deja_el_manifiesto_de_huellas()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows.");
            return;
        }

        var install = Path.Combine(_dataRoot, "install");
        UpdateTests.CrearVersion(install, "1.3.0");
        var host = Real(install);
        var paquete = Path.Combine(_dataRoot, "BusinessPost-1.4.0.zip");
        UpdateTests.CrearPaquete(paquete, "1.4.0");
        var manifest = UpdateTests.ManifestOf(paquete, "1.4.0");

        // Otra huella (paquete cambiado después de verificar): no se instala.
        await Should.ThrowAsync<InvalidOperationException>(() => host.StageAsync("1.4.0", paquete, manifest with { PackageSha256 = new string('0', 64) }, Ct));
        host.IsStaged("1.4.0", manifest.PackageSha256).ShouldBeFalse();

        await host.StageAsync("1.4.0", paquete, manifest, Ct);
        host.IsStaged("1.4.0", manifest.PackageSha256).ShouldBeTrue();
        host.IsStaged("1.4.0", new string('b', 64)).ShouldBeFalse("M3: preparada de OTRO paquete (otra huella) no cuenta.");
        File.Exists(Path.Combine(install, "app", "1.4.0", StagedContents.ManifestFile)).ShouldBeTrue();

        // U1: una carpeta que solo existe (la 1.3.0 la copió el instalador) o con un archivo cambiado no cuenta como preparada.
        host.IsStaged("1.3.0", manifest.PackageSha256).ShouldBeFalse();
        await File.WriteAllTextAsync(Path.Combine(install, "app", "1.4.0", "server", "Pos.Server.Host.exe"), "otro programa", Ct);
        host.IsStaged("1.4.0", manifest.PackageSha256).ShouldBeFalse();
    }

    [Fact]
    public async Task Un_paquete_con_rutas_fuera_de_su_carpeta_se_rechaza()
    {
        var zip = Path.Combine(_dataRoot, "malicioso.zip");
        using (var archivo = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            await using var escritura = await archivo.CreateEntry("../../fuera.exe").OpenAsync(Ct);
            await escritura.WriteAsync("x"u8.ToArray(), Ct);
        }

        await using var lectura = File.OpenRead(zip);
        using var zipLectura = new System.IO.Compression.ZipArchive(lectura);
        await Should.ThrowAsync<InvalidOperationException>(() => StagedContents.ExtractAsync(zipLectura, Path.Combine(_dataRoot, "destino"), "x", Ct));
        File.Exists(Path.Combine(_dataRoot, "..", "fuera.exe")).ShouldBeFalse();
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Activar_cambia_la_union_y_si_la_version_no_existe_deja_la_anterior()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows (uniones NTFS).");
            return;
        }

        var install = Path.Combine(_dataRoot, "install");
        UpdateTests.CrearVersion(install, "1.3.0");
        UpdateTests.CrearVersion(install, "1.4.0");
        var host = Real(install);
        host.Activate("1.3.0");
        host.Activate("1.4.0");
        host.CurrentVersion.ShouldBe("1.4.0");
        Directory.Exists(Path.Combine(install, "app", "current.nueva")).ShouldBeFalse();

        Should.Throw<InvalidOperationException>(() => host.Activate("9.9.9"));
        host.CurrentVersion.ShouldBe("1.4.0");
        File.Exists(Path.Combine(install, "app", "current", "server", "Pos.Server.Host.exe")).ShouldBeTrue();
    }

    [Fact]
    public async Task Con_current_json_danado_la_version_no_se_conoce()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows.");
            return;
        }

        var install = Path.Combine(_dataRoot, "install");
        Directory.CreateDirectory(Path.Combine(install, "app"));
        await File.WriteAllTextAsync(Path.Combine(install, "app", "current.json"), "{ dañado", Ct);
        Real(install).CurrentVersion.ShouldBe(string.Empty);
    }

    [Fact]
    public void La_copia_de_la_ventana_no_sigue_uniones()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows (uniones NTFS).");
            return;
        }

        var install = Path.Combine(_dataRoot, "install");
        UpdateTests.CrearVersion(install, "1.4.0");
        var objetivo = Directory.CreateDirectory(Path.Combine(_dataRoot, "sistema-protegido")).FullName;
        Union(Path.Combine(install, "desktop"), objetivo);

        var avisos = VentanaEscritorio.RefrescarCopiaAnterior(install, Path.Combine(install, "app", "1.4.0"));

        avisos.ShouldHaveSingleItem().ShouldContain("enlace");
        Directory.GetFiles(objetivo).ShouldBeEmpty("No se escribió nada a través de la unión.");
        Directory.Delete(Path.Combine(install, "desktop"));
    }

    [Fact]
    public void Borrar_no_sigue_uniones()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows (uniones NTFS).");
            return;
        }

        var protegido = Directory.CreateDirectory(Path.Combine(_dataRoot, "protegido")).FullName;
        File.WriteAllText(Path.Combine(protegido, "importante.txt"), "no borrar");
        var carpeta = Directory.CreateDirectory(Path.Combine(_dataRoot, "version-vieja")).FullName;
        Union(Path.Combine(carpeta, "enlace"), protegido);

        StagedContents.DeleteWithoutFollowingLinks(carpeta).ShouldBeNull();

        Directory.Exists(carpeta).ShouldBeFalse();
        File.Exists(Path.Combine(protegido, "importante.txt")).ShouldBeTrue();
    }

    [Fact]
    public void La_carpeta_de_la_instalacion_sale_del_programa_y_de_Inno_no_de_la_configuracion()
    {
        var updater = Path.Combine(_dataRoot, "install", "updater");
        UpdaterSecurity.ResolveInstallRoot(updater, null).ShouldBe(Path.Combine(_dataRoot, "install"));
        UpdaterSecurity.ResolveInstallRoot(updater, Path.Combine(_dataRoot, "install") + Path.DirectorySeparatorChar).ShouldBe(Path.Combine(_dataRoot, "install"));
        UpdaterSecurity.ResolveInstallRoot(updater, Path.Combine(_dataRoot, "otra")).ShouldBeNull("El actualizador copiado a otra carpeta no se confía.");
    }

    [Fact]
    public void Una_carpeta_en_la_que_los_usuarios_pueden_escribir_no_se_acepta()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows (ACL).");
            return;
        }

        var segura = new DirectorySecurity();
        segura.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        segura.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        segura.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        UpdaterSecurity.WritableByUnprivileged(segura, "prueba").ShouldBeNull();

        segura.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        UpdaterSecurity.WritableByUnprivileged(segura, "prueba").ShouldNotBeNull();
    }

    // ───────────────────────────── Ronda 2: ALTA 1-4, M1-M4, B1-B3 ─────────────────────────────

    private string JournalFile => Path.Combine(UpdateFiles.Directory(_dataRoot), UpdateFiles.InProgress);

    private async Task JournalAsync(string json)
    {
        Directory.CreateDirectory(UpdateFiles.Directory(_dataRoot));
        await File.WriteAllTextAsync(JournalFile, json, Ct);
    }

    [Theory]
    [InlineData("1.0.0 ")]
    [InlineData(" 1.0.0")]
    [InlineData("1.0.0-..")]
    [InlineData("1.0.0-a/b")]
    [InlineData("1.0.0-a\\b")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0-")]
    [InlineData("..\\..\\Windows")]
    [InlineData("1.0")]
    [InlineData("1.0.0-beta..1")]
    public void ALTA1_la_version_es_SemVer_estricta(string text) => SemanticVersion.TryParse(text, out _).ShouldBeFalse();

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0-rc-1+build.5")]
    public void ALTA1_las_versiones_normales_siguen_valiendo(string text) => SemanticVersion.IsSafeFolderName(text).ShouldBeTrue();

    [Fact]
    public async Task ALTA1_un_registro_con_versiones_que_no_son_carpetas_de_app_se_archiva_y_no_se_usa()
    {
        var host = new Equipo("1.4.0");
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Terminal, _dataRoot, TimeProvider.System);
        await JournalAsync("""{ "from": "..\\..\\..\\Windows\\Temp\\malo", "to": "1.4.0", "step": "Activated", "backup": null, "at": "2026-10-17T07:00:00Z" }""");

        (await orchestrator.RecoverAsync(Ct)).ShouldBeNull();
        host.Steps.ShouldBeEmpty("No se ejecutó nada con una versión inventada.");
        orchestrator.JournalProblem!.ShouldContain("versiones inválidas");
        File.Exists(JournalFile).ShouldBeFalse();

        // Una versión SemVer válida pero sin su carpeta en app tampoco.
        await JournalAsync("""{ "from": "9.9.9", "to": "1.4.0", "step": "Activated", "backup": null, "at": "2026-10-17T07:00:00Z" }""");
        (await orchestrator.RecoverAsync(Ct)).ShouldBeNull();
        host.Steps.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALTA1_un_registro_que_no_es_de_SYSTEM_ni_de_Administradores_se_borra()
    {
        var host = new Equipo("1.4.0");
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Server, _dataRoot, TimeProvider.System, isTrustedFile: _ => false);
        await JournalAsync("""{ "from": "1.3.0", "to": "1.4.0", "step": "Activated", "backup": "pre.posbak", "at": "2026-10-17T07:00:00Z" }""");

        (await orchestrator.RecoverAsync(Ct)).ShouldBeNull();
        host.Steps.ShouldBeEmpty();
        File.Exists(JournalFile).ShouldBeFalse();
        orchestrator.JournalProblem!.ShouldContain("SYSTEM");
    }

    [Fact]
    public async Task ALTA1_una_secuencia_que_dejo_un_usuario_no_se_usa()
    {
        // Un usuario dejó una secuencia enorme para que el equipo rechace todos los manifiestos nuevos.
        Directory.CreateDirectory(UpdateFiles.Directory(_dataRoot));
        await File.WriteAllTextAsync(Path.Combine(UpdateFiles.Directory(_dataRoot), UpdateFiles.LastSequence), """{ "stable": 999999999999 }""", Ct);
        using var worker = new UpdateWorker(Settings(UpdaterMode.Server), new Equipo("1.3.0"), new LicenseKeyRing([_key.PublicKey]), TimeProvider.System,
            NullLogger<UpdateWorker>.Instance, new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(), _key)), Package))
        {
            IsTrustedFile = path => !path.EndsWith(UpdateFiles.LastSequence, StringComparison.Ordinal),
        };

        await worker.CheckAsync(Ct);

        worker.ReadyManifest.ShouldNotBeNull();
    }

    [Fact]
    public async Task ALTA2_desde_detenido_siempre_vuelve_a_activar_la_version_anterior()
    {
        // current.json todavía dice 1.3.0, pero la unión pudo quedar a medio cambiar: se activa 1.3.0 igual (idempotente).
        var host = new Equipo("1.3.0");
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Server, _dataRoot, TimeProvider.System);
        await JournalAsync("""{ "from": "1.3.0", "to": "1.4.0", "step": "Stopped", "backup": "pre.posbak", "at": "2026-10-17T07:00:00Z" }""");

        (await orchestrator.RecoverAsync(Ct)).ShouldBe(UpdateResult.RolledBack);
        host.Steps.ShouldBe(["stop", "activate 1.3.0", "restore 1.3.0 pre.posbak", "start", "health"]);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ALTA2_Activate_es_idempotente_y_recrea_app_current_si_falta()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows (uniones NTFS).");
            return;
        }

        var install = Path.Combine(_dataRoot, "install");
        UpdateTests.CrearVersion(install, "1.3.0");
        var host = Real(install);
        host.Activate("1.3.0");
        host.Activate("1.3.0");
        host.CurrentVersion.ShouldBe("1.3.0");

        // Corte de luz justo después de borrar la unión: no hay app\current.
        Directory.Delete(Path.Combine(install, "app", "current"));
        host.Activate("1.3.0");
        File.Exists(Path.Combine(install, "app", "current", "server", "Pos.Server.Host.exe")).ShouldBeTrue();

        // La versión activa sale de la unión aunque current.json diga otra cosa.
        File.WriteAllText(Path.Combine(install, "app", "current.json"), """{ "version": "1.9.9" }""");
        host.CurrentVersion.ShouldBe("1.3.0");
    }

    [Fact]
    public async Task ALTA3_no_restaura_dos_veces_cuenta_los_intentos_y_al_tercero_queda_para_soporte()
    {
        var host = new Equipo("1.3.0") { RestoreFails = true };
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Server, _dataRoot, TimeProvider.System);
        await JournalAsync("""{ "from": "1.3.0", "to": "1.4.0", "step": "Migrated", "backup": "pre.posbak", "at": "2026-10-17T07:00:00Z" }""");

        for (var intento = 1; intento <= UpdateOrchestrator.MaxRecoveryAttempts; intento++)
        {
            (await orchestrator.RecoverAsync(Ct)).ShouldBe(UpdateResult.RollbackFailed);
            host.Steps[^2].ShouldBe("start", "Los servicios se arrancan aunque falle la restauración (finally).");
            orchestrator.ReadJournal()!.Attempts.ShouldBe(intento);
        }

        host.Steps.Clear();
        (await orchestrator.RecoverAsync(Ct)).ShouldBe(UpdateResult.Pending);
        host.Steps.ShouldBe(["start"], "Requiere soporte: no se toca la BD, solo se arrancan los servicios.");
        orchestrator.ReadJournal()!.NeedsSupport.ShouldBeTrue();

        // No se empieza otra actualización con un registro pendiente.
        (await orchestrator.ApplyAsync(Manifest("1.5.0"), "paquete.zip", force: true, Ct)).ShouldBe(UpdateResult.Pending);
        host.Steps.ShouldNotContain("backup 1.3.0");
    }

    [Fact]
    public async Task ALTA3_si_ya_se_restauro_el_backup_no_se_restaura_otra_vez()
    {
        var host = new Equipo("1.3.0");
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Server, _dataRoot, TimeProvider.System);
        await JournalAsync("""{ "from": "1.3.0", "to": "1.4.0", "step": "Restored", "backup": "pre.posbak", "at": "2026-10-17T07:00:00Z", "attempts": 1 }""");

        (await orchestrator.RecoverAsync(Ct)).ShouldBe(UpdateResult.RolledBack);
        host.Steps.ShouldNotContain("restore 1.3.0 pre.posbak");
    }

    [Fact]
    public async Task B2_un_registro_danado_se_avisa()
    {
        var host = new Equipo("1.3.0");
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Server, _dataRoot, TimeProvider.System);
        await JournalAsync("{ dañado");

        (await orchestrator.RecoverAsync(Ct)).ShouldBeNull();
        orchestrator.JournalProblem!.ShouldContain("dañado");
        Directory.GetFiles(UpdateFiles.Directory(_dataRoot), "*.archivado").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task M1_si_no_se_puede_activar_la_version_nueva_vuelve_atras_y_arranca_los_servicios()
    {
        var host = new Equipo("1.3.0") { ActivateFails = true };
        var orchestrator = new UpdateOrchestrator(host, UpdaterMode.Server, _dataRoot, TimeProvider.System);

        (await orchestrator.ApplyAsync(Manifest(), "paquete.zip", force: true, Ct)).ShouldBe(UpdateResult.RolledBack);
        host.CurrentVersion.ShouldBe("1.3.0");
        host.Steps.ShouldContain("restore 1.3.0 backup-1.3.0.posbak");
        host.Steps[^2].ShouldBe("start");
        orchestrator.ReadJournal().ShouldBeNull();
    }

    [Fact]
    public async Task ALTA4_la_caja_instala_al_encender_el_equipo_antes_de_vender()
    {
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
        var host = new Equipo("1.3.0");
        var settings = Settings(UpdaterMode.Terminal) with
        {
            ServerUrl = "https://principal.test:5443/",
            WindowHour = (TimeZoneInfo.ConvertTime(reloj.GetUtcNow(), TimeZoneInfo.Local).Hour + 3) % 24,
        };
        using var worker = new UpdateWorker(settings, host, new LicenseKeyRing([_key.PublicKey]), reloj, NullLogger<UpdateWorker>.Instance,
            new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(), _key)), Package))
        {
            Uptime = () => TimeSpan.FromMinutes(2),
        };

        await worker.TickAsync(Ct);

        host.Steps.ShouldContain("activate 1.4.0");
    }

    [Fact]
    public async Task ALTA4_el_equipo_principal_propaga_instalar_ahora_a_la_caja()
    {
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
        var host = new Equipo("1.3.0");
        var settings = Settings(UpdaterMode.Terminal) with
        {
            ServerUrl = "https://principal.test:5443/",
            WindowHour = (TimeZoneInfo.ConvertTime(reloj.GetUtcNow(), TimeZoneInfo.Local).Hour + 3) % 24,
        };
        using var worker = new UpdateWorker(settings, host, new LicenseKeyRing([_key.PublicKey]), reloj, NullLogger<UpdateWorker>.Instance,
            new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(), _key)), Package) { InstallNow = true })
        {
            Uptime = () => TimeSpan.FromHours(5),
        };

        await worker.TickAsync(Ct);

        host.Steps.ShouldContain("activate 1.4.0");
    }

    [Fact]
    public async Task B3_si_el_equipo_principal_no_tiene_manifiesto_la_caja_no_muestra_error()
    {
        var settings = Settings(UpdaterMode.Terminal) with { ServerUrl = "https://principal.test:5443/" };
        using var sinManifiesto = new UpdateWorker(settings, new Equipo("1.3.0"), new LicenseKeyRing([_key.PublicKey]), TimeProvider.System,
            NullLogger<UpdateWorker>.Instance, new Servidor(null, Package));
        await sinManifiesto.CheckAsync(Ct);
        sinManifiesto.LastError.ShouldBeNull();

        using var viejo = new UpdateWorker(settings, new Equipo("1.3.0"), new LicenseKeyRing([_key.PublicKey]), TimeProvider.System,
            NullLogger<UpdateWorker>.Instance, new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(sequence: 0), _key)), Package));
        await viejo.CheckAsync(Ct);
        viejo.LastError.ShouldBeNull("Un manifiesto de antes de la revisión no deja un MissingFreshness permanente en la caja.");
        viejo.ReadyManifest.ShouldBeNull();
    }

    [Fact]
    public async Task M2_la_version_que_volvio_atras_se_recuerda_aunque_se_reinicie_el_actualizador()
    {
        var host = new Equipo("1.3.0") { ActivateFails = true };
        var settings = Settings(UpdaterMode.Server);
        var pedido = Path.Combine(UpdateFiles.Directory(_dataRoot), UpdateFiles.InstallNowRequest);
        using (var primero = new UpdateWorker(settings, host, new LicenseKeyRing([_key.PublicKey]), TimeProvider.System, NullLogger<UpdateWorker>.Instance,
                   new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(), _key)), Package)))
        {
            Directory.CreateDirectory(UpdateFiles.Directory(_dataRoot));
            await File.WriteAllTextAsync(pedido, string.Empty, Ct);
            await primero.TickAsync(Ct);
        }

        using var segundo = new UpdateWorker(settings, new Equipo("1.3.0"), new LicenseKeyRing([_key.PublicKey]), TimeProvider.System,
            NullLogger<UpdateWorker>.Instance, new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(), _key)), Package));
        await segundo.CheckAsync(Ct);
        segundo.ReadyManifest.ShouldBeNull("La 1.4.0 ya volvió atrás en este equipo: se espera una más nueva.");
    }

    [Fact]
    public async Task M2_instalar_ahora_se_atiende_aunque_haya_espera_por_un_fallo()
    {
        var host = new Equipo("1.3.0") { StopFails = true };
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
        using var worker = new UpdateWorker(Settings(UpdaterMode.Server), host, new LicenseKeyRing([_key.PublicKey]), reloj, NullLogger<UpdateWorker>.Instance,
            new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(), _key)), Package));
        var pedido = Path.Combine(UpdateFiles.Directory(_dataRoot), UpdateFiles.InstallNowRequest);
        Directory.CreateDirectory(UpdateFiles.Directory(_dataRoot));
        await File.WriteAllTextAsync(pedido, string.Empty, Ct);
        await worker.TickAsync(Ct);
        worker.RetryAt.ShouldBeGreaterThan(reloj.GetUtcNow());

        await File.WriteAllTextAsync(pedido, string.Empty, Ct);
        await worker.TickAsync(Ct);
        host.Steps.Count(s => s.StartsWith("backup", StringComparison.Ordinal)).ShouldBe(2, "El propietario lo pidió: se intenta ya.");
    }

    [Fact]
    public async Task M4_la_secuencia_se_guarda_por_canal_y_solo_despues_de_validar_el_canal()
    {
        using (var beta = new UpdateWorker(Settings(UpdaterMode.Server), new Equipo("1.3.0"), new LicenseKeyRing([_key.PublicKey]), TimeProvider.System,
                   NullLogger<UpdateWorker>.Instance,
                   new Servidor(UpdateSigning.Serialize(UpdateSigning.Sign(Manifest(sequence: 9000) with { Channel = UpdateChannels.Beta }, _key)), Package)))
        {
            await beta.CheckAsync(Ct);
            beta.ReadyManifest.ShouldBeNull("Este equipo está en el canal stable.");
        }

        File.Exists(Path.Combine(UpdateFiles.Directory(_dataRoot), UpdateFiles.LastSequence)).ShouldBeFalse("Un manifiesto de otro canal no mueve la secuencia.");
        using var stable = Worker(Manifest(sequence: 100));
        await stable.CheckAsync(Ct);
        stable.ReadyManifest.ShouldNotBeNull();
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void B1_los_permisos_genericos_de_escritura_tambien_cuentan()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo en Windows (ACL).");
            return;
        }

        // GENERIC_WRITE (GW) heredable para los Usuarios (BU), como lo deja a veces un instalador de terceros: .NET no deja crear esa regla
        // con su API, así que se arma desde SDDL.
        var acl = new DirectorySecurity();
        acl.SetSecurityDescriptorSddlForm("D:P(A;OICIIO;GW;;;BU)(A;OICI;FA;;;SY)");
        UpdaterSecurity.WritableByUnprivileged(acl, "prueba").ShouldNotBeNull();
    }

    [Fact]
    public async Task B1_con_un_problema_de_seguridad_no_se_deshace_nada_al_arrancar()
    {
        var host = new Equipo("1.4.0");
        await JournalAsync("""{ "from": "1.3.0", "to": "1.4.0", "step": "Activated", "backup": "pre.posbak", "at": "2026-10-17T07:00:00Z" }""");
        using var worker = new UpdateWorker(Settings(UpdaterMode.Server), host, new LicenseKeyRing([_key.PublicKey]), TimeProvider.System,
            NullLogger<UpdateWorker>.Instance)
        {
            SecurityProblem = "Un usuario sin privilegios puede escribir en la instalación.",
        };

        await worker.RecoverAtStartupAsync(Ct);

        host.Steps.ShouldBeEmpty();
        File.Exists(JournalFile).ShouldBeTrue();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private WindowsUpdateHost Real(string install) =>
        new(Settings(UpdaterMode.Server) with { InstallRoot = install }, NullLogger<WindowsUpdateHost>.Instance);

    private static void Union(string link, string target)
    {
        using var proceso = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        proceso.StandardOutput.ReadToEnd();
        proceso.WaitForExit();
        proceso.ExitCode.ShouldBe(0);
    }

    /// <summary>Servidor de actualizaciones simulado: el manifiesto firmado y el paquete.</summary>
    private sealed class Servidor(string? manifest, byte[] package) : HttpMessageHandler
    {
        /// <summary>El equipo principal propaga "instalar ahora" (cabecera del manifiesto).</summary>
        public bool InstallNow { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                || request.RequestUri.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal))
            {
                if (manifest is null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest, Encoding.UTF8, "application/json") };
                if (InstallNow)
                {
                    response.Headers.Add(UpdateWorker.TerminalInstallNowHeader, "1");
                }

                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        }
    }

    private sealed class RelojFijo(DateTimeOffset inicio) : TimeProvider
    {
        private DateTimeOffset _ahora = inicio;

        public void Avanzar(TimeSpan cuanto) => _ahora += cuanto;

        public override DateTimeOffset GetUtcNow() => _ahora;
    }

    /// <summary>Equipo simulado (servicios, versiones, migrador).</summary>
    private sealed class Equipo(string version) : IUpdateHost
    {
        public List<string> Steps { get; } = [];

        public bool StopFails { get; init; }

        public Action? OnMigrate { get; set; }

        public string CurrentVersion { get; private set; } = version;

        public bool IsStaged(string v, string packageSha256) => true;

        public HashSet<string> Versions { get; } = ["1.2.0", "1.3.0", "1.4.0", "1.5.0"];

        public bool HasVersion(string v) => Versions.Contains(v);

        public bool ActivateFails { get; init; }

        public bool RestoreFails { get; init; }

        public Task StageAsync(string v, string packagePath, UpdateManifest manifest, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> OpenCashSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<string> BackupAsync(string v, CancellationToken cancellationToken)
        {
            Steps.Add($"backup {v}");
            return Task.FromResult($"backup-{v}.posbak");
        }

        public Task<bool> MigrateAsync(string v, CancellationToken cancellationToken)
        {
            Steps.Add($"migrate {v}");
            OnMigrate?.Invoke();
            return Task.FromResult(true);
        }

        public Task<bool> RestoreAsync(string v, string backupPath, CancellationToken cancellationToken)
        {
            Steps.Add($"restore {v} {backupPath}");
            return Task.FromResult(!RestoreFails);
        }

        public Task StopServicesAsync(CancellationToken cancellationToken)
        {
            Steps.Add("stop");
            return StopFails ? Task.FromException(new InvalidOperationException("El servicio BusinessPost-Server no llegó a Stopped.")) : Task.CompletedTask;
        }

        public Task StartServicesAsync(CancellationToken cancellationToken)
        {
            Steps.Add("start");
            return Task.CompletedTask;
        }

        public void Activate(string v)
        {
            Steps.Add($"activate {v}");
            if (ActivateFails && v != CurrentVersion && Steps.Count(s => s.StartsWith("activate", StringComparison.Ordinal)) == 1)
            {
                throw new InvalidOperationException("No se pudo crear la unión.");
            }

            CurrentVersion = v;
        }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            Steps.Add("health");
            return Task.FromResult(true);
        }

        public void CleanupOldVersions(int keep) => Steps.Add("cleanup");
    }
}
