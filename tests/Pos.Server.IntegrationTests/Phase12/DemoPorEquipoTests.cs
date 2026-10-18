using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Messaging;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;
using Pos.Modules.Licensing.Infrastructure;
using Pos.Modules.Organization.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase12;

/// <summary>Equipo reinstalado borrando los datos: la marca del equipo (registro de Windows) recuerda que su demo empezó hace 40 días.</summary>
public sealed class ReinstalledDeviceServerFactory : LicenseServerFactory
{
    internal override IDeviceDemoMark DemoMark { get; } = new MemoryDeviceDemoMark(Fingerprint, DateTimeOffset.UtcNow.AddDays(-40));
}

/// <summary>Demo por equipo (docs/fases/fase-demo-por-equipo.md): reinstalar borrando los datos no reinicia la prueba gratuita.</summary>
public class DemoPorEquipoTests
{
    [Fact]
    public async Task Reinstalar_borrando_los_datos_no_reinicia_la_demo_por_la_marca_del_equipo()
    {
        await using var factory = new ReinstalledDeviceServerFactory();
        var owner = await OwnerClientAsync(factory); // BD nueva: sin la marca serían 30 días de demo

        var status = await RefreshAsync(factory);
        status.State.ShouldBe(LicenseStates.Restricted);
        status.Reason.ShouldBe(LicenseEvaluator.DemoEndedMessage);
        status.Notices.ShouldContain(n => n.Code == LicenseEvaluator.DemoEndedCode);

        var setup = await GetAsync<SetupStatusDto>(owner, "/api/v1/setup/status");
        setup.DemoEnded.ShouldBeTrue();

        // Restringida: no se administra; activar la licencia sí está permitido.
        await RawAsync(owner, HttpMethod.Post, "/api/v1/catalog/categories", new { name = "Nueva", sortOrder = 0 })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "LICENSE.RESTRICTED");
        (await PostAsync<LicenseStatusDto>(owner, "/api/v1/license/activate", new { licenseKey = FakeLicenseCloud.ValidKey })).State
            .ShouldBe(LicenseStates.Valid);
    }

    [Fact]
    public async Task La_nube_reconoce_el_equipo_aunque_no_quede_la_marca_local()
    {
        await using var factory = new LicenseServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        (await RefreshAsync(factory)).State.ShouldBe(LicenseStates.Demo);

        // La nube ya vio este equipo hace 31 días (otra instalación, borrada): devuelve ese inicio firmado y la tienda lo adopta.
        factory.Cloud.DemoStartedAt = DateTimeOffset.UtcNow.AddDays(-31);
        (await SendAsync<bool>(factory, new RegisterDemoDeviceCommand())).ShouldBeTrue();
        var status = await RefreshAsync(factory);
        status.State.ShouldBe(LicenseStates.Restricted);
        status.Reason.ShouldBe(LicenseEvaluator.DemoEndedMessage);
        (await GetAsync<SetupStatusDto>(shop.Owner, "/api/v1/setup/status")).DemoEnded.ShouldBeTrue();

        // Y la marca local queda guardada para cuando no haya Internet.
        factory.DemoMark.Read(LicenseServerFactory.Fingerprint).ShouldNotBeNull().ShouldBeLessThan(DateTimeOffset.UtcNow.AddDays(-30));
    }

    [Fact]
    public async Task Un_inicio_de_la_nube_mas_reciente_no_alarga_la_demo()
    {
        await using var factory = new ReinstalledDeviceServerFactory();
        await OwnerClientAsync(factory);

        factory.Cloud.DemoStartedAt = DateTimeOffset.UtcNow; // respuesta (válida o no) que "empezaría hoy"
        (await SendAsync<bool>(factory, new RegisterDemoDeviceCommand())).ShouldBeTrue();
        (await RefreshAsync(factory)).State.ShouldBe(LicenseStates.Restricted);
    }

    private static async Task<LicenseStatusDto> RefreshAsync(LicenseServerFactory factory) => await SendAsync<LicenseStatusDto>(factory, new RefreshLicenseCommand());

    private static async Task<T> SendAsync<T>(LicenseServerFactory factory, ICommand<T> command)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(command, Ct);
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }
}
