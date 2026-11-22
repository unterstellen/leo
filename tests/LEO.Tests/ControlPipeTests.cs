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
            using var target = new LeoControlTarget(status, null, client, NullLogger.Instance);
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

        // This dev box's user is an administrator (UAC split token), so the service must grant Full access.
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var isAdminUser = me.Claims.Any(c => c.Value == "S-1-5-32-544") ||
                          new System.Security.Principal.WindowsPrincipal(me).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (isAdminUser) Assert.Equal(ClientAccess.Full, identity.Access);
    }

    [Fact]
    public async Task RoundTrip_With_NetworkManager_Attached()
    {
        var pipeName = $"LEO.Test.{Guid.NewGuid():N}";
        var configPath = Path.Combine(Path.GetTempPath(), $"leo-config-{Guid.NewGuid():N}.json");
        var networks = new Leo.Service.Networking.NetworkManager(
            new Leo.Service.Networking.NetworkInventory(NullLogger<Leo.Service.Networking.NetworkInventory>.Instance),
            new Leo.Core.Config.ConfigStore(configPath),
            new NotificationHub(NullLogger<NotificationHub>.Instance),
            NullLogger<Leo.Service.Networking.NetworkManager>.Instance) { ProbesEnabled = false };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Run it like the service does (scans + raises Changed). Non-admin: metric writes just fail.
        var firstSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        networks.Changed += (_, _) => firstSnapshot.TrySetResult();
        await networks.StartAsync(cts.Token);
        await firstSnapshot.Task.WaitAsync(cts.Token);

        var serverTask = Task.Run(async () =>
        {
            await using var pipe = PipeTransport.CreateServerStream(pipeName);
            await pipe.WaitForConnectionAsync(cts.Token);
            var client = ClientIdentity.FromPipe(pipe);
            using var target = new LeoControlTarget(new EngineStatusProvider(), networks, client, NullLogger.Instance);
            using var rpc = PipeTransport.CreateRpc(pipe);
            rpc.AddLocalRpcTarget<ILeoControl>(target, new JsonRpcTargetOptions { NotifyClientOfEvents = true });
            rpc.StartListening();
            await rpc.Completion.WaitAsync(cts.Token);
        });

        await using var stream = await PipeTransport.ConnectClientAsync(pipeName, 5000, cts.Token);
        using var clientRpc = PipeTransport.CreateRpc(stream);
        var proxy = clientRpc.Attach<ILeoControl>();
        clientRpc.StartListening();

        var helloTask = proxy.HelloAsync(new HelloRequest(LeoInfo.ProtocolVersion, "tests", "1.0"), cts.Token);
        var finished = await Task.WhenAny(helloTask, serverTask, Task.Delay(8000));
        if (finished == serverTask) await serverTask; // surfaces the server-side exception
        Assert.Same(helloTask, finished);
        Assert.True((await helloTask).Compatible);

        // The round trip is what's tested. How many adapters the machine has (and whether the first scan has
        // finished on a cold CI VM) isn't, so an empty list is fine here.
        var snapshot = await proxy.GetNetworksAsync(cts.Token);
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Adapters);
        await networks.StopAsync(CancellationToken.None);
        File.Delete(configPath);
    }

    [Fact]
    public async Task ReadOnly_Client_Cannot_Change_Anything()
    {
        var target = new LeoControlTarget(new EngineStatusProvider(), null,
            new ClientIdentity(1, "x", "standard-user", ClientAccess.ReadOnly), NullLogger.Instance);

        async Task Denied(Func<Task> call) =>
            Assert.Equal(LeoErrorCodes.AccessDenied, (await Assert.ThrowsAsync<LocalRpcException>(call)).ErrorCode);

        await Denied(() => target.SetPrimaryAsync("{X}"));
        await Denied(() => target.SetPriorityOrderAsync(["{X}"]));
        await Denied(() => target.SetAdapterNicknameAsync("{X}", "nick"));
        await Denied(() => target.SetNetworkNicknameAsync("wifi:X", "nick"));
    }

    [Fact]
    public async Task Incompatible_Protocol_Is_Reported()
    {
        var target = new LeoControlTarget(new EngineStatusProvider(), null,
            new ClientIdentity(1, "x", "u", ClientAccess.ReadOnly), NullLogger.Instance);
        var hello = await target.HelloAsync(new HelloRequest(LeoInfo.ProtocolVersion + 1, "old-ui", "0.0"));
        Assert.False(hello.Compatible);
        Assert.Equal(ClientAccess.ReadOnly, hello.Access);
    }
}
