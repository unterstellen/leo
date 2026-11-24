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
    string? FollowsAdapterId = null,  // secondary station: the primary Wi-Fi adapter it follows
    bool IsRouteTarget = false,       // rules may send traffic here: physical networks + connected VPNs (M6)
    VpnInfo? Vpn = null)              // VPN adapters only (M6, LEO.md §20)
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
    ProfileSwitched = 7,
    VpnFirewallDetected = 8,   // a VPN's kill switch blocks traffic outside it (M6)
    SplitTunnelStoodAside = 9, // LEO paused split tunneling because a VPN only allows its own tunnel
    SplitTunnelResumed = 10,
    RoutesTampered = 11,       // another program keeps removing LEO's routes
    BondMemberLost = 12,       // a network in a bond in use stopped working (M7)
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

/// <summary>What a VPN's firewall (kill switch) does to traffic outside it, as LEO detected it (LEO.md §20.4).</summary>
public enum VpnFirewall
{
    None = 0,
    BlocksBypass = 1,  // rules aimed at physical networks can't connect while the VPN is on
    BlocksLeo = 2,     // the VPN blocks LEO's own adapter: split tunneling stands aside until it disconnects
}

/// <summary>A VPN adapter as LEO sees it (M6, LEO.md §20). Counts only: no prefixes, destinations or apps.</summary>
public sealed record VpnInfo(
    string Key,               // VpnIdentity key, e.g. "Tailscale Tunnel"; rules and per-VPN settings use it
    bool Connected,           // Up with a usable (non-169.254) IPv4 address
    bool Internet,            // the probe bound to the VPN reached the internet ("private network only" when false)
    bool TakesDefault4,       // the VPN routes the IPv4 default route itself
    bool TakesDefault6,
    int PublicRanges,         // public address ranges it routes itself (when it doesn't take the default route)
    int RouteMetric,          // its catch-all metric (two VPNs taking default: the lower one wins)
    string? OwnTrafficVia,    // display name of the network carrying the VPN's own transport (app rule), else null
    bool OwnTrafficRuled,     // true: an app rule moves its transport; false: it chooses its own network (follows Primary)
    VpnFirewall Firewall,
    string? FirewallOwner,    // product that owns the blocking firewall (read-only WFP provider names), best effort
    int DnsServers,           // IPv4 DNS servers the VPN set
    int NrptRules,            // Windows per-domain DNS rules that point at this VPN's DNS
    bool SkipUnruled,         // the user's "Let unruled traffic skip this VPN"
    bool UnruledFollows)      // unruled traffic follows this VPN right now
{
    public bool PrivateOnly => !TakesDefault4 && !TakesDefault6 && PublicRanges == 0;
}

/// <summary>An installed VPN client LEO knows (Add app → VPN clients).</summary>
public sealed record VpnClientInfo(string Name, string ExePath);
