using System.IO.Pipes;
using Leo.Core;
using Leo.Ipc;
using StreamJsonRpc;

namespace Leo.Service;

/// <summary>Accepts UI connections on \\.\pipe\LEO.Control and serves <see cref="ILeoControl"/> over JSON-RPC.</summary>
public sealed class ControlPipeServer(EngineStatusProvider status, ILoggerFactory loggers) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger<ControlPipeServer>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(@"Control pipe listening on \\.\pipe\{Pipe}", LeoInfo.ControlPipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = PipeTransport.CreateServerStream(LeoInfo.ControlPipeName);
                await pipe.WaitForConnectionAsync(stoppingToken);
                var session = pipe;
                pipe = null; // ownership moves to the session task
                _ = Task.Run(() => ServeAsync(session, stoppingToken), CancellationToken.None);
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

        using var target = new LeoControlTarget(status, client, loggers.CreateLogger<LeoControlTarget>());
        using var rpc = PipeTransport.CreateRpc(pipe);
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
