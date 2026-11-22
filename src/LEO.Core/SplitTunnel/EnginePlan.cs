using System.Globalization;
using System.Text;
using Leo.Core.Networking;

namespace Leo.Core.SplitTunnel;

/// <summary>An adapter as the engine planner sees it.</summary>
public sealed record EngineAdapter(string Id, string Alias, bool IsCandidate, bool IsUp, bool IsDemoted, HealthState Health);

/// <summary>A program discovered as a child of a ruled app (only for rules with IncludeChildren).</summary>
public sealed record ChildProgram(string ExePath, string ParentRuleId);

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

    /// <summary>Equal keys ⇒ the running engine only needs live selection/provider updates, not a reload.</summary>
    public required string StructureKey { get; init; }

    public static string OutboundName(string adapterId) => "net-" + Short(adapterId);
    public static string GroupName(string adapterId, OnTargetDown mode) =>
        "to-" + Short(adapterId) + (mode == OnTargetDown.Block ? "-block" : "-fb");

    private static string Short(string adapterId) =>
        new string(adapterId.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();

    public static bool IsUsable(EngineAdapter a) => a.IsUp && !a.IsDemoted && !a.Health.IsFailing();

    public static EnginePlan Build(IReadOnlyList<EngineAdapter> adapters, string? primaryId,
        IReadOnlyList<AppRule> rules, IReadOnlyList<ChildProgram> children)
    {
        var up = adapters.Where(a => a.IsCandidate && a.IsUp).OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var outbounds = up.Select(a => (OutboundName(a.Id), a.Alias, a.Id)).ToList();
        var byId = adapters.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var active = rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern) && !string.IsNullOrWhiteSpace(r.TargetAdapterId)).ToList();

        // Default route follows LEO's Primary (failover included).
        var groups = new List<(string, IReadOnlyList<string>)>();
        var selections = new List<GroupSelection>();
        var primaryMembers = outbounds.Count > 0 ? outbounds.Select(o => o.Item1).ToList() : ["DIRECT"];
        groups.Add((PrimaryGroup, primaryMembers));
        var primaryOut = primaryId is not null && up.Any(a => a.Id.Equals(primaryId, StringComparison.OrdinalIgnoreCase))
            ? OutboundName(primaryId) : primaryMembers[0];
        selections.Add(new GroupSelection(PrimaryGroup, primaryOut));

        // One select group per (target, fallback mode), switched live as health changes.
        foreach (var (target, mode) in active.Select(r => (r.TargetAdapterId, r.OnTargetDown)).Distinct()
                     .OrderBy(t => t.TargetAdapterId, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.OnTargetDown))
        {
            var name = GroupName(target, mode);
            var alternative = mode == OnTargetDown.Block ? Reject : PrimaryGroup;
            var members = new List<string>();
            var targetUp = up.Any(a => a.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (targetUp) members.Add(OutboundName(target));
            members.Add(alternative);
            groups.Add((name, members));
            var usable = targetUp && byId.TryGetValue(target, out var t) && IsUsable(t);
            selections.Add(new GroupSelection(name, usable ? OutboundName(target) : alternative));
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

        // Reload only when outbounds or groups change; rule lists and selections are updated live.
        var key = string.Join("|", outbounds.Select(o => $"{o.Item1}={o.Alias}")) + "#" +
                  string.Join("|", groups.Select(g => $"{g.Item1}:{string.Join(",", g.Item2)}")) + "#" +
                  string.Join("|", providers.Select(p => p.Item1));

        return new EnginePlan
        {
            Outbounds = outbounds,
            Groups = groups,
            Selections = selections,
            Providers = providers,
            StructureKey = key,
        };
    }

    // ── YAML ─────────────────────────────────────────────────────────────────

    /// <summary>mihomo config.yaml for this plan.</summary>
    public string ToConfigYaml(string controller, string secret)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by LEO. Do not edit: LEO rewrites this file.");
        sb.AppendLine("mode: rule");
        sb.AppendLine("log-level: warning"); // info would log which app connects where (privacy)
        sb.AppendLine("ipv6: false");
        sb.AppendLine("allow-lan: false");
        sb.AppendLine($"external-controller: {Q(controller)}");
        sb.AppendLine($"secret: {Q(secret)}");
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
        sb.AppendLine("  dns-hijack: []");
        sb.AppendLine("  route-exclude-address:");
        foreach (var r in ExcludedRanges) sb.AppendLine($"    - {Q(r)}");
        sb.AppendLine("dns:");
        sb.AppendLine("  enable: false");

        sb.AppendLine("proxies:");
        if (Outbounds.Count == 0) sb.AppendLine("  []");
        foreach (var (name, alias, _) in Outbounds)
        {
            sb.AppendLine($"  - name: {Q(name)}");
            sb.AppendLine("    type: direct");
            sb.AppendLine($"    interface-name: {Q(alias)}");
        }

        sb.AppendLine("proxy-groups:");
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
        foreach (var (name, group, _) in Providers) sb.AppendLine($"  - {Q($"RULE-SET,{name},{group}")}");
        sb.AppendLine($"  - {Q("MATCH," + PrimaryGroup)}");
        return sb.ToString();
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
