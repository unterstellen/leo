using Leo.Core.SplitTunnel;

namespace Leo.Ipc;

public enum SplitTunnelEngineState
{
    /// <summary>No enabled rules (or switched off): LEO only manages metrics, nothing is tunneled.</summary>
    Off = 0,
    Starting = 1,
    Running = 2,
    /// <summary>The engine failed or kept crashing; LEO stopped it and networking is normal.</summary>
    Error = 3,
    /// <summary>The engine binary isn't installed.</summary>
    NotInstalled = 4,
    /// <summary>A VPN only allows its own tunnel and blocks LEO's adapter: LEO stopped the engine until it disconnects (M6).</summary>
    StoodAside = 5,
}

/// <summary>An app rule plus where its traffic goes right now.</summary>
public sealed record AppRuleInfo(
    string Id,
    string DisplayName,
    AppMatchKind Kind,
    string Pattern,
    string TargetAdapterId,
    OnTargetDown OnTargetDown,
    bool IncludeChildren,
    bool Enabled,
    string RouteNow,        // e.g. "Ethernet", "WiFi (fallback: Ethernet is down)", "Blocked (Ethernet is down)", "Off"
    bool TargetUsable,
    int ChildPrograms);     // child programs currently routed with this rule

public sealed record SplitTunnelInfo(
    bool Enabled,
    SplitTunnelEngineState State,
    string? Message,
    IReadOnlyList<AppRuleInfo> Rules,
    IReadOnlyList<DestinationRuleInfo>? Destinations = null,
    IReadOnlyList<NetworkDnsInfo>? NetworkDns = null,
    string? UnruledVia = null,      // M6: the VPN unruled traffic follows right now (display name), null = Primary
    string? VpnBanner = null,       // M6: "<VPN> blocks traffic outside it…" / "Split tunneling paused by <VPN>…"
    IReadOnlyList<BondInfo>? Bonds = null, // M7: bond groups (machine-wide), in the user's order
    string? UnruledBondId = null,   // M7: "Everything else uses" this bond (setting), null = Primary
    string? UnruledBondNow = null)  // M7: the bond unruled traffic really uses right now (display name), null = not a bond
{
    public static SplitTunnelInfo Empty { get; } = new(true, SplitTunnelEngineState.Off, null, [], [], []);
}

/// <summary>One network of a bond (M7) and its state right now.</summary>
public sealed record BondMemberInfo(
    string AdapterId,
    string Name,
    int Weight,
    bool Present,       // the adapter exists (a tether may be unplugged)
    bool Connected,
    bool Usable,        // connected and has internet per LEO's health checks
    bool Metered);      // Windows' metered flag (shown only; LEO never limits it)

/// <summary>A bond group (M7, LEO.md §22) and where its traffic goes right now.</summary>
public sealed record BondInfo(
    string Id,
    string Name,
    BondMode Mode,
    OnTargetDown OnAllDown,
    IReadOnlyList<BondMemberInfo> Members,
    int UsableCount,
    int RuleCount,      // enabled app/destination rules that use it
    bool InUse,         // written into the engine (a rule or "Everything else uses" refers to it, engine running)
    string RouteNow,    // "WiFi + Ethernet", "Only WiFi (Ethernet is unavailable)", "Primary (no network in it is available)"
    IReadOnlyList<string> Hints);

/// <summary>Live per-member numbers of one bond (M7), counted from the engine's open connections. Nothing about destinations.</summary>
public sealed record BondLiveMember(string AdapterId, int Connections, long Download, long Upload, bool? EngineAlive);

public sealed record BondLive(string BondId, IReadOnlyList<BondLiveMember> Members);

public sealed record BondsLiveInfo(bool EngineRunning, IReadOnlyList<BondLive> Bonds);

/// <summary>A destination rule (M4) plus where its traffic goes right now.</summary>
public sealed record DestinationRuleInfo(
    string Id,
    string DisplayName,
    DestinationKind Kind,
    string Value,
    string Ports,
    DestinationProtocol Protocol,
    string? AppRuleId,
    string? AppName,        // the combo's app, if any
    string TargetAdapterId,
    OnTargetDown OnTargetDown,
    bool Enabled,
    string Description,     // "youtube.com and its subdomains · port 443"
    string RouteNow,
    bool TargetUsable,
    int Rank);              // 1-based position in the match order (0 = not active)

/// <summary>DNS of one network: the user's custom servers (empty = automatic) and what the network itself offers.</summary>
public sealed record NetworkDnsInfo(
    string AdapterId,
    string Name,
    bool IsUp,
    IReadOnlyList<string> CustomServers,
    IReadOnlyList<string> AutomaticServers,
    IReadOnlyList<string> EffectiveServers);

/// <summary>"Test a rule": which rule a destination would match and which network it would use, and why.</summary>
public sealed record RouteTestResult(
    string Host,
    int Port,
    bool PortGuessed,
    IReadOnlyList<string> Addresses,
    string? DestinationRuleId,
    string? AppRuleId,
    string RuleText,        // "Rule: youtube.com and its subdomains" / "No rule"
    string NetworkName,     // where it goes right now
    string Explanation);
