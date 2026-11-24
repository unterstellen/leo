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
    Leo.Service.Wifi.WifiManager? wifi = null, NotificationHub? hub = null,
    Leo.Service.SplitTunnel.SplitTunnelManager? splitTunnel = null,
    Leo.Service.Profiles.ProfileManager? profiles = null, Leo.Service.Stats.StatsCollector? stats = null,
    Leo.Service.Updates.UpdateService? updates = null) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger<ControlPipeServer>();

    /// <summary>Pipe name; overridable so tests can run a private instance.</summary>
    public string PipeName { get; init; } = LeoInfo.ControlPipeName;

    /// <summary>The pipe-client signature check (LEO.md §24.6); tests inject their own.</summary>
    public Leo.Service.Security.ClientTrust Trust { get; init; } = Leo.Service.Security.ClientTrust.ForThisService();

    /// <summary>Our pipe instances alive right now (listening + sessions). With none, the next one must be the first.</summary>
    private int _liveInstances;
    private bool _squatterLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(@"Control pipe listening on \\.\pipe\{Pipe}", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                var first = Volatile.Read(ref _liveInstances) == 0;
                try
                {
                    pipe = PipeTransport.CreateServerStream(PipeName, firstInstance: first);
                }
                catch (Exception ex) when (first && ex is UnauthorizedAccessException or IOException)
                {
                    // Another program created \\.\pipe\LEO.Control before us: never share the name with it (LEO.md §24.6).
                    if (!_squatterLogged) _log.LogError("Another program already owns the control pipe name; LEO's UI can't connect until it's gone ({Message})", ex.Message);
                    _squatterLogged = true;
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }
                Interlocked.Increment(ref _liveInstances);
                _squatterLogged = false;
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
                if (pipe is not null)
                {
                    await pipe.DisposeAsync();
                    Interlocked.Decrement(ref _liveInstances);
                }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using var _ = pipe;
        try
        {
            await ServeCoreAsync(pipe, stoppingToken);
        }
        finally
        {
            Interlocked.Decrement(ref _liveInstances);
        }
    }

    private async Task ServeCoreAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        // Signed builds: a client that isn't LEO's own signed LEO.exe from the install folder is read-only (LEO.md §24.6).
        var (client, trust) = Trust.Apply(ClientIdentity.FromPipe(pipe));
        _log.LogInformation("Client connected: pid={Pid} user={User} image={Image} access={Access} ({Trust})",
            client.ProcessId, client.UserName, client.ImagePath, client.Access, Leo.Core.Security.ClientTrustPolicy.Describe(trust));

        using var target = new LeoControlTarget(status, networks, client, loggers.CreateLogger<LeoControlTarget>(), wifi, hub, splitTunnel, profiles, stats, updates);
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
