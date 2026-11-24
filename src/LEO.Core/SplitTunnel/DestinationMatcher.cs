using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Leo.Core.SplitTunnel;

/// <summary>What the "test a rule" box asks about.</summary>
public sealed record RouteQuery(string? Host, IReadOnlyList<IPAddress> Addresses, int Port, DestinationProtocol Protocol, string? AppPath = null);

/// <summary>The rule that decides a query: a destination rule, an app rule, or neither (Primary).</summary>
public sealed record RouteDecision(DestinationRule? Destination, AppRule? App);

/// <summary>
/// Destination rules (LEO.md §6.5): normalising/validating user input, matching, precedence, and the engine's
/// rule syntax. Pure, so the service's "test a rule" and the generated engine config agree by construction.
///
/// <para><b>Precedence</b> (decided for M4, LEO.md §2): App+Destination &gt; Destination &gt; App &gt; Primary.
/// Among destination rules: exact host &gt; domain (longer first) &gt; keyword &gt; IP range (narrower first) &gt;
/// any host; a rule with a port/protocol filter beats the same rule without one; then the user's order.</para>
/// </summary>
public static partial class DestinationMatcher
{
    // ── Input ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Guesses the rule kind from what the user typed: an IP/CIDR → IpRange, otherwise a domain (with subdomains).
    /// Accepts pasted URLs ("https://www.example.com/page" → "www.example.com") and "*.example.com".
    /// </summary>
    public static (DestinationKind Kind, string Value) Detect(string input)
    {
        var s = input.Trim();
        if (TryParseRange(s, out _, out _)) return (DestinationKind.IpRange, s);
        return (DestinationKind.DomainSuffix, s);
    }

    /// <summary>Validates and normalises a rule in place. Returns a user-facing problem, or null when it's fine.</summary>
    public static string? Normalize(DestinationRule rule)
    {
        rule.Value = (rule.Value ?? "").Trim();
        rule.Ports = (rule.Ports ?? "").Trim();
        rule.DisplayName = (rule.DisplayName ?? "").Trim();

        switch (rule.Kind)
        {
            case DestinationKind.DomainSuffix:
            case DestinationKind.DomainExact:
            {
                var host = HostFromInput(rule.Value);
                if (host.StartsWith("*.", StringComparison.Ordinal)) { host = host[2..]; rule.Kind = DestinationKind.DomainSuffix; }
                host = host.TrimStart('.').TrimEnd('.');
                if (host.Length == 0) return "Enter a domain, like example.com.";
                if (TryParseRange(host, out _, out _)) { rule.Kind = DestinationKind.IpRange; rule.Value = host; return Normalize(rule); }
                var ascii = ToAscii(host);
                if (ascii is null || !DomainRegex().IsMatch(ascii)) return $"'{rule.Value}' isn't a valid domain.";
                if (!ascii.Contains('.') && rule.Kind == DestinationKind.DomainSuffix && ascii.Length < 2) return "That domain is too short.";
                rule.Value = ascii;
                break;
            }
            case DestinationKind.DomainKeyword:
            {
                var k = rule.Value.ToLowerInvariant();
                if (k.Length < 2) return "Enter at least 2 characters for a keyword.";
                if (!KeywordRegex().IsMatch(k)) return "A keyword can only use letters, digits, dots, dashes and underscores.";
                rule.Value = k;
                break;
            }
            case DestinationKind.IpRange:
            {
                if (!TryParseRange(rule.Value, out var network, out var prefix))
                    return $"'{rule.Value}' isn't an IP address or range (e.g. 203.0.113.7 or 203.0.113.0/24).";
                if (network.AddressFamily == AddressFamily.InterNetworkV6)
                    return "LEO doesn't route IPv6 yet. Use an IPv4 address or range, or a domain.";
                if (EnginePlan.ExcludedRanges.Any(ex => TryParseRange(ex, out var exNet, out var exPrefix) && prefix >= exPrefix && InRange(network, exNet, exPrefix)))
                    return "Local and private addresses never go through LEO (your router, printers and LAN keep working directly).";
                rule.Value = $"{network}/{prefix}";
                break;
            }
            case DestinationKind.AnyHost:
                rule.Value = "";
                if (rule.Ports.Length == 0) return "A rule for any destination needs ports (e.g. 3478-3481).";
                break;
            default:
                return "Unknown rule type.";
        }

        if (rule.Ports.Length > 0)
        {
            if (!TryParsePorts(rule.Ports, out var ranges)) return "Ports look like 443, 80, 443 or 27000-27100 (1 to 65535).";
            rule.Ports = string.Join(", ", ranges.Select(r => r.Lo == r.Hi ? r.Lo.ToString(CultureInfo.InvariantCulture) : $"{r.Lo}-{r.Hi}"));
        }
        if (!Enum.IsDefined(rule.Protocol)) return "Unknown protocol.";
        return null;
    }

