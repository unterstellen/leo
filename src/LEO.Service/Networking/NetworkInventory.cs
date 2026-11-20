using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Leo.Core.Networking;
using Leo.Ipc;
using Leo.Service.Interop;
using Windows.Networking.Connectivity;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace Leo.Service.Networking;

/// <summary>A scanned adapter before LEO's config (nicknames, priority) is applied.</summary>
public sealed record ScannedAdapter(
    string Id,
    int InterfaceIndex,
    string Name,
    string Description,
    AdapterKind Kind,
    string? Mac,
    AdapterStatus Status,
    long LinkSpeedBps,
    IReadOnlyList<string> IPv4,
    IReadOnlyList<string> IPv6,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    ScannedNetwork? Network,
    int? Metric,
    bool AutomaticMetric);

public sealed record ScannedNetwork(string Key, string Name, Connectivity Connectivity, bool IsMetered);

/// <summary>Builds the list of user-visible adapters from Windows (NetworkInterface + NLA + registry + IP Helper).</summary>
public sealed class NetworkInventory(ILogger<NetworkInventory> log)
{
    private readonly VisibleAdapters _visible = new();

    public IReadOnlyList<ScannedAdapter> Scan()
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var characteristics = AdapterRegistry.ReadCharacteristics();
        var visible = _visible.Get(nics.Select(n => n.Id));
        var profiles = ReadConnectionProfiles();
        var result = new List<ScannedAdapter>();

        foreach (var nic in nics)
        {
            try
            {
                characteristics.TryGetValue(nic.Id, out var flags);
                if (visible is not null && !visible.Contains(nic.Id)) flags |= AdapterClassifier.NcfHidden;
                var kind = AdapterClassifier.Classify(nic.Name, nic.Description, nic.NetworkInterfaceType, flags);
                if (kind == AdapterKind.System) continue;

                var props = nic.GetIPProperties();
                var index = InterfaceIndexOf(props);
                var ipv4 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).ToList();
                var ipv6 = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)
                    .Select(a => a.Address.ToString()).ToList();
                var gateways = props.GatewayAddresses.Select(g => g.Address)
                    .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).ToList();
                var dns = props.DnsAddresses.Where(a => !a.IsIPv6SiteLocal).Select(a => a.ToString()).ToList();

                var status = nic.OperationalStatus switch
                {
                    OperationalStatus.Up => AdapterStatus.Up,
                    OperationalStatus.Dormant => AdapterStatus.Connecting,
                    _ => AdapterStatus.Disconnected,
                };

                var metric = index >= 0 ? IpHelper.GetMetric(index, AddressFamily.InterNetwork) : null;
                profiles.TryGetValue(nic.Id, out var profile);
                var network = status == AdapterStatus.Up ? BuildNetwork(profile, gateways) : null;

                result.Add(new ScannedAdapter(
                    nic.Id, index, nic.Name, nic.Description, kind, FormatMac(nic.GetPhysicalAddress()), status,
                    status == AdapterStatus.Up ? Math.Max(0, nic.Speed) : 0,
                    ipv4, ipv6, gateways.Select(g => g.ToString()).ToList(), dns, network,
                    metric?.Metric, metric?.Automatic ?? true));
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Skipping adapter {Name}", nic.Name);
            }
        }

        return result;
    }

    private static ScannedNetwork? BuildNetwork(ProfileInfo? profile, IReadOnlyList<IPAddress> gateways)
    {
        string key;
        string name;
        if (profile?.Ssid is { Length: > 0 } ssid)
        {
            key = "wifi:" + ssid;
            name = ssid;
        }
        else
        {
            var gw4 = gateways.FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork);
            var gwMac = gw4 is null ? null : IpHelper.ResolveMac(gw4);
            name = profile?.Name ?? gw4?.ToString() ?? "Unidentified network";
            key = gwMac is not null ? "gw:" + gwMac : "nla:" + name;
        }

        return new ScannedNetwork(key, name, profile?.Connectivity ?? Connectivity.None, profile?.IsMetered ?? false);
    }

    private sealed record ProfileInfo(string Name, string? Ssid, Connectivity Connectivity, bool IsMetered);

    /// <summary>Windows network profiles (NLA) keyed by adapter GUID "{…}".</summary>
    private Dictionary<string, ProfileInfo> ReadConnectionProfiles()
    {
        var map = new Dictionary<string, ProfileInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in WinRtNetworkInformation.GetConnectionProfiles())
            {
                try
                {
                    var adapterId = p.NetworkAdapter?.NetworkAdapterId;
                    if (adapterId is null) continue;

                    // Windows keeps profiles for past networks (e.g. an old hotspot) on the same adapter;
                    // only connected ones (level > None) describe what the adapter is on right now.
                    var level = p.GetNetworkConnectivityLevel();
                    if (level == NetworkConnectivityLevel.None) continue;

                    var ssid = p.IsWlanConnectionProfile ? p.WlanConnectionProfileDetails?.GetConnectedSsid() : null;
                    var cost = p.GetConnectionCost();
                    var metered = cost is not null && cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
                    var connectivity = level switch
                    {
                        NetworkConnectivityLevel.InternetAccess => Connectivity.Internet,
                        NetworkConnectivityLevel.ConstrainedInternetAccess => Connectivity.ConstrainedInternet,
                        NetworkConnectivityLevel.LocalAccess => Connectivity.LocalAccess,
                        _ => Connectivity.None,
                    };

                    var key = "{" + adapterId.Value.ToString().ToUpperInvariant() + "}";
                    if (map.TryGetValue(key, out var existing) && existing.Connectivity >= connectivity) continue;
                    map[key] = new ProfileInfo(p.ProfileName, ssid, connectivity, metered);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Skipping connection profile");
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read Windows connection profiles");
        }
        return map;
    }

    /// <summary>GetIPv4Properties/GetIPv6Properties throw when that family isn't bound; try both.</summary>
    internal static int InterfaceIndexOf(IPInterfaceProperties props)
    {
        try { if (props.GetIPv4Properties() is { } v4) return v4.Index; } catch (NetworkInformationException) { }
        try { if (props.GetIPv6Properties() is { } v6) return v6.Index; } catch (NetworkInformationException) { }
        return -1;
    }

    private static string? FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 6 ? BitConverter.ToString(bytes) : null;
    }
}
