using Leo.Ipc;

namespace Leo.Service;

/// <summary>
/// Placeholder engine lifecycle for M0: marks the engine Running and emits a status heartbeat.
/// Real subsystems (inventory, health, primary controller, TUN engine) plug in here from M1 on.
/// </summary>
public sealed class EngineHeartbeat(EngineStatusProvider status, ILogger<EngineHeartbeat> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var s = status.Current;
        log.LogInformation("LEO engine starting (v{Version}) as {Account}, elevated={Elevated}",
            s.ServiceVersion, s.RunningAs, s.IsElevated);
        status.SetState(EngineState.Running);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                status.Pulse();
            }
        }
        catch (OperationCanceledException) { }

        status.SetState(EngineState.Stopping);
        log.LogInformation("LEO engine stopping");
    }
}