    /// <summary>Host name from a pasted URL or "host:port"; lower-cased. Returns the input when it isn't URL-like.</summary>
    public static string HostFromInput(string input)
    {
        var s = input.Trim();
        if (s.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(s, UriKind.Absolute, out var uri)) return uri.IdnHost.ToLowerInvariant();
        var slash = s.IndexOf('/');
        if (slash >= 0 && !TryParseRange(s, out _, out _)) s = s[..slash];
        var colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon && int.TryParse(s[(colon + 1)..], out _)) s = s[..colon];
        return s.ToLowerInvariant();
    }

    /// <summary>Parses "host", "host:port", "https://host/path", "1.2.3.4", "[2001:db8::1]:443". Port defaults from the scheme, else 443.</summary>
    public static bool TryParseTarget(string input, out string host, out int port, out bool portGuessed)
    {
        host = "";
        port = 443;
        portGuessed = true;
        var s = input.Trim();
        if (s.Length == 0) return false;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "leo://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return false;
        host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.IdnHost.ToLowerInvariant();
        if (!uri.IsDefaultPort && uri.Port > 0) { port = uri.Port; portGuessed = false; }
        else if (uri.Scheme == "http") { port = 80; portGuessed = false; }
        else if (uri.Scheme == "https") { port = 443; portGuessed = false; }
        return true;
    }

    // ── Matching ────────────────────────────────────────────────────────────

    /// <summary>Enabled, valid rules in match order (see class remarks). Combos whose app rule is missing/off are left out.</summary>
    public static IReadOnlyList<DestinationRule> Ordered(IReadOnlyList<DestinationRule> rules, IReadOnlyList<AppRule> apps)
    {
        var activeApps = apps.Where(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Pattern)).Select(a => a.Id).ToHashSet();
        return rules
            .Select((r, i) => (Rule: r, Index: i))
            .Where(x => x.Rule.Enabled && !string.IsNullOrWhiteSpace(x.Rule.TargetAdapterId) && IsWellFormed(x.Rule)
                        && (x.Rule.AppRuleId is null || activeApps.Contains(x.Rule.AppRuleId)))
            .OrderBy(x => x.Rule.AppRuleId is null ? 1 : 0)
            .ThenBy(x => Tier(x.Rule.Kind))
            .ThenByDescending(x => Specificity(x.Rule))
            .ThenBy(x => x.Rule.Ports.Length > 0 || x.Rule.Protocol != DestinationProtocol.Any ? 0 : 1)
            .ThenBy(x => x.Index)
            .Select(x => x.Rule)
            .ToList();
    }

    /// <summary>Which rule decides this query: the first matching destination rule, else the best app rule, else none (Primary).</summary>
    public static RouteDecision Decide(IReadOnlyList<DestinationRule> rules, IReadOnlyList<AppRule> apps, RouteQuery q)
    {
        var appById = apps.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var r in Ordered(rules, apps))
        {
            if (r.AppRuleId is not null && (q.AppPath is null || !AppRuleMatcher.Matches(appById[r.AppRuleId], q.AppPath))) continue;
            if (Matches(r, q)) return new RouteDecision(r, null);
        }
        var app = q.AppPath is null ? null
            : AppRuleMatcher.FindBest(apps.Where(a => !string.IsNullOrWhiteSpace(a.TargetAdapterId)), q.AppPath);
        return new RouteDecision(null, app);
    }

    /// <summary>Destination part only (the app part of a combo is checked by <see cref="Decide"/>).</summary>
    public static bool Matches(DestinationRule r, RouteQuery q)
    {
        if (r.Protocol != DestinationProtocol.Any && r.Protocol != q.Protocol) return false;
        if (r.Ports.Length > 0 && (!TryParsePorts(r.Ports, out var ranges) || !ranges.Any(p => q.Port >= p.Lo && q.Port <= p.Hi))) return false;

        var host = q.Host?.TrimEnd('.').ToLowerInvariant();
        return r.Kind switch
        {
            DestinationKind.DomainExact => host is not null && host == r.Value,
            DestinationKind.DomainSuffix => host is not null && (host == r.Value || host.EndsWith("." + r.Value, StringComparison.Ordinal)),
            DestinationKind.DomainKeyword => host is not null && host.Contains(r.Value, StringComparison.Ordinal),
            DestinationKind.IpRange => TryParseRange(r.Value, out var net, out var prefix) && q.Addresses.Any(a => InRange(a, net, prefix)),
            DestinationKind.AnyHost => true,
            _ => false,
        };
    }

    /// <summary>Short human description: "youtube.com and its subdomains · port 443 · TCP".</summary>
    public static string Describe(DestinationRule r)
    {
        var what = r.Kind switch
        {
            DestinationKind.DomainSuffix => $"{r.Value} and its subdomains",
            DestinationKind.DomainExact => $"{r.Value} only",
            DestinationKind.DomainKeyword => $"addresses containing “{r.Value}”",
            DestinationKind.IpRange => r.Value.EndsWith("/32", StringComparison.Ordinal) ? r.Value[..^3] : r.Value,
            _ => "any destination",
        };
        if (r.Ports.Length > 0) what += (r.Ports.Contains(',') || r.Ports.Contains('-') ? " · ports " : " · port ") + r.Ports;
        if (r.Protocol != DestinationProtocol.Any) what += " · " + r.Protocol.ToString().ToUpperInvariant();
        return what;
    }

    // ── Engine syntax ───────────────────────────────────────────────────────

    /// <summary>
    /// The engine rule line (without quotes) sending matches to <paramref name="group"/>, e.g.
    /// <c>DOMAIN-SUFFIX,youtube.com,to-x-fb</c> or <c>AND,((IP-CIDR,203.0.113.0/24,no-resolve),(DST-PORT,443)),to-x-fb</c>.
    /// <paramref name="appRegex"/> (combos) is the app rule's path regex.
    /// </summary>
    public static string ToEngineRule(DestinationRule r, string group, string? appRegex = null)
    {
        var conds = new List<(string Type, string Value, bool NoResolve)>();
        if (appRegex is not null) conds.Add(("PROCESS-PATH-REGEX", LogicSafe(appRegex), false));
        switch (r.Kind)
        {
            case DestinationKind.DomainExact: conds.Add(("DOMAIN", r.Value, false)); break;
            case DestinationKind.DomainSuffix: conds.Add(("DOMAIN-SUFFIX", r.Value, false)); break;
            case DestinationKind.DomainKeyword: conds.Add(("DOMAIN-KEYWORD", r.Value, false)); break;
            case DestinationKind.IpRange:
                TryParseRange(r.Value, out var net, out _);
                conds.Add((net.AddressFamily == AddressFamily.InterNetworkV6 ? "IP-CIDR6" : "IP-CIDR", r.Value, true));
                break;
        }
        if (r.Ports.Length > 0 && TryParsePorts(r.Ports, out var ranges))
            conds.Add(("DST-PORT", string.Join("/", ranges.Select(p => p.Lo == p.Hi ? p.Lo.ToString(CultureInfo.InvariantCulture) : $"{p.Lo}-{p.Hi}")), false));
        if (r.Protocol != DestinationProtocol.Any) conds.Add(("NETWORK", r.Protocol == DestinationProtocol.Tcp ? "TCP" : "UDP", false));

        if (conds.Count == 1)
        {
            var c = conds[0];
            return $"{c.Type},{c.Value},{group}" + (c.NoResolve ? ",no-resolve" : "");
        }
        return "AND,(" + string.Join(",", conds.Select(c => $"({c.Type},{c.Value}{(c.NoResolve ? ",no-resolve" : "")})")) + ")," + group;
    }

    /// <summary>The engine's logic-rule parser pairs raw parentheses, so literal ones in a regex become \x28/\x29.</summary>
    private static string LogicSafe(string regex) =>
        regex.Replace(@"\(", @"\x28", StringComparison.Ordinal).Replace(@"\)", @"\x29", StringComparison.Ordinal);

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsWellFormed(DestinationRule r)
    {
        var copy = new DestinationRule { Kind = r.Kind, Value = r.Value, Ports = r.Ports, Protocol = r.Protocol };
        return Normalize(copy) is null && copy.Value == r.Value && copy.Ports == r.Ports && copy.Kind == r.Kind;
    }

    private static int Tier(DestinationKind k) => k switch
    {
        DestinationKind.DomainExact => 0,
        DestinationKind.DomainSuffix => 1,
        DestinationKind.DomainKeyword => 2,
        DestinationKind.IpRange => 3,
        _ => 4,
    };

    private static int Specificity(DestinationRule r) => r.Kind switch
    {
        DestinationKind.DomainSuffix => r.Value.Count(c => c == '.') * 1000 + r.Value.Length,
        DestinationKind.DomainKeyword => r.Value.Length,
        DestinationKind.IpRange => TryParseRange(r.Value, out _, out var p) ? p : 0,
        _ => 0,
    };

    public static bool TryParsePorts(string text, out List<(int Lo, int Hi)> ranges)
    {
        ranges = [];
        foreach (var part in text.Split([',', '/', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535) return false;
                ranges.Add((p, p));
            }
            else
            {
                if (!int.TryParse(part[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var lo)
                    || !int.TryParse(part[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var hi)
                    || lo is < 1 or > 65535 || hi is < 1 or > 65535 || lo > hi) return false;
                ranges.Add((lo, hi));
            }
        }
        ranges = ranges.Distinct().OrderBy(r => r.Lo).ThenBy(r => r.Hi).ToList();
        return ranges.Count is > 0 and <= 16;
    }

    /// <summary>Parses "a.b.c.d", "a.b.c.d/n", IPv6 likewise; host bits are cleared ("10.1.2.3/8" → 10.0.0.0/8).</summary>
    public static bool TryParseRange(string text, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var s = text.Trim();
        var slash = s.IndexOf('/');
        var addrText = slash >= 0 ? s[..slash] : s;
        if (!IPAddress.TryParse(addrText, out var addr) || addrText.Count(c => c == '.') is not (0 or 3)) return false;
        if (addr.AddressFamily == AddressFamily.InterNetwork && !addrText.Contains('.')) return false; // "12345" isn't an IP to users
        var max = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (slash >= 0)
        {
            if (!int.TryParse(s[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > max) return false;
        }
        else prefix = max;
        if (addr.IsIPv4MappedToIPv6) return false;
        var bytes = addr.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
        network = new IPAddress(bytes);
        return true;
    }

    public static bool InRange(IPAddress address, IPAddress network, int prefix)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != network.AddressFamily) return false;
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        for (var i = 0; i < a.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            var mask = (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }

    private static string? ToAscii(string host)
    {
        try { return new IdnMapping().GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?\.)*[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$")]
    private static partial Regex DomainRegex();

    [GeneratedRegex(@"^[a-z0-9._-]{2,100}$")]
    private static partial Regex KeywordRegex();
}
