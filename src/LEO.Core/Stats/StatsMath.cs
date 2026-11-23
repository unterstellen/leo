using System.Net;
using Leo.Core.SplitTunnel;

namespace Leo.Core.Stats;

/// <summary>Retention for stats.db (LEO.md §19): only aggregated daily byte totals are stored.</summary>
public static class StatsRetention
{
    /// <summary>Per-adapter/network daily totals: 13 months, so "this month" and last year's same month stay visible.</summary>
    public const int AdapterDays = 400;

    /// <summary>Per-app per-network daily totals: about three months.</summary>
    public const int AppDays = 90;

    public static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Rows with a day before this are deleted.</summary>
    public static string Cutoff(DateOnly today, int keepDays) => Day(today.AddDays(-(keepDays - 1)));

    public static string MonthStart(DateOnly today) => Day(new DateOnly(today.Year, today.Month, 1));
}

/// <summary>Turns cumulative byte counters into deltas.</summary>
public static class CounterDelta
{
    /// <summary>A counter that went down was reset (adapter re-plugged, driver restart): count it from zero.</summary>
    public static long Compute(long previous, long current) => current >= previous ? current - previous : Math.Max(0, current);
}

/// <summary>Fixed-size history, oldest first.</summary>
public sealed class Ring<T>(int capacity)
{
    private readonly Queue<T> _items = new(capacity);

    public int Capacity { get; } = capacity;

    public void Add(T item)
    {
        if (_items.Count == Capacity) _items.Dequeue();
        _items.Enqueue(item);
    }

    public IReadOnlyList<T> ToList() => [.. _items];

    public void Clear() => _items.Clear();
}

/// <summary>Per-connection byte deltas between two polls of the engine's connection list.</summary>
public sealed class ConnectionDeltaTracker
{
    private Dictionary<string, (long Up, long Down)> _last = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the bytes each connection moved since the previous call. New connections count fully (their first
    /// bytes happened since the last poll, or the tracker just started — then <paramref name="baselineOnly"/>).
    /// Connections that closed in between lose at most one poll interval of bytes.
    /// </summary>
    public IReadOnlyList<(EngineConnection Connection, long Up, long Down)> Update(IReadOnlyList<EngineConnection> now, bool baselineOnly = false)
    {
        var result = new List<(EngineConnection, long, long)>();
        var next = new Dictionary<string, (long, long)>(now.Count, StringComparer.Ordinal);
        foreach (var c in now)
        {
            next[c.Id] = (c.Upload, c.Download);
            if (baselineOnly) continue;
            var (up, down) = _last.TryGetValue(c.Id, out var prev)
                ? (CounterDelta.Compute(prev.Up, c.Upload), CounterDelta.Compute(prev.Down, c.Download))
                : (c.Upload, c.Download);
            if (up > 0 || down > 0) result.Add((c, up, down));
        }
        _last = next;
        return result;
    }

    public void Reset() => _last = new(StringComparer.Ordinal);
}

/// <summary>One connection as the engine reports it (mihomo /connections). Lives in memory only.</summary>
public sealed record EngineConnection(
    string Id,
    string Network,              // "tcp" / "udp"
    string? Host,                // from DNS mapping
    string? SniffHost,           // from TLS SNI / HTTP Host / QUIC
    string DestinationIp,
    int DestinationPort,
    string? ProcessPath,
    long Upload,
    long Download,
    DateTimeOffset Start,
    IReadOnlyList<string> Chains, // [0] = outbound actually used, last = first group matched
    string Rule,                  // engine rule type, e.g. "DomainSuffix", "RuleSet", "Match"
    string RulePayload);

/// <summary>An engine outbound and the adapter behind it.</summary>
public sealed record OutboundAdapter(string Outbound, string AdapterId, string Name);

/// <summary>What the connections view shows for one connection (LEO.md §6.7).</summary>
public sealed record ConnectionView(string App, string Destination, string? AdapterId, string Network, string Rule);

/// <summary>Maps an engine connection to LEO's words: app, destination, network, and the LEO rule that matched.</summary>
public static class ConnectionDescriber
{
    public static string AppName(string? processPath) =>
        string.IsNullOrWhiteSpace(processPath) ? "Unknown app" : Path.GetFileName(processPath.Replace('/', '\\'));

    public static string Destination(EngineConnection c) =>
        !string.IsNullOrWhiteSpace(c.Host) ? c.Host! : !string.IsNullOrWhiteSpace(c.SniffHost) ? c.SniffHost! : c.DestinationIp;

    /// <summary>Adapter carrying the connection, or null when blocked/unknown; plus its label.</summary>
    public static (string? AdapterId, string Network) Network(EngineConnection c, IReadOnlyList<OutboundAdapter> outbounds)
    {
        if (c.Chains.Count == 0) return (null, "Unknown");
        var leaf = c.Chains[0];
        if (leaf.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase)) return (null, "Blocked");
        var o = outbounds.FirstOrDefault(x => x.Outbound == leaf);
        if (o is null) return (null, leaf);
        // A rule's group "to-<x>-fb" that ended on another network's outbound "net-<y>" = fallback.
        var group = c.Chains[^1];
        var fallback = c.Chains.Count > 1 && group.StartsWith("to-", StringComparison.Ordinal)
                       && !group.StartsWith("to-" + leaf["net-".Length..] + "-", StringComparison.Ordinal);
        return (o.AdapterId, fallback ? o.Name + " (fallback)" : o.Name);
    }

    /// <summary>The LEO rule that decided the connection, using the same precedence function as the engine config.</summary>
    public static string Rule(EngineConnection c, IReadOnlyList<AppRule> apps, IReadOnlyList<DestinationRule> dests,
        IReadOnlyList<ChildProgram> children)
    {
        if (c.Rule.Equals("Match", StringComparison.OrdinalIgnoreCase))
            return c.Chains.Count > 0 && c.Chains[0].StartsWith("vpn-", StringComparison.Ordinal) ? "No rule (follows the VPN)" : "No rule (Primary)";
        var addresses = IPAddress.TryParse(c.DestinationIp, out var ip) ? new[] { ip } : [];
        var host = !string.IsNullOrWhiteSpace(c.Host) ? c.Host : c.SniffHost;
        var protocol = c.Network.Equals("udp", StringComparison.OrdinalIgnoreCase) ? DestinationProtocol.Udp : DestinationProtocol.Tcp;
        var decision = DestinationMatcher.Decide(dests, apps, new RouteQuery(host, addresses, c.DestinationPort, protocol, c.ProcessPath));
        if (decision.Destination is { } d)
        {
            var app = d.AppRuleId is null ? null : apps.FirstOrDefault(a => a.Id == d.AppRuleId);
            return DestinationMatcher.Describe(d) + (app is null ? "" : $" from {app.DisplayName}");
        }
        if (decision.App is { } a) return "App: " + a.DisplayName;
        if (c.ProcessPath is { } path && children.FirstOrDefault(ch => ch.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } child
            && apps.FirstOrDefault(r => r.Id == child.ParentRuleId) is { } parent)
            return $"App: {parent.DisplayName} (started by it)";
        return c.Rule; // the engine's own rule name, if LEO's rules changed a moment ago
    }
}
