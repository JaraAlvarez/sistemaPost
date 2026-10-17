using Microsoft.Extensions.Logging.Abstractions;
using Pos.Modules.Licensing.Infrastructure;

namespace Pos.Infrastructure.UnitTests;

/// <summary>Archivo protegido de la licencia (license.vault): ilegible por un rato no se sobrescribe y se recupera solo.</summary>
public sealed class LicenseVaultTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pos-vault-tests", Guid.CreateVersion7().ToString("N"));

    private string File => Path.Combine(_folder, "config", "license.vault");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    [Fact]
    public void Con_el_archivo_bloqueado_no_se_sobrescribe_y_se_recupera_al_liberarlo()
    {
        var original = new LicenseVault(File, NullLogger<LicenseVault>.Instance);
        original.Confirm(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-3), null);
        var mac = original.Mac("estado");
        var bytes = System.IO.File.ReadAllBytes(File);

        LicenseVault degraded;
        using (new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            degraded = new LicenseVault(File, NullLogger<LicenseVault>.Instance, readAttempts: 2);
            degraded.Degraded.ShouldBeTrue();
            degraded.UnavailableMessage.ShouldContain("No se pudo leer el archivo de la licencia");
            degraded.UnavailableMessage.ShouldContain(Path.Combine(_folder, "config"));
            degraded.Raise(DateTimeOffset.UtcNow.AddDays(1)); // lo visto mientras tanto queda en memoria, sin escribir
            degraded.RetryIfDegraded(force: true).ShouldBeFalse();
        }

        System.IO.File.ReadAllBytes(File).ShouldBe(bytes); // intacto: ni se sobrescribió ni se apartó

        degraded.RetryIfDegraded(force: true).ShouldBeTrue();
        degraded.Degraded.ShouldBeFalse();
        degraded.Mac("estado").ShouldBe(mac); // el mismo secreto: las firmas de la BD siguen valiendo
        degraded.Sealed.ShouldBeTrue();
        degraded.Watermark.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(23));
    }

    [Fact]
    public void Un_archivo_danado_se_aparta_como_bad_y_se_crea_otro()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "esto no es un archivo de licencia");

        var vault = new LicenseVault(File, NullLogger<LicenseVault>.Instance);

        vault.Degraded.ShouldBeFalse();
        vault.Sealed.ShouldBeFalse();
        Directory.GetFiles(Path.GetDirectoryName(File)!, "license.vault.*.bad").ShouldHaveSingleItem();
        System.IO.File.Exists(File).ShouldBeTrue();
    }
}
