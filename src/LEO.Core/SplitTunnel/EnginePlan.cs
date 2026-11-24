using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Leo.Core.Networking;

namespace Leo.Core.SplitTunnel;

/// <summary>
/// An adapter as the engine planner sees it. <paramref name="IsCandidate"/> = physical (Primary candidate).
/// VPNs (M6, LEO.md §20.2): <paramref name="IsVpn"/>, <paramref name="IsUp"/> = connected (usable address);
/// <paramref name="TakesDefault"/> = the VPN routes the default route itself; <paramref name="SkipUnruled"/> = the user's
/// "Let unruled traffic skip this VPN"; <paramref name="RouteMetric"/> = its catch-all metric (two VPNs: lower wins).
/// </summary>
public sealed record EngineAdapter(string Id, string Alias, bool IsCandidate, bool IsUp, bool IsDemoted, HealthState Health,
    IReadOnlyList<string>? DnsServers = null, bool IsVpn = false, bool TakesDefault = false, bool SkipUnruled = false,
    int RouteMetric = int.MaxValue);

/// <summary>An NRPT rule (Windows' per-domain DNS, e.g. a VPN's split DNS) owned by a connected VPN (LEO.md §20.3).</summary>
public sealed record NrptNamespace(string Namespace, string VpnId, IReadOnlyList<string> Servers);

/// <summary>
/// The engine's DNS (LEO.md §5.4, M4): every lookup goes out through a specific network, to that network's DNS.
/// <see cref="Nameservers"/> = the Primary's; <see cref="Policy"/> = domains of destination rules → their target's.
/// </summary>
public sealed record EngineDns(
    IReadOnlyList<string> DefaultNameservers,
    IReadOnlyList<string> Nameservers,
    IReadOnlyList<(string Domain, IReadOnlyList<string> Servers)> Policy);

/// <summary>A program discovered as a child of a ruled app (only for rules with IncludeChildren).</summary>
public sealed record ChildProgram(string ExePath, string ParentRuleId);

/// <summary>
/// How the engine checks bond members itself (LEO.md §22.3): always LEO's own probe target, never the engine's default
/// (which would be a Google address).
/// </summary>
public sealed record BondHealthCheck(string Url, string ExpectedStatus)
{
    public static BondHealthCheck Default { get; } = new("http://cp.cloudflare.com/generate_204", "204");

    /// <summary>The first configured probe target that expects an HTTP status (e.g. "204"), else <see cref="Default"/>.</summary>
    public static BondHealthCheck From(IEnumerable<(string Url, string Expect)> probeTargets)
    {
        foreach (var (url, expect) in probeTargets)
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                && int.TryParse(expect, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 200 and < 400)
                return new BondHealthCheck(url, code.ToString(CultureInfo.InvariantCulture));
        return Default;
    }
}

/// <summary>A bond as written into the engine config: one load-balance / url-test group.</summary>
public sealed record PlannedBond(string BondId, string Name, BondMode Mode, IReadOnlyList<string> Members);

/// <summary>Live choice for one engine select-group (switched through the engine API, no reload).</summary>
public sealed record GroupSelection(string Group, string Choice);

/// <summary>
/// Everything LEO tells the engine (mihomo), derived purely from LEO's state (LEO.md §17).
/// <see cref="StructureKey"/> changes only when a reload is needed (adapters/targets/rules); selections and
/// child-program lists change live.
/// </summary>
public sealed class EnginePlan
{
    public const string PrimaryGroup = "LEO-PRIMARY";
    public const string Reject = "REJECT";

    /// <summary>Private/link-local/CGNAT/multicast ranges never enter the tunnel: LAN, router DNS, Tailscale keep working natively.</summary>
    public static readonly string[] ExcludedRanges =
    [
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10",
        "127.0.0.0/8", "224.0.0.0/4", "255.255.255.255/32",
    ];

    public required IReadOnlyList<(string Name, string Alias, string AdapterId)> Outbounds { get; init; }
    public required IReadOnlyList<(string Name, IReadOnlyList<string> Members)> Groups { get; init; }
    public required IReadOnlyList<GroupSelection> Selections { get; init; }

