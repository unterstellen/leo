using System.Security.Principal;
using Leo.Core;
using Leo.Ipc;

namespace Leo.Service;

/// <summary>Owns the service's status snapshot and notifies subscribers when it changes.</summary>
public sealed class EngineStatusProvider
{
    private readonly Lock _gate = new();
    private EngineStatus _current;

    public EngineStatusProvider()
    {
        using var id = WindowsIdentity.GetCurrent();
        _current = new EngineStatus(
            EngineState.Starting,
            LeoInfo.Version,
            DateTimeOffset.Now,
            IsElevated: id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator),
            RunningAs: id.Name,
            ConnectedClients: 0);
    }

    public event EventHandler<EngineStatus>? Changed;

    public EngineStatus Current
    {
        get { lock (_gate) return _current; }
    }

    public void SetState(EngineState state) => Update(s => s with { State = state });

    public void ClientConnected() => Update(s => s with { ConnectedClients = s.ConnectedClients + 1 });

    public void ClientDisconnected() => Update(s => s with { ConnectedClients = Math.Max(0, s.ConnectedClients - 1) });

    /// <summary>Re-publishes the current snapshot (heartbeat).</summary>
    public void Pulse() => Changed?.Invoke(this, Current);

    private void Update(Func<EngineStatus, EngineStatus> mutate)
    {
        EngineStatus next;
        lock (_gate)
        {
            next = _current = mutate(_current);
        }
        Changed?.Invoke(this, next);
    }
}
