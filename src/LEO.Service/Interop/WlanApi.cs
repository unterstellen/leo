using System.Runtime.InteropServices;
using System.Text;

namespace Leo.Service.Interop;

/// <summary>
/// Thin wrapper over the Native Wifi API (wlanapi.dll). Variable-length structures are read by explicit offset
/// (x64 layouts from wlanapi.h / windot11.h) rather than marshalled structs.
/// </summary>
internal sealed class WlanApi : IDisposable
{
    /// <param name="State">1 = connected.</param>
    /// <param name="IsSecondary">Windows 11 secondary STA (e.g. "WiFi 2" on DBS cards): Windows drives it in
    /// "synchronized" mode alongside its primary; WlanConnect on it fails with ERROR_NOT_FOUND (1168).</param>
    public sealed record Interface(Guid Id, string Description, int State, bool IsSecondary = false);

    public sealed record AvailableNetwork(
        string Ssid, string ProfileName, int SignalPercent, bool Connectable, bool SecurityEnabled,
        int AuthAlgorithm, int CipherAlgorithm, bool IsConnected, bool HasProfile);

    public const int StateConnected = 1;

    // DOT11_AUTH_ALGORITHM
    public const int AuthOpen = 1, AuthWpaPsk = 4, AuthRsnaPsk = 7, AuthWpa3Sae = 9, AuthOwe = 10;

    private readonly IntPtr _handle;

    public WlanApi()
    {
        var rc = WlanOpenHandle(2, IntPtr.Zero, out _, out _handle);
        if (rc != 0) throw new InvalidOperationException($"WlanOpenHandle failed: {rc} (is the WLAN AutoConfig service running?)");
    }

    /// <summary>
    /// All Wi-Fi interfaces, including Windows 11 "secondary STA" interfaces (e.g. "WiFi 2" on dual-band
    /// DBS cards). WlanEnumInterfaces only returns primaries; secondaries come from
    /// wlan_intf_opcode_secondary_sta_interfaces (17) on each primary.
    /// </summary>
    public IReadOnlyList<Interface> GetInterfaces()
    {
        Check(WlanEnumInterfaces(_handle, IntPtr.Zero, out var list), nameof(WlanEnumInterfaces));
        List<Interface> result;
        try
        {
            result = ReadInterfaceList(list);
        }
        finally
        {
            WlanFreeMemory(list);
        }

        foreach (var primary in result.ToList())
        {
            var id = primary.Id;
            if (WlanQueryInterface(_handle, ref id, 17, IntPtr.Zero, out _, out var secondaries, out _) != 0) continue;
            try
            {
                foreach (var s in ReadInterfaceList(secondaries))
                {
                    if (result.All(r => r.Id != s.Id)) result.Add(s with { IsSecondary = true });
                }
            }
            finally
            {
                WlanFreeMemory(secondaries);
            }
        }
        return result;
    }

