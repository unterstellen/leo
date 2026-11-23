namespace Leo.Core.Networking;

/// <summary>What kind of network adapter this is, as LEO presents it.</summary>
public enum AdapterKind
{
    Other = 0,
    Ethernet = 1,
    WiFi = 2,
    UsbTether = 3,
    Cellular = 4,
    Bluetooth = 5,
    Vpn = 6,

    /// <summary>Hypervisor switches, loopback, WAN miniports, tunnels… never shown or managed.</summary>
    System = 99,
}

public static class AdapterKindExtensions
{
    /// <summary>Adapters LEO may make Primary and manage metrics for. Physical only: a VPN is never Primary (LEO.md §20.2).</summary>
    public static bool IsPrimaryCandidate(this AdapterKind kind) => kind is
        AdapterKind.Ethernet or AdapterKind.WiFi or AdapterKind.UsbTether or
        AdapterKind.Cellular or AdapterKind.Bluetooth;

    /// <summary>
    /// Adapters a rule may send traffic to (LEO.md §20.2): every Primary candidate, plus a VPN while it is connected
    /// (Up with a usable IPv4 address, see <see cref="VpnConnection.IsConnected"/>).
    /// </summary>
    public static bool IsRouteTarget(this AdapterKind kind, bool connected) =>
        kind.IsPrimaryCandidate() || (kind == AdapterKind.Vpn && connected);

    /// <summary>Default ordering for adapters the user hasn't ranked yet: wired first.</summary>
    public static int DefaultRank(this AdapterKind kind) => kind switch
    {
        AdapterKind.Ethernet => 0,
        AdapterKind.WiFi => 1,
        AdapterKind.UsbTether => 2,
        AdapterKind.Cellular => 3,
        AdapterKind.Bluetooth => 4,
        _ => 9,
    };
}

/// <summary>When a VPN adapter counts as connected (LEO.md §20.2).</summary>
public static class VpnConnection
{
    /// <summary>Up and holding an IPv4 address that isn't link-local (169.254.x = signed out / not connected).</summary>
    public static bool IsConnected(bool isUp, IEnumerable<string> ipv4) =>
        isUp && ipv4.Any(a => System.Net.IPAddress.TryParse(a, out var ip)
                              && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                              && !a.StartsWith("169.254.", StringComparison.Ordinal) && !a.StartsWith("0.", StringComparison.Ordinal));
}
