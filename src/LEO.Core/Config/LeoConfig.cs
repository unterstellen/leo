namespace Leo.Core.Config;

/// <summary>Machine configuration persisted at %ProgramData%\UNTERSTELLEN\LEO\config.json (LEO.md §8).</summary>
public sealed class LeoConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Per-adapter settings, keyed by interface GUID (with MAC as a fallback match).</summary>
    public List<AdapterSettings> Adapters { get; set; } = [];

    /// <summary>Per-network settings, keyed by <see cref="NetworkSettings.Key"/> (e.g. "wifi:HomeNet").</summary>
    public List<NetworkSettings> Networks { get; set; } = [];

    /// <summary>User's failover priority: adapter ids, most preferred first. Index 0 = Primary.</summary>
    public List<string> PriorityOrder { get; set; } = [];

    /// <summary>When true, LEO sets interface metrics to enforce <see cref="PriorityOrder"/>.</summary>
    public bool ManageMetrics { get; set; } = true;
}

public sealed class AdapterSettings
{
    public string Id { get; set; } = "";
    public string? Mac { get; set; }
    public string? Nickname { get; set; }
}

public sealed class NetworkSettings
{
    public string Key { get; set; } = "";
    public string? Nickname { get; set; }
}