    /// <summary>Rule providers: name → (target group, regex lines). Order = match precedence.</summary>
    public required IReadOnlyList<(string Name, string Group, IReadOnlyList<string> Regexes)> Providers { get; init; }

    /// <summary>Destination rules (M4) as engine rule lines, in match order; they come before the app rule lists.</summary>
    public IReadOnlyList<string> DestinationRules { get; init; } = [];

    /// <summary>Engine DNS; null when there is no network to resolve through.</summary>
    public EngineDns? Dns { get; init; }

    /// <summary>TLS SNI / HTTP Host / QUIC sniffing, on while any domain rule is active.</summary>
    public bool Sniff { get; init; }

    /// <summary>Equal keys ⇒ the running engine only needs live selection/provider updates, not a reload.</summary>
    public required string StructureKey { get; init; }

    /// <summary>Loop guards (M6): one line per VPN client preset while any VPN outbound exists; matched before every other rule.</summary>
    public IReadOnlyList<string> GuardRules { get; init; } = [];

    /// <summary>The VPN unruled traffic follows right now (it takes the default route and isn't skipped), or null = Primary.</summary>
    public string? UnruledVpnId { get; init; }

    public const string UnruledGroup = "LEO-UNRULED";

    /// <summary>Bond groups (M7) written into the engine config: only bonds that a rule or "Everything else uses" refers to.</summary>
    public IReadOnlyList<PlannedBond> Bonds { get; init; } = [];

    /// <summary>The bond unruled traffic uses right now (M7), or null (a VPN or the Primary).</summary>
    public string? UnruledBondId { get; init; }

    /// <summary>Engine health check for bond groups.</summary>
    public BondHealthCheck BondCheck { get; init; } = BondHealthCheck.Default;

    public static string OutboundName(string adapterId, bool vpn = false) => (vpn ? "vpn-" : "net-") + Short(adapterId);
    public static string OutboundName(EngineAdapter a) => OutboundName(a.Id, a.IsVpn);

    /// <summary>A rule target's select group: "to-&lt;adapter&gt;-fb|-block", for bonds "to-b&lt;bond&gt;-fb|-block".</summary>
    public static string GroupName(string targetId, OnTargetDown mode) =>
        (BondTargets.IsBond(targetId) ? "to-b" + Short(BondTargets.BondId(targetId)) : "to-" + Short(targetId))
        + (mode == OnTargetDown.Block ? "-block" : "-fb");

    /// <summary>A bond's engine group (load-balance / url-test).</summary>
    public static string BondGroupName(string bondId) => "bond-" + Short(bondId);

    private static string Short(string adapterId) =>
        new string(adapterId.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Can carry a rule's traffic now. Physical: up, not demoted, not failing. VPN: connected — not only when its probe
    /// is Up, because a split VPN carries specific destinations without carrying "the internet" (LEO.md §20.2).
    /// </summary>
    public static bool IsUsable(EngineAdapter a) => a.IsVpn ? a.IsUp : a.IsUp && !a.IsDemoted && !a.Health.IsFailing();

    /// <summary>The VPN unruled traffic follows: takes default, not skipped by the user; lowest catch-all metric wins.</summary>
    public static EngineAdapter? UnruledVpn(IEnumerable<EngineAdapter> adapters) =>
        adapters.Where(a => a.IsVpn && a.IsUp && a.TakesDefault && !a.SkipUnruled)
            .OrderBy(a => a.RouteMetric).ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    public static EnginePlan Build(IReadOnlyList<EngineAdapter> adapters, string? primaryId,
        IReadOnlyList<AppRule> rules, IReadOnlyList<ChildProgram> children,
        IReadOnlyList<DestinationRule>? destinations = null, IReadOnlyList<NetworkDnsSetting>? networkDns = null,
        IReadOnlyList<NrptNamespace>? nrpt = null, IReadOnlyList<Bond>? bonds = null, string? unruledBondId = null,
        BondHealthCheck? bondCheck = null)
    {
        var dests = DestinationMatcher.Ordered(destinations ?? [], rules);
        var up = adapters.Where(a => (a.IsCandidate || a.IsVpn) && a.IsUp).OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var physicalUp = up.Where(a => !a.IsVpn).ToList();
        var vpnUp = up.Where(a => a.IsVpn).ToList();
        var outbounds = up.Select(a => (OutboundName(a), a.Alias, a.Id)).ToList();
        var byId = adapters.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var upById = up.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var active = rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern) && !string.IsNullOrWhiteSpace(r.TargetAdapterId)).ToList();

