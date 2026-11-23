using Leo.Core.Networking;

namespace Leo.Core.SplitTunnel;

/// <summary>A known VPN client: the programs that carry its tunnel (LEO.md §20.1).</summary>
public sealed record VpnClientPreset(string Name, IReadOnlyList<string> FileNames, IReadOnlyList<string> InstallFolders);

/// <summary>
/// VPN client presets (LEO.md §20.1): quick picks for "which network carries the VPN" app rules, and the loop guard that
/// keeps a VPN's own transport out of every VPN. Matched by file name (services included; the engine resolves PID → path).
/// </summary>
public static class VpnClients
{
    public static readonly IReadOnlyList<VpnClientPreset> Presets =
    [
        new("WireGuard", ["wireguard.exe"], [@"%ProgramFiles%\WireGuard"]),
        new("Tailscale", ["tailscaled.exe", "tailscale-ipn.exe"], [@"%ProgramFiles%\Tailscale"]),
        new("OpenVPN", ["openvpn.exe", "openvpnserv.exe", "OpenVPNConnect.exe", "ovpnconnector.exe"],
            [@"%ProgramFiles%\OpenVPN\bin", @"%ProgramFiles%\OpenVPN Connect"]),
        new("Mullvad VPN", ["mullvad-daemon.exe"], [@"%ProgramFiles%\Mullvad VPN\resources"]),
        new("NordVPN", ["nordvpn-service.exe", "nordvpn.exe"], [@"%ProgramFiles%\NordVPN"]),
        new("Proton VPN", ["ProtonVPN.WireGuardService.exe", "ProtonVPNService.exe"], [@"%ProgramFiles%\Proton\VPN"]),
        new("ExpressVPN", ["expressvpnd.exe"], [@"%ProgramFiles(x86)%\ExpressVPN\expressvpnd"]),
        new("Surfshark", ["Surfshark.Service.exe"], [@"%ProgramFiles%\Surfshark"]),
        new("Windscribe", ["WindscribeService.exe"], [@"%ProgramFiles%\Windscribe"]),
        new("Cloudflare WARP", ["warp-svc.exe"], [@"%ProgramFiles%\Cloudflare\Cloudflare WARP"]),
        new("Cisco Secure Client", ["vpnagent.exe"], [@"%ProgramFiles(x86)%\Cisco\Cisco Secure Client", @"%ProgramFiles(x86)%\Cisco\Cisco AnyConnect Secure Mobility Client"]),
        new("GlobalProtect", ["PanGPS.exe"], [@"%ProgramFiles%\Palo Alto Networks\GlobalProtect"]),
        new("FortiClient", ["FortiSSLVPNdaemon.exe"], [@"%ProgramFiles%\Fortinet\FortiClient"]),
        new("ZeroTier", ["ZeroTier One.exe", "zerotier-one_x64.exe"], [@"%ProgramData%\ZeroTier\One", @"%ProgramFiles(x86)%\ZeroTier\One"]),
    ];

    /// <summary>Engine regex (RE2) matching any of the preset's programs by file name, case-insensitive.</summary>
    public static string Regex(VpnClientPreset p) =>
        @"(?i)(^|\\)(" + string.Join("|", p.FileNames.Select(f => f.Replace(".", @"\.").Replace(" ", @"\x20"))) + ")$";

