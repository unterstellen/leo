namespace Leo.Core.SplitTunnel;

/// <summary>How an app rule recognises programs (LEO.md §6.4).</summary>
public enum AppMatchKind
{
    /// <summary>One executable, by full path (case-insensitive).</summary>
    ExactPath = 0,

    /// <summary>Every executable inside a folder and its subfolders (also used for Store app install folders).</summary>
    Folder = 1,

    /// <summary>File-name pattern with * and ? wildcards, e.g. "*chrome*.exe".</summary>
    NamePattern = 2,
}

/// <summary>What happens to an app's traffic when its chosen network is down (LEO.md §3).</summary>
public enum OnTargetDown
{
    FallbackPrimary = 0,
    Block = 1,
}

/// <summary>"This app uses that network." Persisted in config.json.</summary>
public sealed class AppRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Friendly name shown in the UI (e.g. "Steam").</summary>
    public string DisplayName { get; set; } = "";

    public AppMatchKind Kind { get; set; } = AppMatchKind.ExactPath;

    /// <summary>Full exe path, folder path, or file-name pattern — depending on <see cref="Kind"/>.</summary>
    public string Pattern { get; set; } = "";

    /// <summary>Adapter id (interface GUID) the app should use.</summary>
    public string TargetAdapterId { get; set; } = "";

    public OnTargetDown OnTargetDown { get; set; } = OnTargetDown.FallbackPrimary;

    /// <summary>Also route programs this app starts (launchers → games, updaters…). Default on (decided 2026-11-22).</summary>
    public bool IncludeChildren { get; set; } = true;

    public bool Enabled { get; set; } = true;
}

/// <summary>Split-tunneling settings in config.json.</summary>
public sealed class SplitTunnelSettings
{
    /// <summary>Master switch. The engine also only runs while at least one rule is enabled.</summary>
    public bool Enabled { get; set; } = true;

    public List<AppRule> AppRules { get; set; } = [];

    /// <summary>Destination rules (M4), in the user's order (the tie-breaker within one specificity).</summary>
    public List<DestinationRule> DestinationRules { get; set; } = [];

    /// <summary>Custom DNS per network (M4). Networks not listed use their own DNS servers.</summary>
    public List<NetworkDnsSetting> NetworkDns { get; set; } = [];
}
