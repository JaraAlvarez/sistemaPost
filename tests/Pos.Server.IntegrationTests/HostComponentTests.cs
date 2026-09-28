using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Server.Host.Configuration;
using Pos.Server.Host.ErrorHandling;
using Pos.Server.Host.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Pos.Server.IntegrationTests;

public class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task En_produccion_no_expone_detalles_internos()
    {
        var (context, body) = CreateContext();
        var handler = new GlobalExceptionHandler(
            context.RequestServices.GetRequiredService<IProblemDetailsService>(),
            new FakeEnvironment(Environments.Production),
            NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException("cadena de conexión secreta"), TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(500);
        var json = JsonDocument.Parse(body.ToArray()).RootElement;
        json.GetProperty("code").GetString().ShouldBe(GlobalExceptionHandler.UnexpectedErrorCode);
        json.GetProperty("detail").GetString()!.ShouldNotContain("secreta");
        json.GetProperty("detail").GetString()!.ShouldContain("código de correlación");
    }

    private static (DefaultHttpContext Context, MemoryStream Body) CreateContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = body;
        return (context, body);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Pos.Server.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

public class SensitiveDataMaskingEnricherTests
{
    [Theory]
    [InlineData("Password", true)]
    [InlineData("NewPassword", true)]
    [InlineData("AccessToken", true)]
    [InlineData("UserPin", true)]
    [InlineData("Pin", true)]
    [InlineData("Cvv", true)]
    [InlineData("Mapping", false)]
    [InlineData("Shipping", false)]
    [InlineData("ProductName", false)]
    public void Detecta_nombres_sensibles(string property, bool expected) =>
        SensitiveDataMaskingEnricher.IsSensitive(property).ShouldBe(expected);

    [Fact]
    public void Enmascara_el_valor_de_propiedades_sensibles()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Login {Username} {Password}"),
            [
                new LogEventProperty("Username", new ScalarValue("cajero1")),
                new LogEventProperty("Password", new ScalarValue("1234secreto")),
            ]);

        new SensitiveDataMaskingEnricher().Enrich(logEvent, new SimplePropertyFactory());

        logEvent.Properties["Password"].ToString().ShouldBe("\"***\"");
        logEvent.Properties["Username"].ToString().ShouldBe("\"cajero1\"");
    }

    private sealed class SimplePropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }
}

public class ProductPathsTests
{
    [Fact]
    public void Por_defecto_usa_ProgramData()
    {
        var paths = ProductPaths.From(new ConfigurationBuilder().Build());

        paths.DataRoot.ShouldBe(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PosSupermercado"));
        paths.ServerConfigFile.ShouldEndWith(Path.Combine("config", "server.json"));
        paths.LogsDirectory.ShouldEndWith("logs");
    }

    [Fact]
    public void Ruta_relativa_se_resuelve_junto_al_ejecutable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Pos:DataRoot"] = ".data" })
            .Build();

        ProductPaths.From(configuration).DataRoot.ShouldBe(Path.Combine(AppContext.BaseDirectory, ".data"));
    }
}
