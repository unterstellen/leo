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
    IReadOnlyList<AppRuleInfo> Rules)
{
    public static SplitTunnelInfo Empty { get; } = new(true, SplitTunnelEngineState.Off, null, []);
}
