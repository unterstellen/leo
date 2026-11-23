using Leo.Core.Networking;

namespace Leo.Ipc;

public sealed record TrafficTotals(long Received, long Sent)
{
    public static TrafficTotals Zero { get; } = new(0, 0);
}

/// <summary>Per-adapter throughput, totals and latency history (LEO.md §6.7, §19).</summary>
public sealed record AdapterStatsInfo(
    string AdapterId,
    string Name,
    AdapterKind Kind,
    string? NetworkName,
    bool IsUp,
    long ReceiveBps,                    // bytes per second, last sample
    long SendBps,
    TrafficTotals Session,              // since the adapter last connected (or LEO started)
    TrafficTotals Today,
    TrafficTotals Month,
    IReadOnlyList<long> ReceiveHistory, // bytes/s, oldest first, one per StatsSnapshot.SampleSeconds
    IReadOnlyList<long> SendHistory,
    IReadOnlyList<int?> LatencyHistory, // ms per internet check, null = check failed/no answer; oldest first
    int? LatencyAverageMs,
    double FailedChecksPercent);

/// <summary>Bytes one app used on one network (aggregated; no destinations).</summary>
public sealed record AppUsageInfo(
    string App,          // program file name, e.g. "chrome.exe"
    string AdapterId,
    string NetworkName,
    long ReceiveBps,
    long SendBps,
    TrafficTotals Today,
    TrafficTotals Month);

public sealed record StatsSnapshot(
    DateTimeOffset TakenAt,
    int SampleSeconds,
    IReadOnlyList<AdapterStatsInfo> Adapters,
    IReadOnlyList<AppUsageInfo> Apps,
    bool PerAppLive,         // the routing engine is running, so per-app numbers are being counted
    int AdapterRetentionDays,
    int AppRetentionDays)
{
    public static StatsSnapshot Empty { get; } = new(DateTimeOffset.MinValue, 2, [], [], false, 400, 90);
}

/// <summary>One live connection through the routing engine. Shown live only: never stored or logged.</summary>
public sealed record ConnectionInfo(
    string Id,
    string App,              // "chrome.exe" (or "Unknown app")
    string? AppPath,
    string Destination,      // host name if known, else the address
    int Port,
    string Protocol,         // "TCP" / "UDP"
    string? AdapterId,
    string Network,          // "Ethernet", "WiFi (fallback)", "Blocked"
    string Rule,             // "youtube.com and its subdomains", "App: Steam", "No rule (Primary)"
    long Upload,
    long Download,
    DateTimeOffset Started);

public sealed record ConnectionsSnapshot(bool EngineRunning, string? Note, IReadOnlyList<ConnectionInfo> Connections)
{
    public static ConnectionsSnapshot Empty { get; } = new(false, null, []);
}
