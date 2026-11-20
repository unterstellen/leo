using Leo.Core.Networking;

namespace Leo.Ipc;

public enum AdapterStatus
{
    Disconnected = 0,
    Up = 1,
    Connecting = 2,
}

/// <summary>Mirrors Windows' NetworkConnectivityLevel (NLA). LEO's own probes are in <see cref="AdapterInfo.Health"/>.</summary>
public enum Connectivity
{
    None = 0,
    LocalAccess = 1,
    ConstrainedInternet = 2, // captive portal / limited
    Internet = 3,
}

/// <summary>What an adapter is connected to.</summary>
public sealed record NetworkInfo(
    string Key,             // stable identity: "wifi:<SSID>", "gw:<gateway MAC>" or "nla:<profile>"
    string Name,            // SSID or Windows network profile name
    string? Nickname,
    Connectivity Connectivity,
    bool IsMetered);

public sealed record AdapterInfo(
    string Id,              // interface GUID, e.g. {7D0119DE-…}
    string Name,            // Windows alias, e.g. "WiFi 2"
    string Description,     // driver description
    AdapterKind Kind,
    string? Nickname,
    string? Mac,
    AdapterStatus Status,
    long LinkSpeedBps,
    IReadOnlyList<string> IPv4,
    IReadOnlyList<string> IPv6,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    NetworkInfo? Network,
    int? Metric,            // current IPv4 interface metric
    bool AutomaticMetric,
    int? PriorityIndex,     // position in the user's priority list (0 = preferred Primary)
    bool IsPrimary,
    bool IsPrimaryCandidate,
    HealthState Health,     // LEO's own internet probe (M2)
    int? LatencyMs,
    string? HealthDetail,
    bool IsDemoted,         // failover: temporarily moved behind healthy networks
    int? FailbackInSeconds, // when a demoted adapter will be trusted again (if it stays healthy)
    IReadOnlyList<string> PinnedSsids,
    bool DualLinkSupported = false,   // Wi-Fi primary with a secondary station (DBS): can run a dual link
    bool DualLinkEnabled = false,     // dual link on (for both the primary and its secondary)
    string? FollowsAdapterId = null)  // secondary station: the primary Wi-Fi adapter it follows
{
    /// <summary>"Network nickname · Adapter nickname" style display name (LEO.md §3).</summary>
    public string DisplayName => Nickname ?? Name;
}

public sealed record NetworkSnapshot(
    IReadOnlyList<AdapterInfo> Adapters,
    string? PrimaryAdapterId,
    bool ManageMetrics,
    DateTimeOffset TakenAt,
    bool AutoFailover = true,
    bool FailoverActive = false) // the Primary isn't the user's top choice because of failover
{
    public static NetworkSnapshot Empty { get; } = new([], null, true, DateTimeOffset.MinValue);
}

public enum NotificationKind
{
    Failover = 0,
    Failback = 1,
    PrimaryChanged = 2,
    NewAdapter = 3,
    CaptivePortal = 4,
    WifiReconnected = 5,
    NoInternetAnywhere = 6,
}

/// <summary>Something the user should hear about; the tray app shows it as a Windows notification.</summary>
public sealed record LeoNotification(NotificationKind Kind, string Title, string Message, string? AdapterId, DateTimeOffset At);

/// <summary>A Wi-Fi network visible to (or pinned on) one Wi-Fi adapter.</summary>
public sealed record WifiNetworkInfo(
    string Ssid,
    int SignalPercent,      // 0 when not currently visible
    bool IsVisible,
    bool IsSecured,
    string Security,        // "Open", "WPA2-Personal", "WPA3-Personal", "Enterprise", …
    bool CanJoinWithPassword,
    bool HasProfile,        // saved on this adapter
    bool KnownElsewhere,    // saved on another Wi-Fi adapter (LEO copies it over when connecting)
    bool IsConnected,
    bool IsPinned);

/// <summary>JSON-RPC error codes returned by LEO.Service.</summary>
public static class LeoErrorCodes
{
    public const int AccessDenied = -32001;
    public const int NotFound = -32002;
    public const int InvalidOperation = -32003;
    public const int PasswordRequired = -32004;
}
