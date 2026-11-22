using System.Net.NetworkInformation;

namespace Leo.Core.Networking;

/// <summary>
/// Decides an adapter's <see cref="AdapterKind"/> from what Windows reports. Pure function so it's unit-testable.
/// </summary>
public static class AdapterClassifier
{
    // Network class driver "Characteristics" flags (HKLM\...\Class\{4d36e972-…}\NNNN).
    public const int NcfVirtual = 0x1;
    public const int NcfHidden = 0x8;

    private static readonly string[] SystemMarkers =
    [
        "Hyper-V", "vEthernet", "vSwitch", "VMware", "VirtualBox", "Host-Only", "WAN Miniport",
        "Kernel Debug", "Teredo", "6to4", "IP-HTTPS", "ISATAP", "Loopback", "Wi-Fi Direct Virtual",
        "Microsoft Wi-Fi Direct", "Npcap", "Hamachi Loopback",
        "Meta Tunnel", // LEO's own engine adapter (mihomo/Wintun) — never shown or managed
    ];

    private static readonly string[] TetherMarkers =
    [
        "Remote NDIS", "RNDIS", "NCM", "Apple Mobile Device Ethernet", "iPhone USB", "Android USB",
        "USB Tethering", "UsbNcm",
    ];

    private static readonly string[] VpnMarkers =
    [
        "Tailscale", "WireGuard", "Wintun", "TAP-Windows", "TAP-NordVPN", "OpenVPN", "ZeroTier", "VPN",
        "Cisco AnyConnect", "Fortinet", "PANGP", "GlobalProtect", "ProtonVPN", "Mullvad", "Cloudflare WARP",
        "LEO Tunnel",
    ];

    public static AdapterKind Classify(string name, string description, NetworkInterfaceType type, int characteristics)
    {
        var text = $"{name} {description}";

        if ((characteristics & NcfHidden) != 0) return AdapterKind.System;
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return AdapterKind.System;
        if (ContainsAny(text, SystemMarkers)) return AdapterKind.System;

        if (ContainsAny(text, TetherMarkers)) return AdapterKind.UsbTether;
        if (text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return AdapterKind.Bluetooth;
        if (ContainsAny(text, VpnMarkers) || type is NetworkInterfaceType.Ppp) return AdapterKind.Vpn;

        return type switch
        {
            NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => AdapterKind.Cellular,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
                NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or
                NetworkInterfaceType.Ethernet3Megabit =>
                (characteristics & NcfVirtual) != 0 ? AdapterKind.Other : AdapterKind.Ethernet,
            // IF_TYPE_PROP_VIRTUAL (53): user-mode tunnels such as Wintun/WireGuard.
            (NetworkInterfaceType)53 => AdapterKind.Vpn,
            _ => AdapterKind.Other,
        };
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        foreach (var m in markers)
        {
            if (text.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
