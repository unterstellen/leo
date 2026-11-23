using System.Net.NetworkInformation;
using System.Net.Sockets;
using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Service.Interop;
using Leo.Service.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Leo.Tests;

public sealed class AdapterClassifierTests
{
    [Theory]
    [InlineData("WiFi", "Qualcomm FastConnect 6900 Wi-Fi 6E Dual Band Simultaneous (DBS) WiFiCx Network Adapter", NetworkInterfaceType.Wireless80211, 0x84, AdapterKind.WiFi)]
    [InlineData("WiFi 3", "Qualcomm FastConnect 6900 Wi-Fi 6E Dual Band Simultaneous (DBS) WiFiCx Network Adapter", NetworkInterfaceType.Wireless80211, 0x8C, AdapterKind.System)] // NCF_HIDDEN
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, 0x84, AdapterKind.Ethernet)]
    [InlineData("Ethernet 3", "Remote NDIS based Internet Sharing Device", NetworkInterfaceType.Ethernet, 0x84, AdapterKind.UsbTether)]
    [InlineData("Ethernet 4", "Apple Mobile Device Ethernet", NetworkInterfaceType.Ethernet, 0x84, AdapterKind.UsbTether)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", NetworkInterfaceType.Ethernet, 0x81, AdapterKind.Bluetooth)]
    [InlineData("Tailscale", "Tailscale Tunnel", (NetworkInterfaceType)53, 0x1, AdapterKind.Vpn)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, 0x1, AdapterKind.System)]
    [InlineData("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", NetworkInterfaceType.Loopback, 0, AdapterKind.System)]
    [InlineData("Cellular", "Generic Mobile Broadband Adapter", NetworkInterfaceType.Wwanpp, 0x84, AdapterKind.Cellular)]
    public void Classifies(string name, string description, NetworkInterfaceType type, int flags, AdapterKind expected) =>
        Assert.Equal(expected, AdapterClassifier.Classify(name, description, type, flags));
}

public sealed class PriorityPlannerTests
{
    private static PlannerAdapter A(string id, AdapterKind kind, bool up = true, bool gw = true, int metric = 50) =>
        new(id, kind, up, gw, metric);

    [Fact]
    public void UserOrder_Wins_And_Gets_Lowest_Metrics()
    {
        var plan = PriorityPlanner.Plan([A("eth", AdapterKind.Ethernet), A("wifi", AdapterKind.WiFi)], ["wifi", "eth"]);
        Assert.Equal(["wifi", "eth"], plan.Order);
        Assert.Equal(10, plan.Metrics["wifi"]);
        Assert.Equal(20, plan.Metrics["eth"]);
        Assert.Equal("wifi", plan.PrimaryId);
    }

    [Fact]
    public void Unranked_Adapters_Append_Wired_First_And_Vpn_Is_Ignored()
    {
        var plan = PriorityPlanner.Plan(
            [A("phone", AdapterKind.UsbTether), A("vpn", AdapterKind.Vpn), A("wifi", AdapterKind.WiFi), A("eth", AdapterKind.Ethernet)],
            ["phone"]);
        Assert.Equal(["phone", "eth", "wifi"], plan.Order);
        Assert.False(plan.Metrics.ContainsKey("vpn"));
    }

    [Fact]
    public void Primary_Falls_Through_To_First_Connected_With_Gateway()
    {
        var plan = PriorityPlanner.Plan(
            [A("eth", AdapterKind.Ethernet, up: false), A("wifi2", AdapterKind.WiFi, gw: false), A("wifi", AdapterKind.WiFi)],
            ["eth", "wifi2", "wifi"]);
        Assert.Equal("wifi", plan.PrimaryId);
        Assert.Equal(10, plan.Metrics["eth"]); // still ranked first, so it takes over when it comes back
    }

