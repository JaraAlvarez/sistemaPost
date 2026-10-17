using Pos.Infrastructure.Backup;

namespace Pos.Infrastructure.UnitTests;

/// <summary>
/// Comprobación de la carpeta de un destino de backups (pantallas faltantes §1, PA3/PA4/PA7) con carpetas temporales reales. Las carpetas
/// temporales están en el mismo disco que la "LOCAL" de la prueba: la regla de otro volumen se prueba aparte y en las demás se permite.
/// </summary>
public sealed class BackupFolderProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pos-probe-" + Guid.NewGuid().ToString("N"));

    public BackupFolderProbeTests() => Directory.CreateDirectory(Local);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Local => Path.Combine(_root, "backups", "local");

    private Task<BackupFolderProbeResult> External(string? path, string? label = null, bool allowSameVolume = true) =>
        BackupFolderProbe.CheckAsync(path, label, Local, BackupFolderKind.External, allowSameVolume, Ct);

    private Task<BackupFolderProbeResult> Network(string path) =>
        BackupFolderProbe.CheckAsync(path, null, Local, BackupFolderKind.Network, allowSameVolume: true, Ct);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("Backups")]
    [InlineData("..\\otra")]
    [InlineData(@"\\?\C:\Backups")]
    [InlineData(@"\\.\C:\Backups")]
    public async Task Sin_ruta_relativa_o_de_dispositivo_no_sirve(string? path)
    {
        var result = await External(path);
        result.Ok.ShouldBeFalse();
        result.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task La_carpeta_LOCAL_y_sus_subcarpetas_no_sirven_como_destino()
    {
        (await External(Local)).Message.ShouldContain("carpeta LOCAL");
        (await External(Local + Path.DirectorySeparatorChar)).Ok.ShouldBeFalse();
        (await External(Path.Combine(Local, "copia"))).Ok.ShouldBeFalse();
        Directory.CreateDirectory(Path.Combine(_root, "backups", "local-usb"));
        (await External(Path.Combine(_root, "backups", "local-usb"))).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Un_alias_de_la_carpeta_LOCAL_se_descubre_con_el_archivo_de_prueba()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Junction (no pide privilegios) que apunta a la LOCAL: por la ruta parece otra carpeta.
        var alias = Path.Combine(_root, "alias");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{alias}\" \"{Local}\"")
               {
                   UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
               })!)
        {
            await mklink.WaitForExitAsync(Ct);
        }

        Directory.Exists(alias).ShouldBeTrue();
        var result = await External(alias);
        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("carpeta LOCAL");
        Directory.EnumerateFileSystemEntries(Local).ShouldBeEmpty();
        Directory.Delete(alias); // quita solo el enlace
    }

    [Fact]
    public async Task Una_carpeta_existente_se_escribe_y_no_queda_el_archivo_de_prueba()
    {
        var usb = Directory.CreateDirectory(Path.Combine(_root, "usb")).FullName;

        var result = await External(usb);

        result.Ok.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("existe y se puede escribir");
        result.FullPath.ShouldBe(usb);
        result.FreeBytes.ShouldNotBeNull();
        Directory.EnumerateFileSystemEntries(usb).ShouldBeEmpty();
    }

    [Fact]
    public async Task Si_no_existe_basta_con_su_carpeta_superior_y_no_se_crea_nada()
    {
        var disco = Directory.CreateDirectory(Path.Combine(_root, "disco")).FullName;
        var nueva = Path.Combine(disco, "BackupsTienda");

        var result = await External(nueva);

        result.Ok.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("se creará en el primer backup");
        Directory.Exists(nueva).ShouldBeFalse();
        Directory.EnumerateFileSystemEntries(disco).ShouldBeEmpty();

        // Sin la superior tampoco: no se crean carpetas para probar.
        var lejana = Path.Combine(_root, "no", "existe", "BackupsTienda");
        (await External(lejana)).Message.ShouldContain("ni su carpeta superior");
        Directory.Exists(Path.Combine(_root, "no")).ShouldBeFalse();
    }

    [Fact]
    public async Task En_el_mismo_disco_que_la_LOCAL_no_sirve_salvo_que_se_permita()
    {
        var otra = Directory.CreateDirectory(Path.Combine(_root, "misma-unidad")).FullName;
        if (OperatingSystem.IsWindows())
        {
            (await External(otra, allowSameVolume: false)).Message.ShouldContain("mismo disco");
        }

        (await External(otra, allowSameVolume: true)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Un_archivo_no_es_una_carpeta_y_la_etiqueta_del_disco_debe_coincidir()
    {
        var file = Path.Combine(_root, "archivo.txt");
        await File.WriteAllTextAsync(file, "x", Ct);
        (await External(file)).Message.ShouldContain("es un archivo");

        var result = await External(Path.Combine(_root, "usb"), "ETIQUETA-QUE-NO-EXISTE-" + Guid.NewGuid().ToString("N")[..6]);
        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("no es 'ETIQUETA-QUE-NO-EXISTE");
    }

    [Fact]
    public async Task Disco_externo_no_acepta_UNC_y_la_red_no_acepta_letras()
    {
        (await External(@"\\NAS\backups")).Message.ShouldContain("con su letra");
        (await Network(Path.Combine(_root, "usb"))).Message.ShouldContain("\\\\equipo\\carpeta");
    }

    [Theory]
    [InlineData(@"\\localhost\backups")]
    [InlineData(@"\\LOCALHOST\c$\backups")]
    [InlineData(@"\\127.0.0.1\backups")]
    [InlineData(@"\\127.10.0.5\backups")]
    [InlineData(@"\\0.0.0.0\backups")]
    [InlineData(@"\\[::1]\backups")]
    [InlineData(@"\\0--1.ipv6-literal.net\backups")]
    public async Task La_red_no_acepta_este_mismo_equipo(string path)
    {
        BackupFolderProbe.NetworkHostProblem(BackupFolderProbe.HostOf(path), NoDns).ShouldNotBeNull().ShouldContain("es este mismo equipo");
        (await Network(path)).Message.ShouldContain("es este mismo equipo");
    }

    [Fact]
    public async Task La_red_no_acepta_el_nombre_ni_las_IP_de_este_equipo()
    {
        var machine = Environment.MachineName;
        (await Network($@"\\{machine}\backups")).Message.ShouldContain("es este mismo equipo");
        (await Network($@"\\{machine.ToLowerInvariant()}.midominio.local\backups")).Ok.ShouldBeFalse();
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
        if (own is not null)
        {
            BackupFolderProbe.NetworkHostProblem(own.ToString(), NoDns).ShouldNotBeNull().ShouldContain("es este mismo equipo");
        }

        BackupFolderProbe.IsUnc(@"\\NAS\backups").ShouldBeTrue();
        BackupFolderProbe.IsUnc(@"E:\backups").ShouldBeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8", false)]
    [InlineData("200.1.2.3", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("10.0.0.20", true)]
    [InlineData("172.16.5.9", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("fd12:3456::1", true)]
    public void Solo_equipos_con_IP_privada(string host, bool allowed) =>
        (BackupFolderProbe.NetworkHostProblem(host, NoDns) is null).ShouldBe(allowed);

    [Fact]
    public void Un_nombre_debe_resolver_solo_a_IP_privadas()
    {
        BackupFolderProbe.NetworkHostProblem("NAS-TIENDA", _ => [System.Net.IPAddress.Parse("192.168.1.20")]).ShouldBeNull();
        BackupFolderProbe.NetworkHostProblem("nas.ejemplo.com", _ => [System.Net.IPAddress.Parse("192.168.1.20"), System.Net.IPAddress.Parse("34.1.2.3")])
            .ShouldNotBeNull().ShouldContain("no es un equipo de la red local");
        BackupFolderProbe.NetworkHostProblem("trampa", _ => [System.Net.IPAddress.Loopback]).ShouldNotBeNull().ShouldContain("es este mismo equipo");
        BackupFolderProbe.NetworkHostProblem("no-existe", _ => throw new System.Net.Sockets.SocketException()).ShouldNotBeNull().ShouldContain("No se encuentra");
    }

    private static IReadOnlyList<System.Net.IPAddress> NoDns(string host) => throw new InvalidOperationException($"No debía resolver {host}");
}
