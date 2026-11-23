using System.Net;
using System.Net.Sockets;

namespace Leo.Core.Networking;

/// <summary>One row of Windows' route table, as the VPN analysis needs it. <see cref="Metric"/> = route + interface metric.</summary>
public sealed record RouteEntry(string AdapterId, IPAddress Prefix, int PrefixLength, int Metric)
{
    public static RouteEntry Parse(string adapterId, string cidr, int metric = 0)
    {
        var slash = cidr.IndexOf('/');
        return new RouteEntry(adapterId, IPAddress.Parse(cidr[..slash]), int.Parse(cidr[(slash + 1)..]), metric);
    }

    public override string ToString() => $"{Prefix}/{PrefixLength}";
}

/// <summary>What one VPN does with the route table (LEO.md §20.3). Counts only, never the prefixes themselves (privacy).</summary>
public sealed record VpnRouteAnalysis(
    string AdapterId,
    bool TakesDefault4,
    bool TakesDefault6,
    int PublicRanges4,     // public IPv4 prefixes the VPN routes itself
    int PublicRanges6,
    int DefaultMetric)     // lowest metric among its catch-all routes (int.MaxValue when it takes none)
{
    public bool TakesDefault => TakesDefault4 || TakesDefault6;

    /// <summary>No catch-all and no public prefixes: Tailscale without an exit node, corporate VPNs for 10.x only.</summary>
    public bool PrivateOnly => !TakesDefault && PublicRanges4 == 0 && PublicRanges6 == 0;
}

/// <summary>
/// Pure route-table analysis for VPN coexistence (LEO.md §20.3): which VPN takes the default route, how much public space
/// it routes itself, which interface wins longest-prefix for an address, and whether LEO's own engine routes are present.
/// </summary>
public static class VpnRoutes
{
    /// <summary>Space that is never "public": the ranges LEO excludes from its tunnel, plus 0/8 and 240/4.</summary>
    private static readonly (uint Start, uint End)[] NonPublic4 = Merge(
        Leo.Core.SplitTunnel.EnginePlan.ExcludedRanges.Concat(["0.0.0.0/8", "240.0.0.0/4"]).Select(ToInterval4));

    private static readonly double PublicSpace4 = Math.Pow(2, 32) - NonPublic4.Sum(i => (double)i.End - i.Start + 1);

