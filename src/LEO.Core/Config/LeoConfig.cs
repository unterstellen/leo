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

    /// <summary>Health probes and automatic failover (LEO.md §5.7).</summary>
    public HealthSettings Health { get; set; } = new();

    /// <summary>Split tunneling: which app uses which network (LEO.md §6.4, M3).</summary>
    public Leo.Core.SplitTunnel.SplitTunnelSettings SplitTunnel { get; set; } = new();
}

public sealed class AdapterSettings
{
    public string Id { get; set; } = "";
    public string? Mac { get; set; }
    public string? Nickname { get; set; }

    /// <summary>Wi-Fi only: SSIDs this adapter should stay on, most preferred first (LEO.md §5.5).</summary>
    public List<string> PinnedSsids { get; set; } = [];

    /// <summary>
    /// Wi-Fi primary with a secondary station (DBS cards): desired "dual link" (Windows synchronized dual-station
    /// mode). null = leave Windows' setting alone. LEO re-applies it if Windows resets it.
    /// </summary>
    public bool? DualLink { get; set; }
}

public sealed class NetworkSettings
{
    public string Key { get; set; } = "";
    public string? Nickname { get; set; }
}

public sealed class HealthSettings
{
    public bool AutoFailover { get; set; } = true;
    public int ProbeIntervalSeconds { get; set; } = 5;
    public int FailoverDelaySeconds { get; set; } = 6;
    public int FailbackDelaySeconds { get; set; } = 30;

    /// <summary>Above this round-trip, a working network is reported as Degraded ("slow").</summary>
    public int SlowLatencyMs { get; set; } = 1000;

    /// <summary>Probe targets in order; the first that answers decides. Expected = body text, or "204".</summary>
    public List<ProbeTarget> ProbeTargets { get; set; } =
    [
        new() { Url = "http://www.msftconnecttest.com/connecttest.txt", Expect = "Microsoft Connect Test" },
        new() { Url = "http://cp.cloudflare.com/generate_204", Expect = "204" },
    ];
}

public static class HealthSettingsValidator
{
    /// <summary>Returns a user-facing problem, or null when the settings are acceptable.</summary>
    public static string? Validate(HealthSettings s)
    {
        if (s.ProbeIntervalSeconds is < 2 or > 300) return "Check interval must be 2–300 seconds.";
        if (s.FailoverDelaySeconds is < 1 or > 300) return "Failover delay must be 1–300 seconds.";
        if (s.FailbackDelaySeconds is < 5 or > 3600) return "Switch-back delay must be 5–3600 seconds.";
        if (s.SlowLatencyMs is < 100 or > 10000) return "Slow threshold must be 100–10000 ms.";
        if (s.ProbeTargets.Count is < 1 or > 4) return "Use 1 to 4 probe addresses.";
        foreach (var t in s.ProbeTargets)
        {
            if (!Uri.TryCreate(t.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return $"'{t.Url}' isn't an http(s) address.";
            if (string.IsNullOrWhiteSpace(t.Expect)) return $"Say what '{t.Url}' should answer (\"204\" or the exact text).";
        }
        return null;
    }
}

public sealed class ProbeTarget
{
    public string Url { get; set; } = "";

    /// <summary>"204" = expect HTTP 204 No Content; anything else = expect HTTP 200 with exactly this body.</summary>
    public string Expect { get; set; } = "204";
}