    /// <summary>The preset a program belongs to (by file name), or null.</summary>
    public static VpnClientPreset? Match(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        var name = Path.GetFileName(exePath.Trim().Replace('/', '\\'));
        return Presets.FirstOrDefault(p => p.FileNames.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The preset an app rule names directly (an exact path or a wildcard-free file name), or null. Used to refuse
    /// "VPN client → VPN" ("A VPN can't carry its own traffic"); folder/wildcard rules are covered by the guard rules.
    /// </summary>
    public static VpnClientPreset? NamedBy(AppRule rule) => rule.Kind switch
    {
        AppMatchKind.ExactPath => Match(rule.Pattern),
        AppMatchKind.NamePattern when rule.Pattern.IndexOfAny(['*', '?']) < 0 => Match(rule.Pattern),
        _ => null,
    };

    /// <summary>
    /// The user's app rule that decides where a preset's programs go (their "underlay" rule), if any: an exact path or
    /// name pattern that matches one of the preset's file names. Folder rules don't count (they name a place, not a VPN).
    /// </summary>
    public static AppRule? UnderlayRule(VpnClientPreset p, IEnumerable<AppRule> rules) =>
        rules.Where(r => r.Enabled && r.Kind != AppMatchKind.Folder)
            .Where(r => p.FileNames.Any(f => r.Kind == AppMatchKind.ExactPath
                ? Path.GetFileName(r.Pattern.Trim()).Equals(f, StringComparison.OrdinalIgnoreCase)
                : AppRuleMatcher.Matches(r, @"C:\x\" + f)))
            .OrderBy(r => r.Kind).FirstOrDefault();

    /// <summary>Installed presets: any of their programs exists in a known install folder (environment variables expanded).</summary>
    public static IEnumerable<(VpnClientPreset Preset, string ExePath)> Installed(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var p in Presets)
        {
            var found = p.InstallFolders.SelectMany(dir => p.FileNames.Select(f => Path.Combine(Environment.ExpandEnvironmentVariables(dir), f)))
                .FirstOrDefault(exists);
            if (found is not null) yield return (p, found);
        }
    }
}

/// <summary>Which adapters a rule may target, and rule defaults per target kind (LEO.md §20.1–20.2). Pure, shared by service and UI.</summary>
public static class RuleTargets
{
    /// <summary>New rules that target a VPN default to Block (people route an app through a VPN so it doesn't go out without it).</summary>
    public static OnTargetDown DefaultOnTargetDown(Leo.Core.Networking.AdapterKind targetKind) =>
        targetKind == Leo.Core.Networking.AdapterKind.Vpn ? OnTargetDown.Block : OnTargetDown.FallbackPrimary;

    /// <summary>
    /// A user-facing problem with this target, or null. Physical networks and VPNs (connected or not: a rule keeps
    /// pointing at a VPN that is currently disconnected) are fine; a VPN client's own programs can't be sent into a VPN.
    /// </summary>
    public static string? Check(AppRule? appRule, Leo.Core.Networking.AdapterKind targetKind, string targetName)
    {
        if (!targetKind.IsPrimaryCandidate() && targetKind != Leo.Core.Networking.AdapterKind.Vpn)
            return $"{targetName} can't carry this traffic.";
        if (targetKind == Leo.Core.Networking.AdapterKind.Vpn && appRule is not null && VpnClients.NamedBy(appRule) is { } preset)
            return $"A VPN can't carry its own traffic: {preset.Name} always uses a physical network.";
        return null;
    }
}

/// <summary>
/// Fail-closed "VPN, else block" (LEO.md §20.7 Q1, §21): which app rules get a LEO-owned Windows Firewall rule so the app
/// can't reach the internet over a physical network even while LEO's engine isn't running (crash back-off, stood aside,
/// service stopped). Only exact-path rules that target a VPN with "Block"; nothing while split tunneling is switched off.
/// While the engine runs the app's traffic enters LEO's adapter (or the VPN's), which the firewall rule doesn't cover.
/// </summary>
public static class FailClosedRules
{
    public sealed record Wanted(string RuleId, string Name, string ProgramPath);

    public static IReadOnlyList<Wanted> Plan(bool splitTunnelEnabled, IEnumerable<AppRule> rules, Func<string, bool> isVpnAdapter) =>
        !splitTunnelEnabled ? []
        : rules.Where(r => r.Enabled && r.OnTargetDown == OnTargetDown.Block && r.Kind == AppMatchKind.ExactPath
                           && Path.IsPathFullyQualified(r.Pattern.Trim())
                           && (r.TargetVpnKey is not null || isVpnAdapter(r.TargetAdapterId))
                           && VpnClients.NamedBy(r) is null)
            .Select(r => new Wanted(r.Id, $"LEO: {Clean(r.DisplayName)} only through its VPN [{r.Id[..Math.Min(8, r.Id.Length)]}]", r.Pattern.Trim()))
            .ToList();

    /// <summary>Public addresses only (LAN, router, CGNAT/Tailscale peers stay reachable): IPv4 complement of the tunnel exclusions, plus 2000::/3.</summary>
    public static string PublicRemoteAddresses() =>
        string.Join(",", VpnRoutes.Complement4(EnginePlan.ExcludedRanges).Select(p => $"{p.Prefix}/{p.Length}")) + ",2000::/3";

    private static string Clean(string s) => new string(s.Where(c => !char.IsControl(c) && c != '|').ToArray()).Trim() is { Length: > 0 } t ? t : "app";
}
