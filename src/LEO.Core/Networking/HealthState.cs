namespace Leo.Core.Networking;

/// <summary>Real internet health of one adapter, from LEO's own probes (LEO.md §5.7).</summary>
public enum HealthState
{
    /// <summary>Not probed yet (just connected, or probes still running).</summary>
    Unknown = 0,

    /// <summary>Probes succeed with normal latency.</summary>
    Up = 1,

    /// <summary>Probes succeed but slowly, or some recent probes failed.</summary>
    Degraded = 2,

    /// <summary>Redirected / wrong content: a captive portal (hotel, café sign-in page).</summary>
    Captive = 3,

    /// <summary>Connected locally but the internet probes fail.</summary>
    NoInternet = 4,

    /// <summary>Adapter has no link or no IPv4 address.</summary>
    Disconnected = 5,
}

public static class HealthStateExtensions
{
    /// <summary>States that can carry the PC's traffic.</summary>
    public static bool IsUsable(this HealthState s) => s is HealthState.Up or HealthState.Degraded;

    /// <summary>States that trigger failover once they persist.</summary>
    public static bool IsFailing(this HealthState s) => s is HealthState.NoInternet or HealthState.Captive;
}

/// <summary>Result of one probe round for one adapter.</summary>
public sealed record HealthReport(HealthState State, int? LatencyMs, DateTimeOffset CheckedAt, string? Detail = null);