    [Fact]
    public void Unranked_Connected_Adapter_Beats_Disconnected_Of_Same_Kind()
    {
        // Real case: "WiFi 2" (DBS secondary, disconnected, auto metric 25) vs "WiFi" (connected, metric 35).
        var plan = PriorityPlanner.Plan(
            [A("wifi2", AdapterKind.WiFi, up: false, gw: false, metric: 25), A("wifi", AdapterKind.WiFi, metric: 35)], []);
        Assert.Equal(["wifi", "wifi2"], plan.Order);
    }

    [Fact]
    public void Absent_Ids_In_User_Order_Are_Skipped()
    {
        var plan = PriorityPlanner.Plan([A("wifi", AdapterKind.WiFi)], ["unplugged-phone", "wifi"]);
        Assert.Equal(["wifi"], plan.Order);
        Assert.Equal(10, plan.Metrics["wifi"]);
    }

    [Fact]
    public void MoveToFront_Keeps_Others_In_Order() =>
        Assert.Equal(["c", "a", "b"], PriorityPlanner.MoveToFront(["a", "b", "c"], "c"));
}

public sealed class ConfigStoreTests
{
    [Fact]
    public void Persists_And_Reloads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"leo-config-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            store.Update(c =>
            {
                c.PriorityOrder = ["{A}", "{B}"];
                c.Adapters.Add(new AdapterSettings { Id = "{A}", Mac = "AA-BB-CC-DD-EE-FF", Nickname = "Home Fiber" });
                c.Networks.Add(new NetworkSettings { Key = "wifi:Zixel", Nickname = "Home" });
                return true;
            });

            var reloaded = new ConfigStore(path);
            Assert.Equal(["{A}", "{B}"], reloaded.Read(c => c.PriorityOrder));
            Assert.Equal("Home Fiber", reloaded.Read(c => c.Adapters.Single().Nickname));
            Assert.Equal("Home", reloaded.Read(c => c.Networks.Single().Nickname));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_File_Starts_Fresh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"leo-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            var store = new ConfigStore(path);
            Assert.Empty(store.Read(c => c.PriorityOrder));
            Assert.True(File.Exists(path + ".corrupt"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".corrupt");
        }
    }
}

/// <summary>Read-only checks against this machine's real adapters (nothing is changed).</summary>
public sealed class LiveInventoryTests(ITestOutputHelper output)
{
    [Fact]
    public void Scan_Lists_User_Adapters_Only()
    {
        var adapters = new NetworkInventory(NullLogger<NetworkInventory>.Instance).Scan();
        foreach (var a in adapters)
        {
            output.WriteLine($"{a.Kind,-10} {a.Name,-30} {a.Status,-12} idx={a.InterfaceIndex} metric={a.Metric}{(a.AutomaticMetric ? " (auto)" : "")} " +
                             $"net={a.Network?.Name}|{a.Network?.Key}|{a.Network?.Connectivity} ip={string.Join(',', a.IPv4)} gw={string.Join(',', a.Gateways)}");
        }
        Assert.DoesNotContain(adapters, a => a.Kind == AdapterKind.System);
        Assert.DoesNotContain(adapters, a => a.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Metric_Interop_Matches_Windows()
    {
        // Our MIB_IPINTERFACE_ROW marshalling must read sane metrics for interfaces that have an IPv4 address.
        var checkedAny = false;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var props = nic.GetIPProperties();
            if (!props.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
            var index = NetworkInventory.InterfaceIndexOf(props);
            var metric = IpHelper.GetMetric(index, AddressFamily.InterNetwork);
            output.WriteLine($"{nic.Name}: index={index} metric={metric}");
            Assert.NotNull(metric);
            // The engine's own tunnel adapter ("LEO", present while split tunneling runs) legitimately uses metric 0.
            Assert.InRange(metric!.Value.Metric, nic.Name.Equals("LEO", StringComparison.OrdinalIgnoreCase) ? 0 : 1, 9999);
            checkedAny = true;
        }
        Assert.True(checkedAny);
    }
}
