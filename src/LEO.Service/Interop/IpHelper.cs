using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Leo.Service.Interop;

/// <summary>IP Helper API: interface metrics (Get/SetIpInterfaceEntry) and ARP lookups.</summary>
internal static class IpHelper
{
    private const ushort AF_INET = 2;
    private const ushort AF_INET6 = 23;

    public readonly record struct InterfaceMetric(int Metric, bool Automatic);

    public static InterfaceMetric? GetMetric(int interfaceIndex, AddressFamily family)
    {
        var row = NewRow(interfaceIndex, family);
        return GetIpInterfaceEntry(ref row) == 0
            ? new InterfaceMetric((int)row.Metric, row.UseAutomaticMetric != 0)
            : null;
    }

    /// <summary>Pins a manual metric (UseAutomaticMetric = false). Returns the Win32 error code (0 = success).</summary>
    public static int SetMetric(int interfaceIndex, AddressFamily family, int metric)
    {
        var row = NewRow(interfaceIndex, family);
        var rc = GetIpInterfaceEntry(ref row);
        if (rc != 0) return rc;
        if (row.UseAutomaticMetric == 0 && row.Metric == (uint)metric) return 0;

        row.UseAutomaticMetric = 0;
        row.Metric = (uint)metric;
        if (family == AddressFamily.InterNetwork) row.SitePrefixLength = 0; // required for IPv4 (MSDN)
        return SetIpInterfaceEntry(ref row);
    }

    /// <summary>Hands the metric back to Windows (UseAutomaticMetric = true).</summary>
    public static int RestoreAutomaticMetric(int interfaceIndex, AddressFamily family)
    {
        var row = NewRow(interfaceIndex, family);
        var rc = GetIpInterfaceEntry(ref row);
        if (rc != 0) return rc;
        if (row.UseAutomaticMetric != 0) return 0;

        row.UseAutomaticMetric = 1;
        if (family == AddressFamily.InterNetwork) row.SitePrefixLength = 0;
        return SetIpInterfaceEntry(ref row);
    }

    /// <summary>Resolves an IPv4 neighbour (e.g. the gateway) to its MAC, "AA-BB-CC-DD-EE-FF".</summary>
    public static string? ResolveMac(IPAddress ipv4)
    {
        if (ipv4.AddressFamily != AddressFamily.InterNetwork) return null;
#pragma warning disable CS0618 // Address is the simplest way to get the network-order uint
        var dest = (uint)ipv4.Address;
#pragma warning restore CS0618
        var mac = new byte[6];
        var len = (uint)mac.Length;
        if (SendARP(dest, 0, mac, ref len) != 0 || len < 6) return null;
        return BitConverter.ToString(mac, 0, 6);
    }

    private static MibIpInterfaceRow NewRow(int interfaceIndex, AddressFamily family)
    {
        InitializeIpInterfaceEntry(out var row);
        row.Family = family == AddressFamily.InterNetworkV6 ? AF_INET6 : AF_INET;
        row.InterfaceIndex = (uint)interfaceIndex;
        return row;
    }

    // MIB_IPINTERFACE_ROW (netioapi.h), x64 natural alignment.
    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpInterfaceRow
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public int RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public int LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public uint[] ZoneIndices;
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }

    [DllImport("iphlpapi.dll")]
    private static extern void InitializeIpInterfaceEntry(out MibIpInterfaceRow row);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [DllImport("iphlpapi.dll")]
    private static extern int SetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physicalAddrLen);
}
