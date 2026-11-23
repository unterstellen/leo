using System.Runtime.InteropServices;

namespace Leo.Service.Interop;

/// <summary>One LEO-owned Windows Firewall rule: block this program's outbound traffic to public addresses on these adapters.</summary>
public sealed record LeoFirewallRule(string Name, string ProgramPath, IReadOnlyList<string> InterfaceAliases, string RemoteAddresses);

/// <summary>
/// Windows Firewall access for fail-closed "VPN, else block" app rules (LEO.md §20.7 Q1, §21). Only rules in LEO's own group
/// are ever read back, changed or removed; rules LEO didn't create are never touched. Uses the firewall's COM API
/// (HNetCfg.FwPolicy2), the same one the Windows Firewall console uses.
/// </summary>
public static class FirewallRules
{
    public const string Group = "LEO (VPN only)";

    private const int DirectionOut = 2;   // NET_FW_RULE_DIR_OUT
    private const int ActionBlock = 0;    // NET_FW_ACTION_BLOCK
    private const int ProfilesAll = 0x7FFFFFFF;

    /// <summary>Names of the rules currently in LEO's group, with a fingerprint of their settings.</summary>
    public static Dictionary<string, string> ReadLeoRules()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        dynamic policy = NewPolicy();
        try
        {
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    if ((string?)rule.Grouping != Group) continue;
                    result[(string)rule.Name] = Fingerprint((string?)rule.ApplicationName ?? "", Aliases(rule.Interfaces), (string?)rule.RemoteAddresses ?? "");
                }
                finally { Marshal.FinalReleaseComObject(rule); }
            }
        }
        finally { Marshal.FinalReleaseComObject(policy); }
        return result;
    }

    /// <summary>Makes LEO's group exactly <paramref name="wanted"/>. Returns (added, removed).</summary>
    public static (int Added, int Removed) Reconcile(IReadOnlyList<LeoFirewallRule> wanted)
    {
        var existing = ReadLeoRules();
        var want = wanted.ToDictionary(w => w.Name, StringComparer.Ordinal);
        int added = 0, removed = 0;
        dynamic policy = NewPolicy();
        try
        {
            dynamic rules = policy.Rules;
            foreach (var (name, fp) in existing)
            {
                if (want.TryGetValue(name, out var w) && Fingerprint(w.ProgramPath, w.InterfaceAliases, w.RemoteAddresses) == fp) continue;
                RemoveFromGroup(rules, name);
                removed++;
            }
            foreach (var w in wanted)
            {
                if (existing.TryGetValue(w.Name, out var fp) && Fingerprint(w.ProgramPath, w.InterfaceAliases, w.RemoteAddresses) == fp) continue;
                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
                rule.Name = w.Name;
                rule.Description = "Created by LEO: this app may only use its VPN. Removed when the rule is deleted in LEO or LEO is uninstalled.";
                rule.Grouping = Group;
                rule.ApplicationName = w.ProgramPath;
                rule.Direction = DirectionOut;
                rule.Action = ActionBlock;
                rule.Profiles = ProfilesAll;
                rule.Protocol = 256; // any
                rule.RemoteAddresses = w.RemoteAddresses;
                if (w.InterfaceAliases.Count > 0) rule.Interfaces = w.InterfaceAliases.Cast<object>().ToArray();
                rule.Enabled = true;
                rules.Add(rule);
                Marshal.FinalReleaseComObject(rule);
                added++;
            }
            Marshal.FinalReleaseComObject(rules);
        }
        finally { Marshal.FinalReleaseComObject(policy); }
        return (added, removed);
    }

    /// <summary>Removes every rule in LEO's group (uninstall, split tunneling off).</summary>
    public static int RemoveAll() => Reconcile([]).Removed;

    private static void RemoveFromGroup(dynamic rules, string name)
    {
        // Remove by name, but only while the rule with that name is in LEO's group (never another product's rule).
        for (var i = 0; i < 64; i++)
        {
            dynamic? found = null;
            try { found = rules.Item(name); } catch (COMException) { return; }
            try
            {
                if ((string?)found!.Grouping != Group) return;
            }
            finally { Marshal.FinalReleaseComObject(found!); }
            rules.Remove(name);
        }
    }

    private static dynamic NewPolicy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    private static IReadOnlyList<string> Aliases(object? interfaces) =>
        interfaces is object[] arr ? arr.Select(o => o?.ToString() ?? "").ToList() : [];

    private static string Fingerprint(string program, IEnumerable<string> aliases, string remote) =>
        program.ToUpperInvariant() + "|" + string.Join(",", aliases.Select(a => a.ToUpperInvariant()).Order()) + "|" + Normalize(remote);

    // The firewall rewrites address lists (e.g. "1.0.0.0/8" → "1.0.0.0/255.0.0.0"), so compare loosely: count only.
    private static string Normalize(string remote) => remote.Split(',', StringSplitOptions.RemoveEmptyEntries).Length.ToString();
}
