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
    IReadOnlyList<NetworkDnsInfo>? NetworkDns = null)
{
    public static SplitTunnelInfo Empty { get; } = new(true, SplitTunnelEngineState.Off, null, [], [], []);
}

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
