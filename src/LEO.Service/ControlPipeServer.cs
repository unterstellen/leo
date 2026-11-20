using System.IO.Pipes;
using Leo.Core;
using Leo.Ipc;
using Leo.Service.Logging;
using Leo.Service.Networking;
using StreamJsonRpc;

namespace Leo.Service;

/// <summary>Accepts UI connections on \\.\pipe\LEO.Control and serves <see cref="ILeoControl"/> over JSON-RPC.</summary>
public sealed class ControlPipeServer(
    EngineStatusProvider status, NetworkManager networks, ILoggerFactory loggers,
    Leo.Service.Wifi.WifiManager? wifi = null, NotificationHub? hub = null) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger<ControlPipeServer>();

    /// <summary>Pipe name; overridable so tests can run a private instance.</summary>
    public string PipeName { get; init; } = LeoInfo.ControlPipeName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(@"Control pipe listening on \\.\pipe\{Pipe}", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = PipeTransport.CreateServerStream(PipeName);
                await pipe.WaitForConnectionAsync(stoppingToken);
                var session = pipe;
                pipe = null; // ownership moves to the session task
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ServeAsync(session, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Client session failed");
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Control pipe accept failed; retrying");
                try { await Task.Delay(1000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (pipe is not null) await pipe.DisposeAsync();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using var _ = pipe;
        var client = ClientIdentity.FromPipe(pipe);
        _log.LogInformation("Client connected: pid={Pid} user={User} image={Image} access={Access}",
            client.ProcessId, client.UserName, client.ImagePath, client.Access);

        // TODO(M8): reject clients whose image is not Authenticode-signed by INFERSTELLEN/UNTERSTELLEN.

        using var target = new LeoControlTarget(status, networks, client, loggers.CreateLogger<LeoControlTarget>(), wifi, hub);
        using var rpc = PipeTransport.CreateRpc(pipe);
        // Surface protocol-level failures (they otherwise vanish inside StreamJsonRpc).
        rpc.TraceSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
        rpc.TraceSource.Listeners.Add(new LoggerTraceListener(_log));
        rpc.AddLocalRpcTarget<ILeoControl>(target, new JsonRpcTargetOptions { NotifyClientOfEvents = true });
        rpc.StartListening();
        status.ClientConnected();
        try
        {
            await rpc.Completion.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Client session ended with error");
        }
        finally
        {
            status.ClientDisconnected();
            _log.LogInformation("Client disconnected: pid={Pid}", client.ProcessId);
        }
    }
}
