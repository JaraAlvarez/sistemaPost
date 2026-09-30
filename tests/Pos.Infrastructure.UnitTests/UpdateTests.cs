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
