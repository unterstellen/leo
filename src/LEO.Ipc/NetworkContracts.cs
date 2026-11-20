using Leo.Core.Networking;

namespace Leo.Ipc;

public enum AdapterStatus
{
    Disconnected = 0,
    Up = 1,
    Connecting = 2,
}

/// <summary>Mirrors Windows' NetworkConnectivityLevel (NLA). Real per-interface probes arrive in M2.</summary>
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
    int? PriorityIndex,     // position in LEO's priority list (0 = preferred Primary)
    bool IsPrimary,
    bool IsPrimaryCandidate)
{
    /// <summary>"Network nickname · Adapter nickname" style display name (LEO.md §3).</summary>
    public string DisplayName => Nickname ?? Name;
}

public sealed record NetworkSnapshot(
    IReadOnlyList<AdapterInfo> Adapters,
    string? PrimaryAdapterId,
    bool ManageMetrics,
    DateTimeOffset TakenAt)
{
    public static NetworkSnapshot Empty { get; } = new([], null, true, DateTimeOffset.MinValue);
}

/// <summary>JSON-RPC error codes returned by LEO.Service.</summary>
public static class LeoErrorCodes
{
    public const int AccessDenied = -32001;
    public const int NotFound = -32002;
    public const int InvalidOperation = -32003;
}
