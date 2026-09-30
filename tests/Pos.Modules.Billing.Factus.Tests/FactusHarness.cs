using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pos.Modules.Billing.FactusFake;
using Pos.Modules.Billing.Infrastructure.Factus;

namespace Pos.Modules.Billing.Factus.Tests;

/// <summary>Cliente real (registrado con AddFactusApi) conectado al Factus simulado en memoria, con reloj compartido.</summary>
internal sealed class FactusHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private FactusHarness(FactusFakeServer server, ManualTimeProvider time, ServiceProvider services, CapturingLoggerProvider logs)
    {
        Server = server;
        Time = time;
        _services = services;
        Logs = logs;
        Api = services.GetRequiredService<IFactusApi>();
    }

    public FactusFakeServer Server { get; }
    public ManualTimeProvider Time { get; }
    public IFactusApi Api { get; }
    public CapturingLoggerProvider Logs { get; }

    public static FactusOptions Options(Uri baseUrl, TimeSpan? timeout = null, string? password = null) => new()
    {
        Environment = FactusEnvironment.Sandbox,
        BaseUrl = baseUrl,
        ClientId = FactusFakeServer.DefaultClientId,
        ClientSecret = FactusFakeServer.DefaultClientSecret,
        Username = FactusFakeServer.DefaultUsername,
        Password = password ?? FactusFakeServer.DefaultPassword,
        RequestTimeout = timeout ?? TimeSpan.FromSeconds(10),
    };

    public static FactusHarness Create(Action<FactusFakeServer>? configure = null, FactusOptions? options = null, bool configured = true, bool inMemory = true)
    {
        var time = new ManualTimeProvider();
        var server = new FactusFakeServer(time);
        configure?.Invoke(server);

        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IFactusOptionsProvider>(new StaticFactusOptionsProvider(configured ? options ?? Options(server.BaseUrl) : null));
        var http = services.AddFactusApi();
        if (inMemory)
            http.ConfigurePrimaryHttpMessageHandler(server.CreateHandler);

        return new FactusHarness(server, time, services.BuildServiceProvider(), logs);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await Server.DisposeAsync();
    }
}

/// <summary>Guarda los mensajes de log para comprobar que no filtran credenciales ni tokens.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public ILogger CreateLogger(string categoryName) => new Logger(_messages);

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
