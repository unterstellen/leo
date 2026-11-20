using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Service.Health;
using Leo.Service.Networking;
using Xunit.Abstractions;

namespace Leo.Tests;

public sealed class InterfaceDnsTests
{
    [Fact]
    public void Builds_A_Query()
    {
        var q = InterfaceDns.BuildQuery(0x1234, "www.example.com");
        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, q[..12]);
        Assert.Equal(3, q[12]);                       // "www"
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1 }, q[^5..]); // root, QTYPE A, QCLASS IN
    }

    [Fact]
    public void Parses_Compressed_Answers()
    {
        // Response to the query above with two A records using name compression (0xC00C).
        var query = InterfaceDns.BuildQuery(0x1234, "www.example.com");
        var msg = new List<byte>(query);
        msg[2] = 0x81; msg[3] = 0x80; // response, RA, RCODE 0
        msg[7] = 2;                   // ANCOUNT = 2
        foreach (var ip in new[] { new byte[] { 93, 184, 216, 34 }, [1, 2, 3, 4] })
        {
            msg.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]);
            msg.AddRange(ip);
        }
        var answers = InterfaceDns.ParseResponse(msg.ToArray(), 0x1234);
        Assert.Equal(["93.184.216.34", "1.2.3.4"], answers!.Select(a => a.ToString()));
    }

    [Fact]
    public void Ignores_Foreign_Ids_And_Reports_NxDomain_As_Empty()
    {
        var q = InterfaceDns.BuildQuery(7, "nope.invalid");
        Assert.Null(InterfaceDns.ParseResponse(q, 8));
        q[2] = 0x81; q[3] = 0x83; // NXDOMAIN
        Assert.Empty(InterfaceDns.ParseResponse(q, 7)!);
    }
}

/// <summary>Real probes through this PC's connected adapters. Only with LEO_LIVE_TESTS=1.</summary>
public sealed class LiveProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Probes_Every_Connected_Adapter_Through_Itself()
    {
        if (Environment.GetEnvironmentVariable("LEO_LIVE_TESTS") != "1") return;

        var targets = new HealthSettings().ProbeTargets;
        var probed = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var props = nic.GetIPProperties();
            var ip = props.UnicastAddresses.Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."));
            if (ip is null || props.GatewayAddresses.Count == 0) continue;

            var adapter = new ProbeTargetAdapter(nic.Id, NetworkInventory.InterfaceIndexOf(props), ip,
                props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList());
            var report = await InterfaceProbe.RunAsync(adapter, targets, 1000, CancellationToken.None);
            output.WriteLine($"{nic.Name}: {report.State} {report.LatencyMs} ms ({report.Detail})");
            probed++;
            Assert.True(report.State is HealthState.Up or HealthState.Degraded, $"{nic.Name}: {report.State} {report.Detail}");
        }
        Assert.True(probed > 0);
    }

    [Fact]
    public async Task Unreachable_Target_Reports_NoInternet()
    {
        if (Environment.GetEnvironmentVariable("LEO_LIVE_TESTS") != "1") return;

        var nic = NetworkInterface.GetAllNetworkInterfaces().First(n =>
            n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0);
        var props = nic.GetIPProperties();
        var ip = props.UnicastAddresses.Select(a => a.Address).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        var adapter = new ProbeTargetAdapter(nic.Id, NetworkInventory.InterfaceIndexOf(props), ip, []);
        // TEST-NET-1 (RFC 5737): guaranteed unroutable.
        var report = await InterfaceProbe.RunAsync(adapter, [new ProbeTarget { Url = "http://192.0.2.1/", Expect = "204" }], 1000, CancellationToken.None);
        output.WriteLine($"{report.State}: {report.Detail}");
        Assert.Equal(HealthState.NoInternet, report.State);
    }
}
