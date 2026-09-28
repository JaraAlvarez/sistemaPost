using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Messaging;
using Pos.Infrastructure.Messaging;
using Pos.Infrastructure.Messaging.Behaviors;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.UnitTests.Messaging;

public class DispatcherTests
{
    [Fact]
    public async Task Envia_la_consulta_a_su_handler()
    {
        using var host = BuildHost();
        var dispatcher = host.Get<IDispatcher>();

        var result = await dispatcher.Send(new Add(2, 3), TestContext.Current.CancellationToken);

        result.Value.ShouldBe(5);
    }

    [Fact]
    public async Task Validacion_fallida_no_ejecuta_el_handler_y_devuelve_errores_por_campo()
    {
        using var host = BuildHost();
        var dispatcher = host.Get<IDispatcher>();
        var tracker = host.Get<CallTracker>();

        var result = await dispatcher.Send(new RenameProduct(""), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ValidationBehavior<RenameProduct, Result>.ErrorCode);
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.FieldErrors.ShouldHaveSingleItem().Code.ShouldBe("PRODUCT.NAME_REQUIRED");
        tracker.Calls.ShouldNotContain(nameof(RenameProductHandler));
    }

    [Fact]
    public async Task Validacion_fallida_en_peticion_con_valor_devuelve_Result_generico()
    {
        using var host = BuildHost();
        var dispatcher = host.Get<IDispatcher>();

        var result = await dispatcher.Send(new Add(-1, 3), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.FieldErrors.ShouldHaveSingleItem().Field.ShouldBe(nameof(Add.A));
    }

    [Fact]
    public async Task Validacion_exitosa_ejecuta_el_handler()
    {
        using var host = BuildHost();
        var dispatcher = host.Get<IDispatcher>();
        var tracker = host.Get<CallTracker>();

        var result = await dispatcher.Send(new RenameProduct("Arroz 500 g"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        tracker.Calls.ShouldContain(nameof(RenameProductHandler));
    }

    [Fact]
    public async Task Comportamientos_se_ejecutan_en_orden_de_registro_alrededor_del_handler()
    {
        using var host = BuildHost(services =>
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TrackingBehavior<,>)));
        var dispatcher = host.Get<IDispatcher>();
        var tracker = host.Get<CallTracker>();

        await dispatcher.Send(new RenameProduct("Leche"), TestContext.Current.CancellationToken);

        tracker.Calls.ShouldBe(["TrackingBehavior:before", nameof(RenameProductHandler), "TrackingBehavior:after"]);
    }

    [Fact]
    public async Task Peticion_sin_handler_lanza_excepcion_clara()
    {
        using var host = BuildHost();
        var dispatcher = host.Get<IDispatcher>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.Send(new Orphan(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(Orphan));
    }

    [Fact]
    public void Registrar_dos_veces_los_handlers_se_detecta_como_duplicado()
    {
        var services = new ServiceCollection();
        services.AddRequestHandlersFrom(typeof(DispatcherTests).Assembly);

        Should.Throw<InvalidOperationException>(() => services.AddRequestHandlersFrom(typeof(DispatcherTests).Assembly))
            .Message.ShouldContain("más de un handler");
    }

    [Fact]
    public void Fabrica_de_fallos_rechaza_tipos_de_resultado_no_soportados()
    {
        var error = Error.Unexpected("TEST.ERROR", "error");

        ResultFailureFactory<Result>.Create(error).Error.ShouldBe(error);
        ResultFailureFactory<Result<string>>.Create(error).Error.ShouldBe(error);
        Should.Throw<TypeInitializationException>(() => ResultFailureFactory<CustomResult>.Create(error));
    }

    private static TestHost BuildHost(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPosInfrastructure(BusinessTimeZones.Colombia);
        services.AddRequestHandlersFrom(typeof(DispatcherTests).Assembly);
        services.AddScoped<CallTracker>();
        configure?.Invoke(services);
        return new TestHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
    }

    /// <summary>Proveedor + un scope por prueba (equivalente a una petición HTTP).</summary>
    private sealed class TestHost(ServiceProvider provider) : IDisposable
    {
        private readonly IServiceScope _scope = provider.CreateScope();

        public T Get<T>()
            where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

        public void Dispose()
        {
            _scope.Dispose();
            provider.Dispose();
        }
    }
}

internal sealed class CallTracker
{
    public List<string> Calls { get; } = [];
}

internal sealed record Add(int A, int B) : IQuery<int>;

internal sealed class AddValidator : AbstractValidator<Add>
{
    public AddValidator() => RuleFor(q => q.A).GreaterThanOrEqualTo(0);
}

internal sealed class AddHandler : IQueryHandler<Add, int>
{
    public Task<Result<int>> Handle(Add request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<int>>(request.A + request.B);
}

internal sealed record RenameProduct(string Name) : ICommand;

internal sealed class RenameProductValidator : AbstractValidator<RenameProduct>
{
    public RenameProductValidator() =>
        RuleFor(c => c.Name).NotEmpty().WithErrorCode("PRODUCT.NAME_REQUIRED");
}

internal sealed class RenameProductHandler(CallTracker tracker) : ICommandHandler<RenameProduct>
{
    public Task<Result> Handle(RenameProduct request, CancellationToken cancellationToken)
    {
        tracker.Calls.Add(nameof(RenameProductHandler));
        return Task.FromResult(Result.Success());
    }
}

internal sealed record Orphan : ICommand;

internal sealed class CustomResult() : Result(true, Error.None);

internal sealed class TrackingBehavior<TRequest, TResponse>(CallTracker tracker) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerNext<TResponse> next, CancellationToken cancellationToken)
    {
        tracker.Calls.Add("TrackingBehavior:before");
        var response = await next();
        tracker.Calls.Add("TrackingBehavior:after");
        return response;
    }
}