        // Default route follows LEO's Primary (failover included). Physical networks only: a VPN is never Primary.
        var groups = new List<(string, IReadOnlyList<string>)>();
        var selections = new List<GroupSelection>();
        var primaryMembers = physicalUp.Count > 0 ? physicalUp.Select(OutboundName).ToList() : ["DIRECT"];
        groups.Add((PrimaryGroup, primaryMembers));
        var primaryOut = primaryId is not null && physicalUp.Any(a => a.Id.Equals(primaryId, StringComparison.OrdinalIgnoreCase))
            ? OutboundName(primaryId) : primaryMembers[0];
        selections.Add(new GroupSelection(PrimaryGroup, primaryOut));

        // Bonds (M7, LEO.md §22): only bonds a rule or "Everything else uses" refers to get an engine group. Members are the
        // connected member networks (outbounds exist only for those); the engine's own health check skips dead ones, and
        // LEO's "to-b…" groups switch away live when LEO's health says no member is usable.
        var bondById = (bonds ?? []).GroupBy(b => b.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var referenced = active.Select(r => r.TargetAdapterId).Concat(dests.Select(d => d.TargetAdapterId))
            .Where(BondTargets.IsBond).Select(BondTargets.BondId)
            .Concat(unruledBondId is null ? [] : [unruledBondId])
            .Where(bondById.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var planned = new List<PlannedBond>();
        foreach (var id in referenced)
        {
            var b = bondById[id];
            var members = new List<string>();
            foreach (var m in b.Members)
            {
                if (!upById.TryGetValue(m.AdapterId, out var a) || a.IsVpn || !a.IsCandidate) continue;
                // Weights: Weighted (by site) and Every connection (round-robin over the repeated list). Found live: an
                // even split over a fast and a slow link waits on the slow one (LEO.md §23).
                var copies = b.Mode is BondMode.Weighted or BondMode.EveryConnection ? Math.Clamp(m.Weight, 1, BondRules.MaxWeight) : 1;
                for (var i = 0; i < copies; i++) members.Add(OutboundName(a));
            }
            if (members.Count > 0) planned.Add(new PlannedBond(b.Id, b.Name, b.Mode, members));
        }
        var plannedIds = planned.Select(p => p.BondId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool BondUsable(string bondId) => plannedIds.Contains(bondId) && bondById[bondId].Members
            .Any(m => upById.TryGetValue(m.AdapterId, out var a) && !a.IsVpn && a.IsCandidate && IsUsable(a));

        // Unruled traffic (M6): follows a VPN that takes the default route, else the chosen bond (M7), else the Primary.
        // Switched live.
        var unruledVpn = UnruledVpn(vpnUp);
        var unruledBond = unruledBondId is not null && plannedIds.Contains(unruledBondId) ? bondById[unruledBondId] : null;
        var unruledMembers = vpnUp.Select(OutboundName).ToList();
        if (unruledBond is not null) unruledMembers.Add(BondGroupName(unruledBond.Id));
        unruledMembers.Add(PrimaryGroup);
        groups.Add((UnruledGroup, unruledMembers));
        var unruledBondNow = unruledVpn is null && unruledBond is not null && BondUsable(unruledBond.Id) ? unruledBond : null;
        selections.Add(new GroupSelection(UnruledGroup, unruledVpn is not null ? OutboundName(unruledVpn)
            : unruledBondNow is not null ? BondGroupName(unruledBondNow.Id) : PrimaryGroup));

        // One select group per (target, fallback mode), switched live as health changes.
        foreach (var (target, mode) in active.Select(r => (r.TargetAdapterId, r.OnTargetDown))
                     .Concat(dests.Select(d => (d.TargetAdapterId, d.OnTargetDown))).Distinct()
                     .OrderBy(t => t.TargetAdapterId, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.OnTargetDown))
        {
            var name = GroupName(target, mode);
            var alternative = mode == OnTargetDown.Block ? Reject : PrimaryGroup;
            var members = new List<string>();
            if (BondTargets.IsBond(target))
            {
                var bondId = BondTargets.BondId(target);
                if (plannedIds.Contains(bondId)) members.Add(BondGroupName(bondId));
                members.Add(alternative);
                groups.Add((name, members));
                selections.Add(new GroupSelection(name, BondUsable(bondId) ? BondGroupName(bondId) : alternative));
                continue;
            }
            var targetUp = upById.TryGetValue(target, out var t);
            if (targetUp) members.Add(OutboundName(t!));
            members.Add(alternative);
            groups.Add((name, members));
            var usable = targetUp && IsUsable(t!);
            selections.Add(new GroupSelection(name, usable ? OutboundName(t!) : alternative));
        }

        // Loop guard (LEO.md §20.1): a VPN client's own transport never goes into a VPN — its own or another's — whatever
        // "unruled follows the VPN" or a destination rule says. It goes where the user's underlay rule says, else Primary.
        var guards = new List<string>();
        if (vpnUp.Count > 0)
        {
            foreach (var preset in VpnClients.Presets)
            {
                var underlay = VpnClients.UnderlayRule(preset, active);
                var target = underlay is not null && !(byId.TryGetValue(underlay.TargetAdapterId, out var u) && u.IsVpn)
                    ? GroupName(underlay.TargetAdapterId, underlay.OnTargetDown) : PrimaryGroup;
                guards.Add($"PROCESS-PATH-REGEX,{VpnClients.Regex(preset)},{target}");
            }
        }

        // Providers in precedence order: exact > children > folder (deeper first) > name pattern.
        var providers = new List<(string, string, IReadOnlyList<string>)>();
        void Add(string tier, IEnumerable<(string Group, string Regex)> items)
        {
            foreach (var g in items.GroupBy(i => i.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
                providers.Add(($"{tier}-{g.Key}", g.Key, g.Select(i => i.Regex).Distinct().ToList()));
        }

        Add("exact", active.Where(r => r.Kind == AppMatchKind.ExactPath).Select(r => (GroupName(r.TargetAdapterId, r.OnTargetDown), AppRuleMatcher.ToRegex(r))));
        var ruleById = active.ToDictionary(r => r.Id);
        Add("child", children.Where(c => ruleById.ContainsKey(c.ParentRuleId))
            .Select(c => (GroupName(ruleById[c.ParentRuleId].TargetAdapterId, ruleById[c.ParentRuleId].OnTargetDown), AppRuleMatcher.ExactPathRegex(c.ExePath))));
        Add("folder", active.Where(r => r.Kind == AppMatchKind.Folder)
            .OrderByDescending(r => AppRuleMatcher.NormalizeFolder(r.Pattern).Length)
            .Select(r => (GroupName(r.TargetAdapterId, r.OnTargetDown), AppRuleMatcher.ToRegex(r))));
        Add("name", active.Where(r => r.Kind == AppMatchKind.NamePattern).Select(r => (GroupName(r.TargetAdapterId, r.OnTargetDown), AppRuleMatcher.ToRegex(r))));

        // Destination rules: inline lines (combos carry their app's path regex), before every app rule list.
        var appById = rules.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var destLines = dests.Select(d => DestinationMatcher.ToEngineRule(d, GroupName(d.TargetAdapterId, d.OnTargetDown),
            d.AppRuleId is null ? null : AppRuleMatcher.ToRegex(appById[d.AppRuleId]))).ToList();
        var sniff = dests.Any(d => d.Kind is DestinationKind.DomainExact or DestinationKind.DomainSuffix or DestinationKind.DomainKeyword);

        var dnsPrimary = primaryId is not null && physicalUp.Any(a => a.Id.Equals(primaryId, StringComparison.OrdinalIgnoreCase))
            ? primaryId : physicalUp.FirstOrDefault()?.Id;
        // DNS for bond-targeted domains goes through one member (LEO.md §22.5): the Primary if it's a usable member, else
        // the first usable member in the bond's order.
        string? DnsTarget(string target)
        {
            if (!BondTargets.IsBond(target)) return target;
            if (!bondById.TryGetValue(BondTargets.BondId(target), out var b)) return null;
            var usable = b.Members.Select(m => upById.GetValueOrDefault(m.AdapterId))
                .Where(a => a is { IsVpn: false, IsCandidate: true } && IsUsable(a)).Select(a => a!).ToList();
            return usable.FirstOrDefault(a => a.Id.Equals(dnsPrimary, StringComparison.OrdinalIgnoreCase))?.Id ?? usable.FirstOrDefault()?.Id;
        }
        var dns = BuildDns(up, dnsPrimary, dests, networkDns ?? [], unruledVpn, nrpt ?? [], DnsTarget);

        // Reload when anything in config.yaml changes (outbounds, groups, guard/destination rules, DNS); app rule lists
        // and selections are updated live.
        var key = string.Join("|", outbounds.Select(o => $"{o.Item1}={o.Alias}")) + "#" +
                  string.Join("|", groups.Select(g => $"{g.Item1}:{string.Join(",", g.Item2)}")) + "#" +
                  string.Join("|", planned.Select(p => $"{p.BondId}:{p.Mode}:{string.Join(",", p.Members)}")) + "#" +
                  (bondCheck ?? BondHealthCheck.Default) + "#" +
                  string.Join("|", providers.Select(p => p.Item1)) + "#" +
                  string.Join("|", guards) + "#" +
                  string.Join("|", destLines) + "#" + sniff + "#" + DnsKey(dns);

        return new EnginePlan
        {
            Outbounds = outbounds,
            Groups = groups,
            Selections = selections,
            Providers = providers,
            GuardRules = guards,
            DestinationRules = destLines,
            Dns = dns,
            Sniff = sniff,
            UnruledVpnId = unruledVpn?.Id,
            Bonds = planned,
            UnruledBondId = unruledBondNow?.Id,
            BondCheck = bondCheck ?? BondHealthCheck.Default,
            StructureKey = key,
        };
    }

    /// <summary>A VPN's own IPv4 DNS servers (no public fallback: a VPN that sets no DNS changes nothing).</summary>
    public static IReadOnlyList<string> VpnDns(EngineAdapter a) =>
        (a.DnsServers ?? []).Where(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork).ToList();

    // ── DNS ──────────────────────────────────────────────────────────────────

    /// <summary>Public resolvers used only when a network reports no DNS servers of its own.</summary>
    private static readonly string[] FallbackDns = ["1.1.1.1", "8.8.8.8"];

    /// <summary>The servers a network resolves with: the user's custom list, else its own (DHCP) IPv4 servers.</summary>
    public static IReadOnlyList<string> EffectiveDns(EngineAdapter a, IReadOnlyList<NetworkDnsSetting> custom)
    {
        var set = custom.FirstOrDefault(c => c.AdapterId.Equals(a.Id, StringComparison.OrdinalIgnoreCase));
        if (set is { Servers.Count: > 0 }) return set.Servers;
        var own = (a.DnsServers ?? []).Where(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork).ToList();
        return own.Count > 0 ? own : FallbackDns;
    }

    private static EngineDns? BuildDns(IReadOnlyList<EngineAdapter> up, string? primaryId,
        IReadOnlyList<DestinationRule> dests, IReadOnlyList<NetworkDnsSetting> custom,
        EngineAdapter? unruledVpn, IReadOnlyList<NrptNamespace> nrpt, Func<string, string?>? dnsTarget = null)
    {
        if (primaryId is null) return null;
        var byId = up.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var primary = byId[primaryId];
        var primaryServers = EffectiveDns(primary, custom);

        // Default lookups go where unruled traffic goes: the VPN's own DNS through the VPN while unruled traffic follows
        // it (LEO.md §20.3), else the Primary's. A VPN that sets no DNS changes nothing.
        var defaultAdapter = primary;
        IReadOnlyList<string> defaultServers = primaryServers;
        if (unruledVpn is not null && VpnDns(unruledVpn) is { Count: > 0 } vpnServers)
        {
            defaultAdapter = unruledVpn;
            defaultServers = vpnServers;
        }
        var nameservers = defaultServers.Select(s => DnsServerSpec(s, OutboundName(defaultAdapter))).ToList();

        // Plain-IP servers resolve the host names of DoH/DoT servers.
        var bootstrap = primaryServers.Where(s => IPAddress.TryParse(s, out _)).ToList();
        if (bootstrap.Count == 0) bootstrap = [.. FallbackDns];

        // Domains sent elsewhere are looked up through that network, with its DNS. Targets that are down
        // aren't listed: their traffic falls back to the Primary (or is blocked), and so does their DNS.
        var policy = new List<(string, IReadOnlyList<string>)>();
        foreach (var d in dests.Where(d => d.Kind is DestinationKind.DomainSuffix or DestinationKind.DomainExact && d.AppRuleId is null))
        {
            var targetId = dnsTarget is null ? d.TargetAdapterId : dnsTarget(d.TargetAdapterId);
            if (targetId is null || !byId.TryGetValue(targetId, out var target) || target.Id.Equals(defaultAdapter.Id, StringComparison.OrdinalIgnoreCase)) continue;
            var servers = target.IsVpn ? VpnDns(target) : EffectiveDns(target, custom);
            if (servers.Count == 0) continue; // a VPN without DNS of its own: the default servers answer
            var pattern = d.Kind == DestinationKind.DomainSuffix ? "+." + d.Value : d.Value;
            if (policy.Any(p => p.Item1 == pattern)) continue; // first (highest-precedence) rule wins
            policy.Add((pattern, servers.Select(s => DnsServerSpec(s, OutboundName(target))).ToList()));
        }

        // NRPT (corporate split DNS, Tailscale's MagicDNS names): names a connected VPN owns resolve through that VPN.
        foreach (var n in nrpt)
        {
            if (!byId.TryGetValue(n.VpnId, out var vpn) || !vpn.IsVpn) continue;
            var ns = n.Namespace.Trim().Trim('.');
            if (ns.Length == 0 || IPAddress.TryParse(ns, out _) || ns.Contains(',') || ns.Contains('\'') || ns.Contains(' ')) continue;
            var servers = n.Servers.Where(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (servers.Count == 0) continue;
            var pattern = "+." + ns.ToLowerInvariant();
            if (policy.Any(p => p.Item1 == pattern)) continue;
            policy.Add((pattern, servers.Select(s => DnsServerSpec(s, OutboundName(vpn))).ToList()));
        }
        return new EngineDns(bootstrap.Select(s => DnsServerSpec(s, OutboundName(primary))).ToList(), nameservers, policy);
    }

    /// <summary>Engine nameserver entry bound to an outbound: "192.168.1.1#net-x", "https://dns.google/dns-query#net-x".</summary>
    public static string DnsServerSpec(string server, string outbound)
    {
        var s = server.Trim();
        if (IPAddress.TryParse(s, out var ip))
            s = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"udp://[{ip}]:53" : ip.ToString();
        var hash = s.IndexOf('#');
        if (hash >= 0) s = s[..hash];
        return s + "#" + outbound;
    }

    /// <summary>A user-facing problem with a custom DNS server, or null. Accepts IPs and udp/tcp/tls/https/quic URLs.</summary>
    public static string? ValidateDnsServer(string server)
    {
        var s = server.Trim();
        if (IPAddress.TryParse(s, out _)) return null;
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "udp" or "tcp" or "tls" or "https" or "quic"
            && uri.Host.Length > 0 && uri.Fragment.Length == 0 && !s.Contains(',') && !s.Contains('\\') && !s.Contains('\''))
            return null;
        return $"'{s}' isn't a DNS server. Use an IP address (1.1.1.1) or a URL (https://dns.google/dns-query, tls://1.1.1.1).";
    }

    private static string DnsKey(EngineDns? dns) => dns is null ? "nodns"
        : string.Join(",", dns.DefaultNameservers) + ";" + string.Join(",", dns.Nameservers) + ";" +
          string.Join("|", dns.Policy.Select(p => p.Domain + "=" + string.Join(",", p.Servers)));

    // ── YAML ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// mihomo config.yaml for this plan. The API secret is never written here: the service passes it on the engine's
    /// command line (<c>-secret</c>), which standard users can't read for a SYSTEM process (LEO.md §24.6).
    /// </summary>
    public string ToConfigYaml(string controller)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by LEO. Do not edit: LEO rewrites this file.");
        sb.AppendLine("mode: rule");
        sb.AppendLine("log-level: warning"); // info would log which app connects where (privacy)
        sb.AppendLine("ipv6: false");
        sb.AppendLine("allow-lan: false");
        sb.AppendLine($"external-controller: {Q(controller)}");
        sb.AppendLine("find-process-mode: always");
        sb.AppendLine("profile:");
        sb.AppendLine("  store-selected: false");
        sb.AppendLine("tun:");
        sb.AppendLine("  enable: true");
        sb.AppendLine("  stack: mixed");
        sb.AppendLine("  device: LEO");
        sb.AppendLine("  auto-route: true");
        sb.AppendLine("  auto-detect-interface: false");
        sb.AppendLine("  strict-route: false");
        sb.AppendLine(Dns is null ? "  dns-hijack: []" : "  dns-hijack: ['any:53']");
        sb.AppendLine("  route-exclude-address:");
        foreach (var r in ExcludedRanges) sb.AppendLine($"    - {Q(r)}");
        AppendDns(sb);
        AppendSniffer(sb);

        sb.AppendLine("proxies:");
        if (Outbounds.Count == 0) sb.AppendLine("  []");
        foreach (var (name, alias, _) in Outbounds)
        {
            sb.AppendLine($"  - name: {Q(name)}");
            sb.AppendLine("    type: direct");
            sb.AppendLine($"    interface-name: {Q(alias)}");
        }

        sb.AppendLine("proxy-groups:");
        foreach (var b in Bonds)
        {
            // LEO.md §22.2–22.3: always LEO's own health-check URL (the engine's default would be a Google address).
            sb.AppendLine($"  - name: {Q(BondGroupName(b.BondId))}");
            if (b.Mode == BondMode.Fastest)
            {
                sb.AppendLine("    type: url-test");
                sb.AppendLine("    tolerance: 50");
            }
            else
            {
                sb.AppendLine("    type: load-balance");
                sb.AppendLine($"    strategy: {(b.Mode == BondMode.EveryConnection ? "round-robin" : "consistent-hashing")}");
            }
            sb.AppendLine($"    url: {Q(BondCheck.Url)}");
            sb.AppendLine($"    expected-status: {Q(BondCheck.ExpectedStatus)}");
            sb.AppendLine("    interval: 10");
            // 5 s, not 3: a phone tether busy with a download answered checks in up to ~2 s on the dev PC, and one
            // timed-out check makes the engine skip that member until the next check (LEO.md §23).
            sb.AppendLine("    timeout: 5000");
            sb.AppendLine("    lazy: true");
            sb.AppendLine("    max-failed-times: 2");
            sb.AppendLine("    proxies:");
            foreach (var m in b.Members) sb.AppendLine($"      - {Q(m)}");
        }
        foreach (var (name, members) in Groups)
        {
            sb.AppendLine($"  - name: {Q(name)}");
            sb.AppendLine("    type: select");
            sb.AppendLine("    proxies:");
            foreach (var m in members) sb.AppendLine($"      - {Q(m)}");
        }

        sb.AppendLine("rule-providers:");
        if (Providers.Count == 0) sb.AppendLine("  {}");
        foreach (var (name, _, _) in Providers)
        {
            sb.AppendLine($"  {name}:");
            sb.AppendLine("    type: file");
            sb.AppendLine("    behavior: classical");
            sb.AppendLine("    format: yaml");
            sb.AppendLine($"    path: {Q("./rules/" + name + ".yaml")}");
        }

        sb.AppendLine("rules:");
        foreach (var line in GuardRules) sb.AppendLine($"  - {Q(line)}");
        foreach (var line in DestinationRules) sb.AppendLine($"  - {Q(line)}");
        foreach (var (name, group, _) in Providers) sb.AppendLine($"  - {Q($"RULE-SET,{name},{group}")}");
        sb.AppendLine($"  - {Q("MATCH," + UnruledGroup)}");
        return sb.ToString();
    }

    private void AppendDns(StringBuilder sb)
    {
        sb.AppendLine("dns:");
        if (Dns is null)
        {
            sb.AppendLine("  enable: false");
            return;
        }
        // redir-host (not fake-ip): apps get real addresses, so nothing breaks if the engine stops while Windows
        // still has answers cached. Domain rules match through the engine's address→domain mapping and sniffing.
        sb.AppendLine("  enable: true");
        sb.AppendLine("  ipv6: false");
        sb.AppendLine("  enhanced-mode: redir-host");
        sb.AppendLine("  use-hosts: true");
        sb.AppendLine("  use-system-hosts: true");
        sb.AppendLine("  respect-rules: false");
        sb.AppendLine("  default-nameserver:");
        foreach (var s in Dns.DefaultNameservers) sb.AppendLine($"    - {Q(s)}");
        sb.AppendLine("  nameserver:");
        foreach (var s in Dns.Nameservers) sb.AppendLine($"    - {Q(s)}");
        if (Dns.Policy.Count > 0)
        {
            sb.AppendLine("  nameserver-policy:");
            foreach (var (domain, servers) in Dns.Policy)
            {
                sb.AppendLine($"    {Q(domain)}:");
                foreach (var s in servers) sb.AppendLine($"      - {Q(s)}");
            }
        }
    }

    private void AppendSniffer(StringBuilder sb)
    {
        sb.AppendLine("sniffer:");
        if (!Sniff)
        {
            sb.AppendLine("  enable: false");
            return;
        }
        // Read the host name from TLS (SNI), HTTP (Host) and QUIC so domain rules work even when an app used its
        // own DNS (DoH) or a cached address. The connection still goes to the address the app chose.
        sb.AppendLine("  enable: true");
        sb.AppendLine("  force-dns-mapping: true");
        sb.AppendLine("  parse-pure-ip: true");
        sb.AppendLine("  override-destination: false");
        sb.AppendLine("  sniff:");
        sb.AppendLine("    HTTP:");
        sb.AppendLine("      ports: [80, 8080-8880]");
        sb.AppendLine("      override-destination: false");
        sb.AppendLine("    TLS:");
        sb.AppendLine("      ports: [443, 8443]");
        sb.AppendLine("    QUIC:");
        sb.AppendLine("      ports: [443, 8443]");
    }

    /// <summary>Rule-provider file contents (classical behaviour), by provider name.</summary>
    public IReadOnlyDictionary<string, string> ProviderFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _, regexes) in Providers)
        {
            var sb = new StringBuilder("payload:\n");
            foreach (var re in regexes) sb.Append("  - ").Append(Q("PROCESS-PATH-REGEX," + re)).Append('\n');
            if (regexes.Count == 0) sb.Append("  []\n");
            files[name] = sb.ToString();
        }
        return files;
    }

    /// <summary>YAML single-quoted scalar (backslashes stay literal; quotes doubled).</summary>
    internal static string Q(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}