    /// <summary>Per VPN adapter: takes default (0/0; 0/1 + 128/1; or ≥ half the public IPv4 space), public prefix counts.</summary>
    public static IReadOnlyDictionary<string, VpnRouteAnalysis> Analyze(IEnumerable<RouteEntry> routes, IEnumerable<string> vpnIds)
    {
        var ids = vpnIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, VpnRouteAnalysis>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in routes.Where(r => ids.Contains(r.AdapterId)).GroupBy(r => r.AdapterId, StringComparer.OrdinalIgnoreCase))
        {
            var v4 = g.Where(r => r.Prefix.AddressFamily == AddressFamily.InterNetwork).ToList();
            var v6 = g.Where(r => r.Prefix.AddressFamily == AddressFamily.InterNetworkV6).ToList();

            var public4 = v4.Where(r => r.PrefixLength > 0 && IsPublic4(r)).ToList();
            var coverage = PublicCoverage4(public4);
            var default4 = v4.Any(r => r.PrefixLength == 0) || coverage >= PublicSpace4 / 2;

            var default6 = v6.Any(r => r.PrefixLength == 0)
                           || (v6.Any(r => r.PrefixLength == 1 && r.Prefix.Equals(IPAddress.IPv6Any))
                               && v6.Any(r => r.PrefixLength == 1 && r.Prefix.Equals(IPAddress.Parse("8000::"))));
            var public6 = v6.Count(r => r.PrefixLength is > 0 and < 128 && IsGlobal6(r.Prefix));

            var catchAll = g.Where(r => r.PrefixLength <= 1 || (r.Prefix.AddressFamily == AddressFamily.InterNetwork && default4 && IsPublic4(r)));
            var metric = default4 || default6 ? catchAll.Select(r => r.Metric).DefaultIfEmpty(int.MaxValue).Min() : int.MaxValue;
            result[g.Key] = new VpnRouteAnalysis(g.Key, default4, default6,
                default4 ? 0 : public4.Count, default6 ? 0 : public6, metric);
        }
        foreach (var id in ids.Where(id => !result.ContainsKey(id)))
            result[id] = new VpnRouteAnalysis(id, false, false, 0, 0, int.MaxValue);
        return result;
    }

    /// <summary>The VPN unruled traffic would follow: takes the IPv4 default route, not skipped; lowest metric wins (as Windows would).</summary>
    public static string? DefaultWinner(IEnumerable<VpnRouteAnalysis> analyses, IReadOnlySet<string>? skipped = null) =>
        analyses.Where(a => a.TakesDefault4 && skipped?.Contains(a.AdapterId) != true)
            .OrderBy(a => a.DefaultMetric).ThenBy(a => a.AdapterId, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.AdapterId).FirstOrDefault();

    /// <summary>The route Windows would pick for an address: longest prefix, then lowest metric.</summary>
    public static RouteEntry? BestRoute(IEnumerable<RouteEntry> routes, IPAddress address) =>
        routes.Where(r => r.Prefix.AddressFamily == address.AddressFamily && Contains(r, address))
            .OrderByDescending(r => r.PrefixLength).ThenBy(r => r.Metric).FirstOrDefault();

    /// <summary>
    /// The prefixes a TUN engine installs when it routes "everything except <paramref name="excluded"/>": the complement
    /// as CIDR blocks (mihomo's auto-route with route-exclude-address does this; LEO.md §20 dev-PC findings).
    /// </summary>
    public static IReadOnlyList<(IPAddress Prefix, int Length)> Complement4(IEnumerable<string> excluded)
    {
        var ex = Merge(excluded.Select(ToInterval4));
        var list = new List<(IPAddress, int)>();
        void Walk(uint start, int length)
        {
            var size = length == 0 ? (1UL << 32) : 1UL << (32 - length);
            var end = (uint)(start + size - 1);
            var overlap = ex.Where(i => i.Start <= end && i.End >= start).ToList();
            if (overlap.Count == 0) { list.Add((ToAddress(start), length)); return; }
            if (overlap.Any(i => i.Start <= start && i.End >= end)) return; // fully excluded
            Walk(start, length + 1);
            Walk((uint)(start + size / 2), length + 1);
        }
        Walk(0, 0);
        return list;
    }

    /// <summary>
    /// LEO's engine routes are in place: its adapter carries public IPv4 prefixes (the complement chunks). False means
    /// another program removed them (LEO.md §20.3 "routes tampered").
    /// </summary>
    public static bool EngineRoutesPresent(IEnumerable<RouteEntry> routes, string leoAdapterId) =>
        routes.Any(r => r.AdapterId.Equals(leoAdapterId, StringComparison.OrdinalIgnoreCase)
                        && r.Prefix.AddressFamily == AddressFamily.InterNetwork && r.PrefixLength is > 0 and <= 16 && IsPublic4(r));

    // ── math ─────────────────────────────────────────────────────────────────

    public static bool IsPublic4(RouteEntry r)
    {
        var (s, e) = ToInterval4(r.Prefix, r.PrefixLength);
        // Public when at least part of it is outside the non-public space.
        return Subtract([(s, e)], NonPublic4).Any();
    }

    private static bool IsGlobal6(IPAddress a) => (a.GetAddressBytes()[0] & 0xE0) == 0x20 || a.Equals(IPAddress.IPv6Any);

    private static double PublicCoverage4(IEnumerable<RouteEntry> routes)
    {
        var merged = Merge(routes.Select(r => ToInterval4(r.Prefix, r.PrefixLength)));
        return Subtract(merged, NonPublic4).Sum(i => (double)i.End - i.Start + 1);
    }

    private static bool Contains(RouteEntry r, IPAddress a)
    {
        var p = r.Prefix.GetAddressBytes();
        var b = a.GetAddressBytes();
        var bits = r.PrefixLength;
        for (var i = 0; i < p.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((p[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    private static (uint Start, uint End) ToInterval4(string cidr)
    {
        var r = RouteEntry.Parse("", cidr);
        return ToInterval4(r.Prefix, r.PrefixLength);
    }

    private static (uint Start, uint End) ToInterval4(IPAddress prefix, int length)
    {
        var b = prefix.GetAddressBytes();
        var value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        var mask = length == 0 ? 0u : uint.MaxValue << (32 - length);
        var start = value & mask;
        return (start, start | ~mask);
    }

    private static IPAddress ToAddress(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    private static (uint Start, uint End)[] Merge(IEnumerable<(uint Start, uint End)> intervals)
    {
        var list = new List<(uint Start, uint End)>();
        foreach (var i in intervals.OrderBy(i => i.Start))
        {
            if (list.Count > 0 && (ulong)i.Start <= (ulong)list[^1].End + 1)
                list[^1] = (list[^1].Start, Math.Max(list[^1].End, i.End));
            else list.Add(i);
        }
        return [.. list];
    }

    private static IEnumerable<(uint Start, uint End)> Subtract(IEnumerable<(uint Start, uint End)> from, (uint Start, uint End)[] remove)
    {
        foreach (var (s0, e) in from)
        {
            ulong s = s0;
            foreach (var r in remove)
            {
                if (r.End < s || r.Start > e) continue;
                if (r.Start > s) yield return ((uint)s, r.Start - 1);
                s = (ulong)r.End + 1;
                if (s > e) break;
            }
            if (s <= e) yield return ((uint)s, e);
        }
    }
}
