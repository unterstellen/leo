using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Leo.Core.Networking;

namespace Leo.Service.Interop;

/// <summary>
/// Watches Windows' route table (LEO.md §20.3): <c>NotifyRouteChange2</c> for IPv4 + IPv6, debounced, and reads the table
/// with <c>GetIpForwardTable2</c>. Read-only: LEO never adds, edits or deletes another product's routes. The table is
/// never logged (it can reveal where a VPN sends traffic).
/// </summary>
public sealed class RouteWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly NotifyCallback _callback; // kept alive while registered
    private readonly Timer _timer;
    private IntPtr _handle;

    /// <summary>Raised (on a timer thread) once the table has been quiet for 250 ms after a change.</summary>
    public event EventHandler? Changed;

    public RouteWatcher()
    {
        _callback = OnNotify;
        _timer = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Starts watching. False when Windows refused (the caller then relies on polling).</summary>
    public bool Start()
    {
        if (_handle != IntPtr.Zero) return true;
        return NotifyRouteChange2(AF_UNSPEC, _callback, IntPtr.Zero, false, out _handle) == 0;
    }

    private void OnNotify(IntPtr context, IntPtr row, int type) => _timer.Change(Debounce, Timeout.InfiniteTimeSpan);

    /// <summary>The current route table (IPv4 + IPv6) with adapter ids; metric = route metric + interface metric.</summary>
    public static List<RouteEntry> ReadRoutes()
    {
        var byIndex = new Dictionary<(int, AddressFamily), string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var p = nic.GetIPProperties();
                try { if (p.GetIPv4Properties() is { } v4) byIndex[(v4.Index, AddressFamily.InterNetwork)] = nic.Id; } catch (NetworkInformationException) { }
                try { if (p.GetIPv6Properties() is { } v6) byIndex[(v6.Index, AddressFamily.InterNetworkV6)] = nic.Id; } catch (NetworkInformationException) { }
            }
            catch (NetworkInformationException) { }
        }

        var list = new List<RouteEntry>();
        if (GetIpForwardTable2(AF_UNSPEC, out var table) != 0) return list;
        try
        {
            var count = Marshal.ReadInt32(table);
            var interfaceMetrics = new Dictionary<(int, AddressFamily), int>();
            for (var i = 0; i < count; i++)
            {
                var row = table + 8 + i * RowSize;
                var index = Marshal.ReadInt32(row, 8);
                var family = (ushort)Marshal.ReadInt16(row, 12);
                var af = family == AF_INET ? AddressFamily.InterNetwork : family == AF_INET6 ? AddressFamily.InterNetworkV6 : AddressFamily.Unknown;
                if (af == AddressFamily.Unknown || !byIndex.TryGetValue((index, af), out var id)) continue;

                var bytes = new byte[af == AddressFamily.InterNetwork ? 4 : 16];
                Marshal.Copy(row + 12 + (af == AddressFamily.InterNetwork ? 4 : 8), bytes, 0, bytes.Length);
                var prefixLength = Marshal.ReadByte(row, 40);
                var routeMetric = Marshal.ReadInt32(row, 84);
                if (!interfaceMetrics.TryGetValue((index, af), out var ifMetric))
                    interfaceMetrics[(index, af)] = ifMetric = IpHelper.GetMetric(index, af)?.Metric ?? 0;
                list.Add(new RouteEntry(id, new IPAddress(bytes), prefixLength, routeMetric + ifMetric));
            }
        }
        finally
        {
            FreeMibTable(table);
        }
        return list;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CancelMibChangeNotify2(_handle);
            _handle = IntPtr.Zero;
        }
        _timer.Dispose();
    }

    // MIB_IPFORWARD_ROW2 (netioapi.h), x64: Luid 0, Index 8, DestinationPrefix 12 (SOCKADDR_INET 28 + PrefixLength at 40),
    // NextHop 44, SitePrefixLength 72, ValidLifetime 76, PreferredLifetime 80, Metric 84, … sizeof = 104.
    // MIB_IPFORWARD_TABLE2: NumEntries, then rows at offset 8.
    private const int RowSize = 104;
    private const ushort AF_UNSPEC = 0;
    private const ushort AF_INET = 2;
    private const ushort AF_INET6 = 23;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NotifyCallback(IntPtr callerContext, IntPtr row, int notificationType);

    [DllImport("iphlpapi.dll")]
    private static extern int NotifyRouteChange2(ushort family, NotifyCallback callback, IntPtr context,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr handle);

    [DllImport("iphlpapi.dll")]
    private static extern int CancelMibChangeNotify2(IntPtr handle);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);
}
