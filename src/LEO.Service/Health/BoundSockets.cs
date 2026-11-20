using System.Net;
using System.Net.Sockets;

namespace Leo.Service.Health;

/// <summary>Sockets pinned to one interface, so traffic leaves through it even when it isn't the Primary.</summary>
internal static class BoundSockets
{
    private const int IpUnicastIf = 31; // IP_UNICAST_IF (ws2ipdef.h)

    /// <summary>
    /// Creates an IPv4 socket bound to <paramref name="localAddress"/> and forced out of interface
    /// <paramref name="interfaceIndex"/> (IP_UNICAST_IF takes the index in network byte order for IPv4).
    /// </summary>
    public static Socket Create(SocketType type, ProtocolType protocol, IPAddress localAddress, int interfaceIndex)
    {
        var socket = new Socket(AddressFamily.InterNetwork, type, protocol);
        try
        {
            var indexBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(interfaceIndex));
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf, indexBytes);
            socket.Bind(new IPEndPoint(localAddress, 0));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
