using System.Text.RegularExpressions;

namespace Leo.Core.Networking;

/// <summary>A VPN adapter as identity resolution sees it.</summary>
public sealed record VpnCandidate(string Id, string Key, bool Connected);

/// <summary>
/// Stable identity for VPN adapters (LEO.md §20.2). Some VPNs create a new adapter (new GUID) per connection, so rules
/// that target a VPN also store a key from the adapter's description; when the GUID is gone the rule re-resolves to a
/// VPN with the same key (same idea as the MAC fallback for USB tethers).
/// </summary>
public static partial class VpnIdentity
{
    /// <summary>
    /// "Tailscale Tunnel", "WireGuard Tunnel: home". Windows' " #2" suffixes are dropped. Generic tunnel drivers shared by
    /// several products (WireGuard, Wintun, TAP) get the adapter's alias appended, because the alias names the tunnel.
    /// </summary>
    public static string KeyOf(string description, string alias)
    {
        var d = InstanceSuffix().Replace(description.Trim(), "");
        if (d.Length == 0) return alias.Trim();
        var generic = d.StartsWith("WireGuard", StringComparison.OrdinalIgnoreCase) || d.StartsWith("Wintun", StringComparison.OrdinalIgnoreCase)
                      || d.StartsWith("TAP-Windows", StringComparison.OrdinalIgnoreCase);
        return generic && alias.Trim().Length > 0 ? $"{d}: {alias.Trim()}" : d;
    }

    /// <summary>
    /// The adapter a rule's target means now: the stored id when that adapter exists; else a VPN with the same key
    /// (a connected one first); else the stored id unchanged (the rule's "if unavailable" choice applies).
    /// </summary>
    public static string Resolve(string targetId, string? targetKey, IReadOnlyCollection<string> presentIds, IReadOnlyList<VpnCandidate> vpns)
    {
        if (presentIds.Contains(targetId, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(targetKey)) return targetId;
        var match = vpns.Where(v => v.Key.Equals(targetKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Connected).ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return match?.Id ?? targetId;
    }

    [GeneratedRegex(@"\s+#\d+$")]
    private static partial Regex InstanceSuffix();
}
