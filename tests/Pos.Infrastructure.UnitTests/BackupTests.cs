using System.Security.Cryptography;
using Pos.Infrastructure.Backup;

namespace Pos.Infrastructure.UnitTests;

/// <summary>Fase 11: paquete cifrado, código de recuperación, retención y firma S3.</summary>
public class BackupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BackupHeader Header(byte[] key, byte[] payload, WrappedKey? recovery = null) => new(
        BackupPackage.CurrentFormat, "1.0.0", "2026.10.029", "900123456-8", "Tienda", "S01", Guid.CreateVersion7(), 1, DateTimeOffset.UtcNow, "MANUAL",
        7, "AAAA-BBBB-CCCC-DDDD", Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length, BackupKeyStore.KeyId(key), recovery,
        new Dictionary<string, long> { ["sales.sales"] = 3 });

    private static async Task<byte[]> PackAsync(BackupHeader header, byte[] payload, byte[] key)
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(payload);
        await BackupPackage.WriteAsync(output, header, input, key, Ct);
        return output.ToArray();
    }

    private static async Task<byte[]> UnpackAsync(byte[] package, byte[] key)
    {
        using var input = new MemoryStream(package);
        using var output = new MemoryStream();
        await BackupPackage.DecryptAsync(input, key, output, Ct);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1024 * 1024)]
    [InlineData((1024 * 1024 * 2) + 17)]
    public async Task El_paquete_se_descifra_igual_con_cualquier_tamano(int size)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = RandomNumberGenerator.GetBytes(size);
        var package = await PackAsync(Header(key, payload), payload, key);

        (await UnpackAsync(package, key)).ShouldBe(payload);
        using var headerOnly = new MemoryStream(package);
        (await BackupPackage.ReadHeaderAsync(headerOnly, Ct)).BranchCode.ShouldBe("S01");
    }

    [Fact]
    public async Task Un_byte_alterado_un_archivo_truncado_o_la_clave_equivocada_se_rechazan()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = RandomNumberGenerator.GetBytes((1024 * 1024) + 500);
        var package = await PackAsync(Header(key, payload), payload, key);

        var altered = (byte[])package.Clone();
        altered[^100] ^= 0x01;
        await Should.ThrowAsync<BackupPackageException>(() => UnpackAsync(altered, key));

        // Truncado exactamente después del primer bloque completo: el bloque que queda no tiene la marca de final.
        var firstChunkEnd = package.Length - (500 + 4 + 12 + 16);
        await Should.ThrowAsync<BackupPackageException>(() => UnpackAsync(package[..firstChunkEnd], key));

        await Should.ThrowAsync<BackupPackageException>(() => UnpackAsync(package, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public async Task Cambiar_el_encabezado_invalida_el_paquete()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = RandomNumberGenerator.GetBytes(2000);
        var package = await PackAsync(Header(key, payload), payload, key);
        var text = System.Text.Encoding.UTF8.GetString(package);
        var index = text.IndexOf("\"S01\"", StringComparison.Ordinal);
        package[index + 3] = (byte)'9'; // S01 → S09 en el encabezado legible

        await Should.ThrowAsync<BackupPackageException>(() => UnpackAsync(package, key));
    }

    [Fact]
    public void El_codigo_de_recuperacion_abre_la_clave_y_otro_codigo_no()
    {
        var code = RecoveryCode.Generate();
        code.ShouldMatch("^([0-9A-HJKMNP-TV-Z]{4}-){5}[0-9A-HJKMNP-TV-Z]{4}$");
        var key = RandomNumberGenerator.GetBytes(32);
        var wrapped = WrappedKey.Wrap(key, code, 1, memoryKiB: 8192, iterations: 1);

        wrapped.Unwrap(code).ShouldBe(key);
        wrapped.Unwrap(code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)).ShouldBe(key);
        wrapped.Unwrap(RecoveryCode.Generate()).ShouldBeNull();
        wrapped.Unwrap("corto").ShouldBeNull();
        RecoveryCode.Normalize("0000-1111-2222-3333-4444-555O").ShouldBe("000011112222333344445550");
    }

    [Fact]
    public void La_retencion_conserva_diarios_semanales_mensuales_y_el_ultimo_verificado()
    {
        var start = new DateOnly(2026, 1, 1);
        var backups = Enumerable.Range(0, 120)
            .Select(i => new RetainedBackup($"b-{start.AddDays(i):yyyyMMdd}", start.AddDays(i), "NIGHTLY", Verified: true))
            .Append(new RetainedBackup("p-1", start.AddDays(10), RetentionPolicy.PreUpdateKind, true))
            .ToList();

        var delete = new RetentionPolicy(Daily: 7, Weekly: 4, Monthly: 3, PreUpdate: 1).ToDelete(backups);
        var kept = backups.Select(b => b.Name).Except(delete.Select(d => d.Name)).ToList();

        kept.ShouldContain("b-20260430"); // el último
        kept.ShouldContain("b-20260424"); // 7 diarios
        kept.ShouldNotContain("b-20260423");
        kept.ShouldContain("p-1"); // último de pre-actualización
        kept.ShouldContain("b-20260331"); // último de marzo (mensual)
        kept.Count.ShouldBeLessThan(20);

        new RetentionPolicy().ToDelete([new RetainedBackup("x", start, "NIGHTLY", Verified: false)]).ShouldBeEmpty();
    }

    /// <summary>Ejemplo publicado por AWS (GET Object con Range) para la firma Signature Version 4.</summary>
    [Fact]
    public void La_firma_S3_coincide_con_el_ejemplo_de_AWS()
    {
        var authorization = S3Signer.Authorization(
            "GET",
            "/test.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string>
            {
                ["Host"] = "examplebucket.s3.amazonaws.com",
                ["Range"] = "bytes=0-9",
                ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                ["x-amz-date"] = "20130524T000000Z",
            },
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero),
            "us-east-1",
            "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");

        authorization.ShouldEndWith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");
        authorization.ShouldContain("SignedHeaders=host;range;x-amz-content-sha256;x-amz-date");
    }

    [Fact]
    public void La_configuracion_viaja_sin_secretos()
    {
        var sanitized = BackupEngine.Sanitize(
            """{ "Pos": { "Database": { "ConnectionString": "dpapi:AAAA", "BackupConnectionString": "Host=x;Username=pos_backup;Password=secreta;Database=pos" } } }""");

        sanitized.ShouldNotContain("AAAA");
        sanitized.ShouldNotContain("secreta");
        sanitized.ShouldContain("Password=***");
    }
}
