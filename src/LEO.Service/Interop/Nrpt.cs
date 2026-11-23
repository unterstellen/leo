using Microsoft.Win32;

namespace Leo.Service.Interop;

/// <summary>One Windows NRPT rule: names under these namespaces are resolved by these DNS servers.</summary>
public sealed record NrptRule(IReadOnlyList<string> Namespaces, IReadOnlyList<string> Servers);

/// <summary>
/// Reads Windows' Name Resolution Policy Table from the registry (read-only, LEO.md §20.3): where VPNs (Tailscale's
/// MagicDNS, corporate split DNS) and group policy put their per-domain DNS servers.
/// </summary>
public static class Nrpt
{
    private static readonly string[] Roots =
    [
        @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig",
        @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig",
    ];

    public static List<NrptRule> Read()
    {
        var rules = new List<NrptRule>();
        foreach (var root in Roots)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub is null) continue;
                    var namespaces = sub.GetValue("Name") switch
                    {
                        string[] many => many,
                        string one => [one],
                        _ => [],
                    };
                    var servers = (sub.GetValue("GenericDNSServers") as string ?? "")
                        .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (namespaces.Length > 0 && servers.Length > 0) rules.Add(new NrptRule(namespaces, servers));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return rules;
    }
}
