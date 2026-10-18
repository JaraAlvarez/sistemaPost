using Pos.Infrastructure.Backup;

namespace Pos.Infrastructure.UnitTests;

/// <summary>IN4: actualización del esquema con vuelta atrás automática al backup PRE_UPDATE.</summary>
public sealed class GuardedSchemaUpdateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Backup = @"C:\ProgramData\BusinessPost\backups\pre-update.posbak";

    [Fact]
    public async Task Si_la_migracion_sale_bien_no_se_restaura_nada()
    {
        var restaurado = false;

        var resultado = await GuardedSchemaUpdate.RunAsync(
            _ => Task.CompletedTask, Backup, true, (_, _) => { restaurado = true; return Task.FromResult("pos_rb"); }, Ct);

        resultado.Outcome.ShouldBe(GuardedUpdateOutcome.Updated);
        restaurado.ShouldBeFalse();
    }

    [Fact]
    public async Task Si_una_migracion_falla_a_mitad_se_restaura_el_backup_previo_antes_de_volver_a_la_version_anterior()
    {
        string? archivoRestaurado = null;

        var resultado = await GuardedSchemaUpdate.RunAsync(
            _ => throw new InvalidOperationException("El script V0042 falló"),
            Backup,
            true,
            (archivo, _) => { archivoRestaurado = archivo; return Task.FromResult("pos_rb20261017"); },
            Ct);

        resultado.Outcome.ShouldBe(GuardedUpdateOutcome.FailedRestored);
        archivoRestaurado.ShouldBe(Backup);
        resultado.RestoredDatabase.ShouldBe("pos_rb20261017");
        resultado.MigrationError!.Message.ShouldContain("V0042");
        resultado.RestoreError.ShouldBeNull();
    }

    [Fact]
    public async Task Si_la_restauracion_tambien_falla_queda_para_restaurar_con_soporte_con_la_ruta_del_backup()
    {
        var resultado = await GuardedSchemaUpdate.RunAsync(
            _ => throw new InvalidOperationException("El script V0042 falló"),
            Backup,
            true,
            (_, _) => throw new IOException("pg_restore terminó con código 1"),
            Ct);

        resultado.Outcome.ShouldBe(GuardedUpdateOutcome.FailedNeedsSupport);
        resultado.BackupFile.ShouldBe(Backup);
        resultado.RestoreError.ShouldBeOfType<IOException>();
    }

    [Fact]
    public async Task Sin_backup_y_con_cambios_de_esquema_pendientes_no_se_puede_volver_atras()
    {
        var resultado = await GuardedSchemaUpdate.RunAsync(
            _ => throw new InvalidOperationException("falló"), null, true, (_, _) => Task.FromResult("x"), Ct);

        resultado.Outcome.ShouldBe(GuardedUpdateOutcome.FailedNeedsSupport);
    }

    [Fact]
    public async Task Sin_cambios_de_esquema_pendientes_un_fallo_deja_los_datos_como_estaban()
    {
        var resultado = await GuardedSchemaUpdate.RunAsync(
            _ => throw new InvalidOperationException("falló un script repetible"), null, false, (_, _) => Task.FromResult("x"), Ct);

        resultado.Outcome.ShouldBe(GuardedUpdateOutcome.FailedRestored);
        resultado.RestoredDatabase.ShouldBeNull();
    }

    [Fact]
    public async Task La_cancelacion_no_dispara_la_vuelta_atras()
    {
        var restaurado = false;

        await Should.ThrowAsync<OperationCanceledException>(() => GuardedSchemaUpdate.RunAsync(
            _ => throw new OperationCanceledException(), Backup, true, (_, _) => { restaurado = true; return Task.FromResult("x"); }, Ct));

        restaurado.ShouldBeFalse();
    }
}
