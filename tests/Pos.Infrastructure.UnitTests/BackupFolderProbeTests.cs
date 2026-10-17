using Pos.Infrastructure.Backup;

namespace Pos.Infrastructure.UnitTests;

/// <summary>Comprobación de la carpeta de un destino de backups (pantallas faltantes §1) con carpetas temporales reales.</summary>
public sealed class BackupFolderProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pos-probe-" + Guid.NewGuid().ToString("N"));

    public BackupFolderProbeTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Local => Path.Combine(_root, "backups", "local");

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
    public async Task Sin_ruta_o_con_ruta_relativa_no_sirve(string? path)
    {
        var result = await BackupFolderProbe.CheckAsync(path, null, Local, Ct);
        result.Ok.ShouldBeFalse();
        result.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task La_carpeta_LOCAL_y_sus_subcarpetas_no_sirven_como_destino()
    {
        Directory.CreateDirectory(Local);
        (await BackupFolderProbe.CheckAsync(Local, null, Local, Ct)).Message.ShouldContain("carpeta LOCAL");
        (await BackupFolderProbe.CheckAsync(Local + Path.DirectorySeparatorChar, null, Local, Ct)).Ok.ShouldBeFalse();
        (await BackupFolderProbe.CheckAsync(Path.Combine(Local, "copia"), null, Local, Ct)).Ok.ShouldBeFalse();
        (await BackupFolderProbe.CheckAsync(Path.Combine(_root, "backups", "local-usb"), null, Local, Ct)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Una_carpeta_existente_se_escribe_y_no_queda_el_archivo_de_prueba()
    {
        var usb = Directory.CreateDirectory(Path.Combine(_root, "usb")).FullName;

        var result = await BackupFolderProbe.CheckAsync(usb, null, Local, Ct);

        result.Ok.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("existe y se puede escribir");
        result.FullPath.ShouldBe(usb);
        result.FreeBytes.ShouldNotBeNull();
        Directory.EnumerateFileSystemEntries(usb).ShouldBeEmpty();
    }

    [Fact]
    public async Task Una_carpeta_que_no_existe_se_prueba_creandola_y_se_borra_al_terminar()
    {
        var nueva = Path.Combine(_root, "disco", "BackupsTienda");

        var result = await BackupFolderProbe.CheckAsync(nueva, null, Local, Ct);

        result.Ok.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("se creará en el primer backup");
        Directory.Exists(nueva).ShouldBeFalse();
    }

    [Fact]
    public async Task Un_archivo_no_es_una_carpeta_y_la_etiqueta_del_disco_debe_coincidir()
    {
        var file = Path.Combine(_root, "archivo.txt");
        await File.WriteAllTextAsync(file, "x", Ct);
        (await BackupFolderProbe.CheckAsync(file, null, Local, Ct)).Message.ShouldContain("es un archivo");

        var result = await BackupFolderProbe.CheckAsync(Path.Combine(_root, "usb"), "ETIQUETA-QUE-NO-EXISTE-" + Guid.NewGuid().ToString("N")[..6], Local, Ct);
        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("no es 'ETIQUETA-QUE-NO-EXISTE");
    }

    [Fact]
    public async Task Una_carpeta_compartida_inexistente_se_informa_sin_lanzar()
    {
        BackupFolderProbe.IsUnc(@"\\NAS\backups").ShouldBeTrue();
        BackupFolderProbe.IsUnc(@"E:\backups").ShouldBeFalse();
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await BackupFolderProbe.CheckAsync(@"\\localhost\no-existe-" + Guid.NewGuid().ToString("N")[..6] + @"\tienda", null, Local, Ct);
        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("No se encuentra la carpeta compartida");
    }
}
