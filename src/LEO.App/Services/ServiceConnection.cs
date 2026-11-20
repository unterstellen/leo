using Leo.Core;
using Leo.Ipc;

namespace Leo.App.Services;

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

    public ConnectionState State { get; private set; } = ConnectionState.Connecting;
    public EngineStatus? Status { get; private set; }
    public ClientAccess Access { get; private set; } = ClientAccess.ReadOnly;
    public ILeoControl? Control { get; private set; }

    public event EventHandler? Changed;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var stream = await PipeTransport.ConnectClientAsync(ct: ct);
                using var rpc = PipeTransport.CreateRpc(stream);
                var control = rpc.Attach<ILeoControl>();
                rpc.StartListening();

                var hello = await control.HelloAsync(
                    new HelloRequest(LeoInfo.ProtocolVersion, "LEO.App", LeoInfo.Version), ct);
                if (!hello.Compatible)
                {
                    Publish(ConnectionState.Incompatible, null, hello.Access, null);
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                control.StatusChanged += (_, s) => Publish(ConnectionState.Connected, s, hello.Access, control);
                Publish(ConnectionState.Connected, await control.GetStatusAsync(ct), hello.Access, control);

                await rpc.Completion.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Service not installed/running, pipe broken, or access denied: retry.
            }

            Publish(ConnectionState.ServiceUnavailable, null, ClientAccess.ReadOnly, null);
            try { await Task.Delay(RetryDelay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void Publish(ConnectionState state, EngineStatus? status, ClientAccess access, ILeoControl? control)
    {
        State = state;
        Status = status;
        Access = access;
        Control = control;
        Changed?.Invoke(this, EventArgs.Empty);
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
