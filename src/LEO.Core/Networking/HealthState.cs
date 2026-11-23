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

    /// <summary>
    /// A connected VPN's firewall (kill switch) blocks traffic outside it (LEO.md §20.4). Not "failing": failover
    /// freezes instead of hopping between networks the VPN blocks anyway.
    /// </summary>
    BlockedByVpn = 6,
}

/// <summary>Why a probe failed, so a firewall block (WSAEACCES) can be told apart from an outage (LEO.md §20.4).</summary>
public enum ProbeError
{
    None = 0,

    /// <summary>The connect/send failed at once with "access denied" (10013): a firewall filter (WFP) blocked it.</summary>
    Denied = 1,

    Timeout = 2,
    Refused = 3,
    Other = 4,
}

public static class ProbeErrors
{
    /// <summary>Classifies a failed probe from its exception chain (pure): 10013 = Denied, 10061 = Refused, 10060/timeouts = Timeout.</summary>
    public static ProbeError Classify(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.AccessDenied => ProbeError.Denied,
                    System.Net.Sockets.SocketError.ConnectionRefused => ProbeError.Refused,
                    System.Net.Sockets.SocketError.TimedOut => ProbeError.Timeout,
                    _ => ProbeError.Other,
                };
            }
            if (e is TimeoutException or OperationCanceledException) return ProbeError.Timeout;
        }
        return ProbeError.Other;
    }
}

public static class HealthStateExtensions
{
    /// <summary>States that can carry the PC's traffic.</summary>
    public static bool IsUsable(this HealthState s) => s is HealthState.Up or HealthState.Degraded;

    /// <summary>States that trigger failover once they persist.</summary>
    public static bool IsFailing(this HealthState s) => s is HealthState.NoInternet or HealthState.Captive;
}

/// <summary>Result of one probe round for one adapter.</summary>
public sealed record HealthReport(HealthState State, int? LatencyMs, DateTimeOffset CheckedAt, string? Detail = null,
    ProbeError Error = ProbeError.None);
