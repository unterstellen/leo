using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Leo.Service.Health;

/// <summary>
/// Minimal DNS A-record resolver that sends the query out of a specific interface to that network's own DNS
/// servers. The system resolver would ask the Primary's DNS, which is exactly what we can't trust when
/// checking a *different* network.
/// </summary>
internal static class InterfaceDns
{
    private static readonly IPAddress[] FallbackServers = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];

    public static async Task<IPAddress[]> ResolveAsync(string host, IPAddress localAddress, int interfaceIndex,
        IEnumerable<IPAddress> dnsServers, TimeSpan timeout, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];

        var servers = dnsServers.Where(s => s.AddressFamily == AddressFamily.InterNetwork).Concat(FallbackServers).Distinct().Take(3);
        Exception? last = null;
        foreach (var server in servers)
        {
            try
            {
                var answers = await QueryAsync(host, server, localAddress, interfaceIndex, timeout, ct);
                if (answers.Length > 0) return answers;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        // Keep "access denied" (a firewall filter) recognisable for the kill-switch detector (LEO.md §20.4).
        var code = Leo.Core.Networking.ProbeErrors.Classify(last) == Leo.Core.Networking.ProbeError.Denied ? SocketError.AccessDenied : SocketError.HostNotFound;
        throw new SocketException((int)code, $"DNS lookup for {host} failed via interface {interfaceIndex}: {last?.Message}");
    }

    private static async Task<IPAddress[]> QueryAsync(string host, IPAddress server, IPAddress localAddress, int interfaceIndex,
        TimeSpan timeout, CancellationToken ct)
    {
        using var socket = BoundSockets.Create(SocketType.Dgram, ProtocolType.Udp, localAddress, interfaceIndex);
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var query = BuildQuery(id, host);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await socket.SendToAsync(query, new IPEndPoint(server, 53), cts.Token);

        var buffer = new byte[1500];
        while (true)
        {
            var result = await socket.ReceiveFromAsync(buffer, new IPEndPoint(IPAddress.Any, 0), cts.Token);
            var answers = ParseResponse(buffer.AsSpan(0, result.ReceivedBytes), id);
            if (answers is not null) return answers;
        }
    }

    internal static byte[] BuildQuery(ushort id, string host)
    {
        var bytes = new List<byte>(32 + host.Length);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // QDCOUNT
        bytes.AddRange(header.ToArray());
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var l = Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)l.Length);
            bytes.AddRange(l);
        }
        bytes.Add(0);
        bytes.AddRange([0, 1, 0, 1]); // QTYPE=A, QCLASS=IN
        return [.. bytes];
    }

    /// <summary>Returns A records, an empty array for a valid answer with none, or null if this isn't our reply.</summary>
    internal static IPAddress[]? ParseResponse(ReadOnlySpan<byte> msg, ushort expectedId)
    {
        if (msg.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(msg) != expectedId) return null;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(msg[2..]);
        if ((flags & 0x8000) == 0) return null;          // not a response
        if ((flags & 0x000F) != 0) return [];            // RCODE error (NXDOMAIN etc.)
        int qd = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        int an = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);

        var pos = 12;
        for (var i = 0; i < qd; i++) { pos = SkipName(msg, pos) + 4; }

        var result = new List<IPAddress>();
        for (var i = 0; i < an && pos < msg.Length; i++)
        {
            pos = SkipName(msg, pos);
            if (pos + 10 > msg.Length) break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
            pos += 10;
            if (type == 1 && rdLength == 4 && pos + 4 <= msg.Length) result.Add(new IPAddress(msg.Slice(pos, 4)));
            pos += rdLength;
        }
        return [.. result];
    }

    private static int SkipName(ReadOnlySpan<byte> msg, int pos)
    {
        while (pos < msg.Length)
        {
            var len = msg[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2; // compression pointer
            pos += len + 1;
        }
        return pos;
    }
}