    /// <summary>Secondary STA interfaces of a primary (wlan_intf_opcode_secondary_sta_interfaces = 17).</summary>
    public IReadOnlyList<Guid> GetSecondaries(Guid primary)
    {
        if (WlanQueryInterface(_handle, ref primary, 17, IntPtr.Zero, out _, out var list, out _) != 0) return [];
        try
        {
            return ReadInterfaceList(list).Select(i => i.Id).ToList();
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>
    /// Windows' synchronized dual-station mode on a primary (wlan_intf_opcode_secondary_sta_synchronized_connections
    /// = 18): when on, the secondary joins the same network as the primary on the other band. Null if unsupported.
    /// </summary>
    public bool? GetSynchronizedConnections(Guid primary)
    {
        if (WlanQueryInterface(_handle, ref primary, 18, IntPtr.Zero, out _, out var data, out _) != 0) return null;
        try
        {
            return Marshal.ReadInt32(data) != 0;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    public void SetSynchronizedConnections(Guid primary, bool enabled)
    {
        var value = enabled ? 1 : 0;
        Check(WlanSetInterface(_handle, ref primary, 18, sizeof(int), ref value, IntPtr.Zero), "WlanSetInterface(dual station)");
    }

    private static List<Interface> ReadInterfaceList(IntPtr list)
    {
        var count = Marshal.ReadInt32(list);
        var result = new List<Interface>(count);
        for (var i = 0; i < count; i++)
        {
            var item = list + 8 + i * 532; // WLAN_INTERFACE_INFO = GUID(16) + WCHAR[256](512) + state(4)
            result.Add(new Interface(Marshal.PtrToStructure<Guid>(item), Marshal.PtrToStringUni(item + 16) ?? "",
                Marshal.ReadInt32(item + 528)));
        }
        return result;
    }

    public void Scan(Guid iface) => WlanScan(_handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    public IReadOnlyList<AvailableNetwork> GetAvailableNetworks(Guid iface)
    {
        Check(WlanGetAvailableNetworkList(_handle, ref iface, 0, IntPtr.Zero, out var list), nameof(WlanGetAvailableNetworkList));
        try
        {
            var count = Marshal.ReadInt32(list);
            var result = new List<AvailableNetwork>(count);
            for (var i = 0; i < count; i++)
            {
                var n = list + 8 + i * 628; // WLAN_AVAILABLE_NETWORK
                var profile = Marshal.PtrToStringUni(n) ?? "";
                var ssid = ReadSsid(n + 512);
                if (ssid.Length == 0) continue; // hidden network without a profile
                var flags = Marshal.ReadInt32(n + 620);
                result.Add(new AvailableNetwork(
                    ssid, profile,
                    SignalPercent: Marshal.ReadInt32(n + 604),
                    Connectable: Marshal.ReadInt32(n + 556) != 0,
                    SecurityEnabled: Marshal.ReadInt32(n + 608) != 0,
                    AuthAlgorithm: Marshal.ReadInt32(n + 612),
                    CipherAlgorithm: Marshal.ReadInt32(n + 616),
                    IsConnected: (flags & 0x1) != 0,
                    HasProfile: (flags & 0x2) != 0));
            }
            return result;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>SSID the interface is connected to, or null.</summary>
    public string? GetConnectedSsid(Guid iface)
    {
        // wlan_intf_opcode_current_connection = 7 → WLAN_CONNECTION_ATTRIBUTES:
        // state(4) mode(4) profile WCHAR[256](512) then WLAN_ASSOCIATION_ATTRIBUTES { DOT11_SSID … }
        var rc = WlanQueryInterface(_handle, ref iface, 7, IntPtr.Zero, out _, out var data, out _);
        if (rc != 0) return null; // not connected (ERROR_INVALID_STATE)
        try
        {
            if (Marshal.ReadInt32(data) != StateConnected) return null;
            return ReadSsid(data + 520);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    public IReadOnlyList<string> GetProfileNames(Guid iface)
    {
        if (WlanGetProfileList(_handle, ref iface, IntPtr.Zero, out var list) != 0) return [];
        try
        {
            var count = Marshal.ReadInt32(list);
            var names = new List<string>(count);
            for (var i = 0; i < count; i++) names.Add(Marshal.PtrToStringUni(list + 8 + i * 516) ?? "");
            return names;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>
    /// Profile XML. With <paramref name="includeKey"/> the key is in plain text (allowed for SYSTEM; needed only to
    /// copy a profile). Never logged or sent to the UI.
    /// </summary>
    public string? GetProfileXml(Guid iface, string profileName, bool includeKey = true)
    {
        uint flags = includeKey ? 4u : 0u; // WLAN_PROFILE_GET_PLAINTEXT_KEY
        if (WlanGetProfile(_handle, ref iface, profileName, IntPtr.Zero, out var xml, ref flags, out _) != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(xml);
        }
        finally
        {
            WlanFreeMemory(xml);
        }
    }

    public void SetProfile(Guid iface, string profileXml)
    {
        var rc = WlanSetProfile(_handle, ref iface, 0, profileXml, null, true, IntPtr.Zero, out var reason);
        if (rc != 0) throw new InvalidOperationException($"Windows rejected the Wi-Fi profile (error {rc}, reason {reason}).");
    }

    public void Connect(Guid iface, string profileName)
    {
        var p = new WlanConnectionParameters
        {
            Mode = 0,           // wlan_connection_mode_profile
            Profile = profileName,
            BssType = 1,        // dot11_BSS_type_infrastructure
        };
        Check(WlanConnect(_handle, ref iface, ref p, IntPtr.Zero), nameof(WlanConnect));
    }

    /// <summary>Deletes a saved profile from one interface. Returns false if it wasn't there.</summary>
    public bool DeleteProfile(Guid iface, string profileName)
    {
        var rc = WlanDeleteProfile(_handle, ref iface, profileName, IntPtr.Zero);
        if (rc == 1168) return false; // ERROR_NOT_FOUND
        Check(rc, nameof(WlanDeleteProfile));
        return true;
    }

    public void Disconnect(Guid iface) => Check(WlanDisconnect(_handle, ref iface, IntPtr.Zero), nameof(WlanDisconnect));

    public void Dispose() => WlanCloseHandle(_handle, IntPtr.Zero);

    private static string ReadSsid(IntPtr dot11Ssid)
    {
        var length = Math.Clamp(Marshal.ReadInt32(dot11Ssid), 0, 32);
        var bytes = new byte[length];
        Marshal.Copy(dot11Ssid + 4, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void Check(int rc, string api)
    {
        if (rc != 0) throw new InvalidOperationException($"{api} failed with error {rc}.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        public int Mode;
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public IntPtr Ssid;
        public IntPtr DesiredBssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, out int valueType);
    [DllImport("wlanapi.dll")] private static extern int WlanSetInterface(IntPtr handle, ref Guid iface, int opcode, int size, ref int data, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern int WlanGetProfile(IntPtr handle, ref Guid iface, string profileName, IntPtr reserved, out IntPtr xml, ref uint flags, out uint access);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern int WlanSetProfile(IntPtr handle, ref Guid iface, uint flags, string xml, string? security, bool overwrite, IntPtr reserved, out uint reasonCode);
    [DllImport("wlanapi.dll")] private static extern int WlanConnect(IntPtr handle, ref Guid iface, ref WlanConnectionParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern int WlanDeleteProfile(IntPtr handle, ref Guid iface, string profileName, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);
}
