using Leo.Core;
using Leo.Ipc;
using Leo.Service;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Leo.Tests;

/// <summary>End-to-end JSON-RPC over a real named pipe, using the same transport and target the service uses.</summary>
public sealed class ControlPipeTests
{
    [Fact]
    public async Task Hello_GetStatus_And_StatusChanged_RoundTrip()
    {
        var pipeName = $"LEO.Test.{Guid.NewGuid():N}";
        var status = new EngineStatusProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Server side (mirrors ControlPipeServer.ServeAsync).
        var serverTask = Task.Run(async () =>
        {
            await using var pipe = PipeTransport.CreateServerStream(pipeName);
            await pipe.WaitForConnectionAsync(cts.Token);
            var client = ClientIdentity.FromPipe(pipe);
            using var target = new LeoControlTarget(status, client, NullLogger.Instance);
            using var rpc = PipeTransport.CreateRpc(pipe);
            rpc.AddLocalRpcTarget<ILeoControl>(target, new JsonRpcTargetOptions { NotifyClientOfEvents = true });
            rpc.StartListening();
            await rpc.Completion.WaitAsync(cts.Token);
            return client;
        });

        // Client side (mirrors the UI's ServiceConnection).
        await using var stream = await PipeTransport.ConnectClientAsync(pipeName, 5000, cts.Token);
        using var clientRpc = PipeTransport.CreateRpc(stream);
        var proxy = clientRpc.Attach<ILeoControl>();
        clientRpc.StartListening();

        var hello = await proxy.HelloAsync(new HelloRequest(LeoInfo.ProtocolVersion, "tests", "1.0"), cts.Token);
        Assert.True(hello.Compatible);
        Assert.Equal(LeoInfo.Version, hello.ServiceVersion);

        status.SetState(EngineState.Running);
        var snapshot = await proxy.GetStatusAsync(cts.Token);
        Assert.Equal(EngineState.Running, snapshot.State);

        var received = new TaskCompletionSource<EngineStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.StatusChanged += (_, s) => received.TrySetResult(s);
        status.SetState(EngineState.Stopping);
        var pushed = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(EngineState.Stopping, pushed.State);

        clientRpc.Dispose();
        var identity = await serverTask;
        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.Equal(Environment.UserDomainName + "\\" + Environment.UserName, identity.UserName, ignoreCase: true);
    }

    [Fact]
    public async Task Incompatible_Protocol_Is_Reported()
    {
        var target = new LeoControlTarget(new EngineStatusProvider(),
            new ClientIdentity(1, "x", "u", ClientAccess.ReadOnly), NullLogger.Instance);
        var hello = await target.HelloAsync(new HelloRequest(LeoInfo.ProtocolVersion + 1, "old-ui", "0.0"));
        Assert.False(hello.Compatible);
        Assert.Equal(ClientAccess.ReadOnly, hello.Access);
    }
}
