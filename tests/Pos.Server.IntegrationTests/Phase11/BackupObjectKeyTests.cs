using Pos.Modules.Backup.Infrastructure;

namespace Pos.Server.IntegrationTests.Phase11;

/// <summary>
/// R4: cada tienda escribe en S3 solo bajo su carpeta (<c>&lt;prefijo&gt;/&lt;id de la instalación&gt;/</c>); la política de su usuario la limita a
/// <c>pos-backups/&lt;NIT&gt;-&lt;sucursal&gt;/*</c> (despliegue-nube.md §14).
/// </summary>
public class BackupObjectKeyTests
{
    private static readonly Guid Node = Guid.Parse("01920000-0000-7000-8000-0000000000aa");

    [Theory]
    [InlineData("900123456-S01", "900123456-S01/019200000000700080000000000000aa/")]
    [InlineData("/900123456-S01/", "900123456-S01/019200000000700080000000000000aa/")]
    [InlineData("tiendas\\900123456-S01", "tiendas/900123456-S01/019200000000700080000000000000aa/")]
    [InlineData("../otra-tienda/./900123456-S01", "otra-tienda/900123456-S01/019200000000700080000000000000aa/")]
    [InlineData(null, "019200000000700080000000000000aa/")]
    [InlineData("  ", "019200000000700080000000000000aa/")]
    public void La_clave_del_objeto_queda_bajo_la_carpeta_de_la_tienda(string? prefix, string expected) =>
        S3Destination.ObjectPrefix(prefix, Node).ShouldBe(expected);
}
