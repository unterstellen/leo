using Leo.Core;
using Leo.Ipc;
using StreamJsonRpc;

namespace Leo.App.Services;

public sealed record RpcError(string Message, int? Code);

public enum ConnectionState
{
    Connecting,
    Connected,
    ServiceUnavailable,
    Incompatible,
}

/// <summary>
/// Keeps a live JSON-RPC connection to LEO.Service, reconnecting automatically.
/// Events are raised on the thread pool; view models marshal to the UI thread.
/// </summary>
public sealed class ServiceConnection : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _serverWarningLogged;

    public ConnectionState State { get; private set; } = ConnectionState.Connecting;
    public EngineStatus? Status { get; private set; }
    public NetworkSnapshot Networks { get; private set; } = NetworkSnapshot.Empty;
    public ClientAccess Access { get; private set; } = ClientAccess.ReadOnly;
    public ILeoControl? Control { get; private set; }

    /// <summary>Connection state or engine status changed.</summary>
    public event EventHandler? Changed;

    /// <summary>A new network snapshot arrived.</summary>
    public event EventHandler? NetworksChanged;

    public void Start() => _loop ??= Task.Run(() =>
    {
        PipeTransport.Warmup();
        return RunAsync(_cts.Token);
    });

    /// <summary>A service-side notification (failover, new adapter…) to show to the user.</summary>
    public event EventHandler<LeoNotification>? Notified;

    public SplitTunnelInfo SplitTunnel { get; private set; } = SplitTunnelInfo.Empty;

    /// <summary>Split-tunnel state or app rules changed.</summary>
    public event EventHandler? SplitTunnelChanged;

    private void SetSplitTunnel(SplitTunnelInfo info)
    {
        SplitTunnel = info;
        SplitTunnelChanged?.Invoke(this, EventArgs.Empty);
    }

    public ProfilesInfo Profiles { get; private set; } = ProfilesInfo.Empty;

    /// <summary>Profiles, the active profile or its reason changed.</summary>
    public event EventHandler? ProfilesChanged;

    private void SetProfiles(ProfilesInfo info)
    {
        Profiles = info;
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs a command against the service. Returns null on success, or a user-facing error message.
    /// </summary>
    public async Task<string?> TryInvokeAsync(Func<ILeoControl, Task> call) => (await InvokeAsync(call))?.Message;

    /// <summary>Like <see cref="TryInvokeAsync"/> but keeps the JSON-RPC error code (e.g. PasswordRequired).</summary>
    public async Task<RpcError?> InvokeAsync(Func<ILeoControl, Task> call)
    {
        var control = Control;
        if (control is null) return new RpcError("The LEO service isn't connected.", null);
        try
        {
            await call(control);
            return null;
        }
        catch (RemoteInvocationException ex)
        {
            return new RpcError(ex.Message, ex.ErrorCode);
        }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException)
        {
            return new RpcError("Lost connection to the LEO service.", null);
        }
    }

    /// <summary>Calls a query on the service; returns default (and the error) on failure.</summary>
    public async Task<(T? Result, RpcError? Error)> QueryAsync<T>(Func<ILeoControl, Task<T>> call)
    {
        T? result = default;
        var error = await InvokeAsync(async c => result = await call(c));
        return (result, error);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var stream = await PipeTransport.ConnectClientAsync(ct: ct);

                // Pipe squatting (LEO.md §24.6): only LEO's own service, in session 0, from the install folder.
                if (PipeServerCheck.Problem(PipeServerCheck.Identify(stream), LeoPaths.InstallDir) is { } problem)
                {
                    if (PipeServerCheck.ShouldEnforce(Environment.ProcessPath, LeoPaths.InstallDir))
                    {
                        if (State != ConnectionState.ServiceUnavailable) UiLog.Write($"Not using the control pipe: {problem}");
                        throw new UnauthorizedAccessException(problem);
                    }
                    if (!_serverWarningLogged) UiLog.Write($"Development build, pipe server not checked: {problem}");
                    _serverWarningLogged = true;
                }

                using var rpc = PipeTransport.CreateRpc(stream);
                var control = rpc.Attach<ILeoControl>();
                rpc.StartListening();

                var hello = await control.HelloAsync(
                    new HelloRequest(LeoInfo.ProtocolVersion, "LEO.App", LeoInfo.Version), ct);
                Access = hello.Access;
                if (!hello.Compatible)
                {
                    SetState(ConnectionState.Incompatible, null, null);
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                control.StatusChanged += (_, s) => SetState(ConnectionState.Connected, s, control);
                control.NetworksChanged += (_, n) => SetNetworks(n);
                control.NotificationRaised += (_, n) => Notified?.Invoke(this, n);
                control.SplitTunnelChanged += (_, s) => SetSplitTunnel(s);
                control.ProfilesChanged += (_, p) => SetProfiles(p);

                SetState(ConnectionState.Connected, await control.GetStatusAsync(ct), control);
                SetNetworks(await control.GetNetworksAsync(ct));
                SetSplitTunnel(await control.GetSplitTunnelAsync(ct));
                SetProfiles(await control.GetProfilesAsync(ct));

                await rpc.Completion.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Service not installed/running, pipe broken, or access denied: retry.
                // Log only when the state changes, so a stopped service doesn't flood the log.
                if (State != ConnectionState.ServiceUnavailable) UiLog.Write("Service connection failed", ex);
            }

            Access = ClientAccess.ReadOnly;
            SetState(ConnectionState.ServiceUnavailable, null, null);
            SetNetworks(NetworkSnapshot.Empty);
            SetSplitTunnel(SplitTunnelInfo.Empty);
            SetProfiles(ProfilesInfo.Empty);
            try { await Task.Delay(RetryDelay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void SetState(ConnectionState state, EngineStatus? status, ILeoControl? control)
    {
        State = state;
        Status = status;
        Control = control;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetNetworks(NetworkSnapshot snapshot)
    {
        Networks = snapshot;
        NetworksChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* shutting down */ }
        }
        _cts.Dispose();
    }
}
