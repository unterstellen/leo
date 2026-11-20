using Leo.Core;
using Leo.Core.Config;
using Leo.Ipc;
using Leo.Service;
using Leo.Service.Networking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Leo.Tests;

/// <summary>The service's real host wiring (same hosted services), on a private pipe, in-process.</summary>
public sealed class HostedServiceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Full_Host_Answers_Hello_And_GetNetworks()
    {
        var pipeName = $"LEO.Test.{Guid.NewGuid():N}";
        var configPath = Path.Combine(Path.GetTempPath(), $"leo-config-{Guid.NewGuid():N}.json");

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new XunitLoggerProvider(output));
        builder.Services.AddSingleton(new ConfigStore(configPath));
        builder.Services.AddSingleton<EngineStatusProvider>();
        builder.Services.AddSingleton<NetworkInventory>();
        builder.Services.AddSingleton<NotificationHub>();
        builder.Services.AddSingleton(sp => new NetworkManager(sp.GetRequiredService<NetworkInventory>(),
            sp.GetRequiredService<ConfigStore>(), sp.GetRequiredService<NotificationHub>(),
            sp.GetRequiredService<ILogger<NetworkManager>>()) { ProbesEnabled = false });
        builder.Services.AddHostedService<EngineHeartbeat>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkManager>());
        builder.Services.AddHostedService(sp => new ControlPipeServer(
            sp.GetRequiredService<EngineStatusProvider>(), sp.GetRequiredService<NetworkManager>(),
            sp.GetRequiredService<ILoggerFactory>()) { PipeName = pipeName });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var stream = await PipeTransport.ConnectClientAsync(pipeName, 5000, cts.Token);
            using var rpc = PipeTransport.CreateRpc(stream);
            var control = rpc.Attach<ILeoControl>();
            rpc.StartListening();

            var hello = control.HelloAsync(new HelloRequest(LeoInfo.ProtocolVersion, "host-test", "1"), cts.Token);
            Assert.Same(hello, await Task.WhenAny(hello, Task.Delay(8000)));
            Assert.True((await hello).Compatible);

            var snapshot = await control.GetNetworksAsync(cts.Token);
            output.WriteLine($"adapters: {snapshot.Adapters.Count}");
        }
        finally
        {
            await host.StopAsync();
            File.Delete(configPath);
        }
    }

    private sealed class XunitLoggerProvider(ITestOutputHelper output) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new L(output, categoryName);
        public void Dispose() { }

        private sealed class L(ITestOutputHelper output, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                try { output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {logLevel} {category.Split('.').Last()}: {formatter(state, exception)}"); } catch { }
            }
        }
    }
}
