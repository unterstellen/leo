using Leo.Core;
using Leo.Ipc;
using Xunit.Abstractions;

namespace Leo.Tests;

/// <summary>
/// Talks to the installed LEO service (read-only calls). Skipped unless LEO_LIVE_TESTS=1,
/// so CI and machines without the service aren't affected.
/// </summary>
public sealed class LiveServiceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Hello_And_GetNetworks_Against_Installed_Service()
    {
        if (Environment.GetEnvironmentVariable("LEO_LIVE_TESTS") != "1") return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await PipeTransport.ConnectClientAsync(ct: cts.Token);
        using var rpc = PipeTransport.CreateRpc(stream);
        var control = rpc.Attach<ILeoControl>();
        rpc.StartListening();

        var hello = await control.HelloAsync(new HelloRequest(LeoInfo.ProtocolVersion, "live-test", "1"), cts.Token);
        output.WriteLine($"hello: {hello}");
        Assert.True(hello.Compatible);

        var networks = await control.GetNetworksAsync(cts.Token);
        foreach (var a in networks.Adapters)
        {
            output.WriteLine($"#{a.PriorityIndex} {a.DisplayName} {a.Kind} {a.Status} {a.Network?.Name} metric={a.Metric} primary={a.IsPrimary}");
        }
        Assert.NotEmpty(networks.Adapters);
    }
}
