using System.Net;
using System.Reflection;
using Pos.Infrastructure.Backup;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Domain;

namespace Pos.Infrastructure.UnitTests;

/// <summary>
/// Revisión R6: el outbox SYNC no lleva secretos ([Sensitive]) y enmascara los datos personales ([PersonalData]) igual que la auditoría.
/// Revisión R4: la tienda sin permiso de borrar en S3 no falla (la retención la hace el ciclo de vida del bucket).
/// </summary>
public class SyncOutboxRedactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Los_secretos_no_viajan_y_los_datos_personales_van_enmascarados()
    {
        PosSaveChangesInterceptor.IsSensitive(Property(nameof(Sample.PasswordHash))).ShouldBeTrue();
        PosSaveChangesInterceptor.IsSensitive(Property(nameof(Sample.Name))).ShouldBeFalse();

        PosSaveChangesInterceptor.SyncValue(Property(nameof(Sample.Email)), "laura.mejia@gmail.com")!.GetValue<string>().ShouldBe("l***@gmail.com");
        PosSaveChangesInterceptor.SyncValue(Property(nameof(Sample.Phone)), "+57 300 123 4567")!.GetValue<string>().ShouldBe("***4567");
        PosSaveChangesInterceptor.SyncValue(Property(nameof(Sample.Address)), "Calle 10 # 20-30")!.GetValue<string>().ShouldBe("(registrado)");
        PosSaveChangesInterceptor.SyncValue(Property(nameof(Sample.Email)), "  ").ShouldBeNull();

        // Lo demás va tal cual.
        PosSaveChangesInterceptor.SyncValue(Property(nameof(Sample.Name)), "Laura")!.GetValue<string>().ShouldBe("Laura");
        PosSaveChangesInterceptor.SyncValue(null, 12.5m)!.GetValue<decimal>().ShouldBe(12.5m);
    }

    [Fact]
    public async Task Sin_permiso_de_borrar_en_S3_el_borrado_no_falla()
    {
        var denied = new StatusHandler(HttpStatusCode.Forbidden);
        var storage = new S3Storage(new HttpClient(denied), new S3Settings("https://backups.test", "us-east-1", "pos-backups", "tienda", "secreto"),
            TimeProvider.System);
        (await storage.TryDeleteAsync("900123456-S01/abc/archivo.posbak", Ct)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => storage.DeleteAsync("900123456-S01/abc/archivo.posbak", Ct));
        denied.Requests.ShouldAllBe(r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath == "/pos-backups/900123456-S01/abc/archivo.posbak");

        var skewed = new S3Storage(new HttpClient(new StatusHandler(HttpStatusCode.Forbidden, "RequestTimeTooSkewed")),
            new S3Settings("https://backups.test", "us-east-1", "pos-backups", "tienda", "secreto"), TimeProvider.System);
        (await Should.ThrowAsync<InvalidOperationException>(() => skewed.TryDeleteAsync("x/archivo.posbak", Ct))).Message.ShouldContain("hora");

        var allowed = new S3Storage(new HttpClient(new StatusHandler(HttpStatusCode.NoContent)),
            new S3Settings("https://backups.test", "us-east-1", "pos-backups", "tienda", "secreto"), TimeProvider.System);
        (await allowed.TryDeleteAsync("x/archivo.posbak", Ct)).ShouldBeTrue();
    }

    private static PropertyInfo Property(string name) => typeof(Sample).GetProperty(name)!;

    private sealed class Sample
    {
        public string? Name { get; set; }

        [Sensitive]
        public string? PasswordHash { get; set; }

        [PersonalData(PersonalDataKind.Email)]
        public string? Email { get; set; }

        [PersonalData(PersonalDataKind.Phone)]
        public string? Phone { get; set; }

        [PersonalData(PersonalDataKind.FreeText)]
        public string? Address { get; set; }
    }

    private sealed class StatusHandler(HttpStatusCode status, string code = "AccessDenied") : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent($"<Error><Code>{code}</Code></Error>") });
        }
    }
}
