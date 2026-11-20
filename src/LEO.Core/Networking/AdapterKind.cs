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
    /// <summary>Adapters LEO may make Primary and manage metrics for (VPNs come in M6).</summary>
    public static bool IsPrimaryCandidate(this AdapterKind kind) => kind is
        AdapterKind.Ethernet or AdapterKind.WiFi or AdapterKind.UsbTether or
        AdapterKind.Cellular or AdapterKind.Bluetooth;

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
