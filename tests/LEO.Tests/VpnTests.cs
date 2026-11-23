using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Leo.Core.Networking;
using Leo.Core.SplitTunnel;
using Leo.Core.Stats;
using Leo.Ipc;
using Leo.Service.Interop;

namespace Leo.Tests;

/// <summary>M6 VPN coexistence (LEO.md §20.8): route analysis, identity, presets, engine plan, kill-switch detection.</summary>
public sealed class VpnRoutesTests
{
    private const string Vpn = "{AAAA0000-0000-0000-0000-000000000001}";
    private const string Vpn2 = "{BBBB0000-0000-0000-0000-000000000002}";
    private const string Leo = "{CCCC0000-0000-0000-0000-000000000003}";
    private const string WiFi = "{7D0119DE-B600-4B42-B91B-B95CCD146227}";

    private static RouteEntry R(string id, string cidr, int metric = 5) => RouteEntry.Parse(id, cidr, metric);

    private static VpnRouteAnalysis One(params RouteEntry[] routes) => VpnRoutes.Analyze(routes, [Vpn])[Vpn];

    [Fact]
    public void Default_Route_On_The_Vpn_Takes_Default()
    {
        var a = One(R(Vpn, "0.0.0.0/0"), R(Vpn, "10.8.0.0/24"));
        Assert.True(a.TakesDefault4);
        Assert.False(a.PrivateOnly);
    }

    [Fact]
    public void Two_Halves_Take_Default()
    {
        Assert.True(One(R(Vpn, "0.0.0.0/1"), R(Vpn, "128.0.0.0/1")).TakesDefault4);
    }

    [Fact]
    public void A_Split_Covering_At_Least_Half_Of_Public_Space_Takes_Default()
    {
        // Mullvad-style: the space split into quarters except one.
        Assert.True(One(R(Vpn, "0.0.0.0/2"), R(Vpn, "64.0.0.0/2"), R(Vpn, "128.0.0.0/2")).TakesDefault4);
        // One quarter is far from half: it "routes some destinations".
        var some = One(R(Vpn, "64.0.0.0/2"));
        Assert.False(some.TakesDefault4);
        Assert.Equal(1, some.PublicRanges4);
    }

    [Fact]
    public void Corporate_Public_Ranges_Are_Counted_Not_Default()
    {
        var a = One(R(Vpn, "203.0.113.0/24"), R(Vpn, "198.51.100.0/24"), R(Vpn, "10.20.0.0/16"));
        Assert.False(a.TakesDefault);
        Assert.Equal(2, a.PublicRanges4);
        Assert.False(a.PrivateOnly);
    }

    [Fact]
    public void Tailscale_Without_Exit_Node_Is_Private_Only()
    {
        var a = One(R(Vpn, "100.64.0.0/10"), R(Vpn, "100.100.100.100/32"), R(Vpn, "100.101.102.103/32"),
            R(Vpn, "255.255.255.255/32"), R(Vpn, "224.0.0.0/4"), R(Vpn, "fd7a:115c:a1e0::/48"));
        Assert.True(a.PrivateOnly);
        Assert.Equal(int.MaxValue, a.DefaultMetric);
    }

    [Fact]
    public void Leos_Own_Adapter_Is_Ignored()
    {
        var routes = VpnRoutes.Complement4(EnginePlan.ExcludedRanges).Select(p => new RouteEntry(Leo, p.Prefix, p.Length, 0)).ToList();
        var analysis = VpnRoutes.Analyze(routes, [Vpn]);
        Assert.Single(analysis);
        Assert.True(analysis[Vpn].PrivateOnly);
    }

    [Fact]
    public void Ipv6_Default_Or_Halves_Take_Default6()
    {
        Assert.True(One(R(Vpn, "::/0")).TakesDefault6);
        Assert.True(One(R(Vpn, "::/1"), R(Vpn, "8000::/1")).TakesDefault6);
        var only6 = One(R(Vpn, "::/1"));
        Assert.False(only6.TakesDefault6);
        Assert.False(only6.TakesDefault4);
    }

    [Fact]
    public void Two_Vpns_Taking_Default_Lower_Metric_Wins_And_Skip_Is_Respected()
    {
        var analysis = VpnRoutes.Analyze([R(Vpn, "0.0.0.0/0", 50), R(Vpn2, "0.0.0.0/0", 10)], [Vpn, Vpn2]);
        Assert.Equal(Vpn2, VpnRoutes.DefaultWinner(analysis.Values));
        Assert.Equal(Vpn, VpnRoutes.DefaultWinner(analysis.Values, new HashSet<string>([Vpn2])));
        Assert.Null(VpnRoutes.DefaultWinner(analysis.Values, new HashSet<string>([Vpn, Vpn2])));
    }

    [Fact]
    public void Engine_Complement_Routes_Beat_A_Vpn_Catch_All_For_Public_Addresses_Only()
    {
        var complement = VpnRoutes.Complement4(EnginePlan.ExcludedRanges);
        Assert.DoesNotContain(complement, p => p.Length <= 1); // never 0/0 or 0/1+128/1 (LEO.md §20 dev-PC findings)
        Assert.Contains(complement, p => p.Prefix.Equals(IPAddress.Parse("32.0.0.0")) && p.Length == 3);
        Assert.All(complement, p => Assert.InRange(p.Length, 2, 32));

        var routes = complement.Select(p => new RouteEntry(Leo, p.Prefix, p.Length, 0)).ToList();
        routes.Add(R(Vpn, "0.0.0.0/0", 1));
        routes.Add(R(Vpn, "0.0.0.0/1", 1));
        routes.Add(R(Vpn, "128.0.0.0/1", 1));
        routes.Add(R(Vpn, "100.64.0.0/10", 1));
        routes.Add(R(Vpn, "10.0.0.0/8", 1));
        routes.Add(R(WiFi, "0.0.0.0/0", 10));

        foreach (var pub in new[] { "8.8.8.8", "1.1.1.1", "141.95.207.211", "203.0.113.9", "223.255.255.1" })
            Assert.Equal(Leo, VpnRoutes.BestRoute(routes, IPAddress.Parse(pub))!.AdapterId);
        foreach (var priv in new[] { "10.1.2.3", "100.101.102.103" })
            Assert.Equal(Vpn, VpnRoutes.BestRoute(routes, IPAddress.Parse(priv))!.AdapterId);
        Assert.True(VpnRoutes.EngineRoutesPresent(routes, Leo));
        Assert.False(VpnRoutes.EngineRoutesPresent(routes.Where(r => r.AdapterId != Leo).ToList(), Leo));
    }

    [Fact]
    public void Analysis_Carries_Counts_Only_No_Addresses()
    {
        var text = One(R(Vpn, "203.0.113.0/24"), R(Vpn, "0.0.0.0/0")).ToString();
        Assert.DoesNotMatch(new Regex(@"\d+\.\d+\.\d+\.\d+"), text);
    }
}

public sealed class VpnIdentityTests
{
    [Theory]
    [InlineData("Tailscale Tunnel", "Tailscale", "Tailscale Tunnel")]
    [InlineData("WireGuard Tunnel", "home", "WireGuard Tunnel: home")]
    [InlineData("WireGuard Tunnel #2", "office", "WireGuard Tunnel: office")]
    [InlineData("TAP-Windows Adapter V9 #3", "Local Area Connection 2", "TAP-Windows Adapter V9: Local Area Connection 2")]
    [InlineData("", "Corp VPN", "Corp VPN")]
    public void Keys_Are_Stable_Across_Instances(string description, string alias, string expected) =>
        Assert.Equal(expected, VpnIdentity.KeyOf(description, alias));

    [Fact]
    public void Rule_Re_Resolves_By_Key_After_A_Guid_Change()
    {
        var vpns = new List<VpnCandidate> { new("{NEW-OFF}", "Corp VPN", false), new("{NEW-ON}", "Corp VPN", true), new("{OTHER}", "Tailscale Tunnel", true) };
        IReadOnlyCollection<string> present = ["{NEW-OFF}", "{NEW-ON}", "{OTHER}"];
        Assert.Equal("{NEW-ON}", VpnIdentity.Resolve("{OLD}", "Corp VPN", present, vpns));
        Assert.Equal("{OLD}", VpnIdentity.Resolve("{OLD}", "Gone VPN", present, vpns)); // no match → the rule's fallback applies
        Assert.Equal("{OTHER}", VpnIdentity.Resolve("{OTHER}", "Corp VPN", present, vpns)); // id still present: unchanged
        Assert.Equal("{OLD}", VpnIdentity.Resolve("{OLD}", null, present, vpns)); // physical rule: never re-resolved
    }

    [Fact]
    public void Connected_Needs_A_Usable_Address()
    {
        Assert.False(VpnConnection.IsConnected(true, ["169.254.83.107"])); // Tailscale signed out
        Assert.False(VpnConnection.IsConnected(false, ["100.101.102.103"]));
        Assert.True(VpnConnection.IsConnected(true, ["169.254.1.1", "100.101.102.103"]));
        Assert.True(AdapterKind.Vpn.IsRouteTarget(connected: true));
        Assert.False(AdapterKind.Vpn.IsRouteTarget(connected: false));
        Assert.True(AdapterKind.WiFi.IsRouteTarget(connected: false));
        Assert.False(AdapterKind.Vpn.IsPrimaryCandidate());
    }

    [Fact]
    public void A_Vpn_Is_Never_A_Priority_Candidate()
    {
        var plan = PriorityPlanner.Plan(
        [
            new PlannerAdapter("wifi", AdapterKind.WiFi, true, true, 10),
            new PlannerAdapter("ts", AdapterKind.Vpn, true, true, 5),
        ], ["ts", "wifi"]);
        Assert.Equal(["wifi"], plan.Order);
        Assert.Equal("wifi", plan.PrimaryId);
        Assert.False(plan.Metrics.ContainsKey("ts"));
    }
}

public sealed class VpnClientsTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Tailscale\tailscaled.exe", "Tailscale")]
    [InlineData(@"C:\Program Files\WireGuard\wireguard.exe", "WireGuard")]
    [InlineData(@"C:\Program Files\OpenVPN\bin\OPENVPN.EXE", "OpenVPN")]
    [InlineData(@"C:\ProgramData\ZeroTier\One\ZeroTier One.exe", "ZeroTier")]
    [InlineData(@"C:\Windows\System32\svchost.exe", null)]
    public void Presets_Match_By_File_Name(string path, string? preset)
    {
        Assert.Equal(preset, VpnClients.Match(path)?.Name);
        foreach (var p in VpnClients.Presets)
            Assert.Equal(p.Name == preset, Regex.IsMatch(path, VpnClients.Regex(p)));
    }

    [Fact]
    public void Preset_Regexes_Are_Engine_Safe()
    {
        foreach (var p in VpnClients.Presets)
        {
            var re = VpnClients.Regex(p);
            Assert.DoesNotContain(",", re);
            Assert.DoesNotContain(" ", re);
            Assert.StartsWith("(?i)", re);
        }
    }

    [Fact]
    public void A_Vpn_Client_Cannot_Be_Sent_Into_A_Vpn()
    {
        var wg = new AppRule { Kind = AppMatchKind.ExactPath, Pattern = @"C:\Program Files\WireGuard\wireguard.exe" };
        Assert.Contains("can't carry its own traffic", RuleTargets.Check(wg, AdapterKind.Vpn, "Tailscale"));
        Assert.Null(RuleTargets.Check(wg, AdapterKind.Ethernet, "Ethernet")); // its underlay: fine
        var byName = new AppRule { Kind = AppMatchKind.NamePattern, Pattern = "openvpn.exe" };
        Assert.NotNull(RuleTargets.Check(byName, AdapterKind.Vpn, "Corp"));
        var curl = new AppRule { Kind = AppMatchKind.ExactPath, Pattern = @"C:\Windows\System32\curl.exe" };
        Assert.Null(RuleTargets.Check(curl, AdapterKind.Vpn, "Tailscale"));
        Assert.Null(RuleTargets.Check(null, AdapterKind.Vpn, "Tailscale"));
        Assert.NotNull(RuleTargets.Check(curl, AdapterKind.Other, "Some adapter"));
    }

    [Fact]
    public void New_Vpn_Rules_Default_To_Block()
    {
        Assert.Equal(OnTargetDown.Block, RuleTargets.DefaultOnTargetDown(AdapterKind.Vpn));
        Assert.Equal(OnTargetDown.FallbackPrimary, RuleTargets.DefaultOnTargetDown(AdapterKind.Ethernet));
    }

    [Fact]
    public void Installed_Looks_In_Known_Folders()
    {
        var expected = Path.Combine(Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Tailscale"), "tailscaled.exe");
        var found = VpnClients.Installed(p => p.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal("Tailscale", Assert.Single(found).Preset.Name);
    }
}

public sealed class VpnEnginePlanTests
{
    private const string WiFi = "{7D0119DE-B600-4B42-B91B-B95CCD146227}";
    private const string Eth = "{62939052-A2ED-4485-87CE-E95E2151550D}";
    private const string Ts = "{A1B2C3D4-0000-4000-8000-000000000001}";
    private const string Wg = "{E5F60718-0000-4000-8000-000000000002}";

    private static EngineAdapter Phys(string id, string alias, string dns) => new(id, alias, true, true, false, HealthState.Up, [dns]);

    private static EngineAdapter V(string id, string alias, bool connected = true, bool takesDefault = false, bool skip = false,
        int metric = 5, string[]? dns = null, HealthState health = HealthState.Up) =>
        new(id, alias, false, connected, false, health, dns ?? [], IsVpn: true, TakesDefault: takesDefault, SkipUnruled: skip, RouteMetric: metric);

    private static List<EngineAdapter> Net(params EngineAdapter[] vpns) =>
        [Phys(WiFi, "WiFi", "192.168.90.1"), Phys(Eth, "Ethernet", "192.168.42.129"), .. vpns];

    private static AppRule Curl(string target, OnTargetDown mode = OnTargetDown.Block) =>
        new() { Id = "curl", Kind = AppMatchKind.ExactPath, Pattern = @"C:\Windows\System32\curl.exe", TargetAdapterId = target, OnTargetDown = mode };

    private static string Selection(EnginePlan p, string group) => p.Selections.Single(s => s.Group == group).Choice;

    [Fact]
    public void Connected_Vpn_Gets_A_Bound_Outbound_But_Never_Joins_The_Primary_Group()
    {
        var plan = EnginePlan.Build(Net(V(Ts, "Tailscale")), WiFi, [Curl(Ts)], []);
        Assert.Contains(plan.Outbounds, o => o.Name == "vpn-a1b2c3d4" && o.Alias == "Tailscale");
        Assert.DoesNotContain("vpn-a1b2c3d4", plan.Groups.Single(g => g.Name == EnginePlan.PrimaryGroup).Members);
        Assert.Contains("interface-name: 'Tailscale'", plan.ToConfigYaml("127.0.0.1:9", "s"));
        Assert.Equal("vpn-a1b2c3d4", Selection(plan, EnginePlan.GroupName(Ts, OnTargetDown.Block)));
    }

    [Fact]
    public void Vpn_Rule_Is_Usable_While_Connected_Even_Without_Internet_And_Rejects_When_Down()
    {
        var privateOnly = EnginePlan.Build(Net(V(Ts, "Tailscale", health: HealthState.NoInternet)), WiFi, [Curl(Ts)], []);
        Assert.Equal("vpn-a1b2c3d4", Selection(privateOnly, EnginePlan.GroupName(Ts, OnTargetDown.Block)));

        var down = EnginePlan.Build(Net(V(Ts, "Tailscale", connected: false)), WiFi, [Curl(Ts)], []);
        Assert.Equal(EnginePlan.Reject, Selection(down, EnginePlan.GroupName(Ts, OnTargetDown.Block)));
        Assert.DoesNotContain(down.Outbounds, o => o.AdapterId == Ts);

        var fallback = EnginePlan.Build(Net(V(Ts, "Tailscale", connected: false)), WiFi, [Curl(Ts, OnTargetDown.FallbackPrimary)], []);
        Assert.Equal(EnginePlan.PrimaryGroup, Selection(fallback, EnginePlan.GroupName(Ts, OnTargetDown.FallbackPrimary)));
    }

    [Fact]
    public void Guard_Rules_Come_First_For_Every_Preset_Whenever_A_Vpn_Outbound_Exists()
    {
        var dest = new DestinationRule { Kind = DestinationKind.DomainSuffix, Value = "example.com", TargetAdapterId = Ts };
        var plan = EnginePlan.Build(Net(V(Ts, "Tailscale")), WiFi, [Curl(Eth)], [], [dest]);
        Assert.Equal(VpnClients.Presets.Count, plan.GuardRules.Count);
        Assert.All(plan.GuardRules, g => Assert.StartsWith("PROCESS-PATH-REGEX,", g));
        Assert.All(plan.GuardRules, g => Assert.EndsWith("," + EnginePlan.PrimaryGroup, g));

        var yaml = plan.ToConfigYaml("127.0.0.1:9", "s");
        var rules = yaml[yaml.IndexOf("\nrules:", StringComparison.Ordinal)..].TrimStart('\n')
            .Split('\n').Skip(1).Where(l => l.Trim().Length > 0).ToList();
        for (var i = 0; i < plan.GuardRules.Count; i++) Assert.Contains("PROCESS-PATH-REGEX", rules[i]);
        Assert.Contains("DOMAIN-SUFFIX", rules[plan.GuardRules.Count]);
        Assert.EndsWith("'MATCH,LEO-UNRULED'", rules[^1].TrimEnd());

        Assert.Empty(EnginePlan.Build(Net(), WiFi, [Curl(Eth)], []).GuardRules); // no VPN outbound, no guards
    }

    [Fact]
    public void Guard_Follows_The_Users_Underlay_Rule()
    {
        var wgRule = new AppRule { Id = "wg", Kind = AppMatchKind.ExactPath, Pattern = @"C:\Program Files\WireGuard\wireguard.exe", TargetAdapterId = Eth };
        var plan = EnginePlan.Build(Net(V(Wg, "home")), WiFi, [wgRule], []);
        var guard = plan.GuardRules.Single(g => g.Contains(@"|wireguard\.exe", StringComparison.OrdinalIgnoreCase) || g.Contains(@"(wireguard\.exe)", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("," + EnginePlan.GroupName(Eth, OnTargetDown.FallbackPrimary), guard);
    }

    [Fact]
    public void Unruled_Group_Follows_Skips_And_Picks_The_Lower_Metric()
    {
        var follow = EnginePlan.Build(Net(V(Ts, "Tailscale", takesDefault: true)), WiFi, [Curl(Eth)], []);
        Assert.Equal(["vpn-a1b2c3d4", EnginePlan.PrimaryGroup], follow.Groups.Single(g => g.Name == EnginePlan.UnruledGroup).Members);
        Assert.Equal("vpn-a1b2c3d4", Selection(follow, EnginePlan.UnruledGroup));
        Assert.Equal(Ts, follow.UnruledVpnId);

        var skip = EnginePlan.Build(Net(V(Ts, "Tailscale", takesDefault: true, skip: true)), WiFi, [Curl(Eth)], []);
        Assert.Equal(EnginePlan.PrimaryGroup, Selection(skip, EnginePlan.UnruledGroup));
        Assert.Null(skip.UnruledVpnId);

        var privateOnly = EnginePlan.Build(Net(V(Ts, "Tailscale")), WiFi, [Curl(Eth)], []);
        Assert.Equal(EnginePlan.PrimaryGroup, Selection(privateOnly, EnginePlan.UnruledGroup));

        var down = EnginePlan.Build(Net(V(Ts, "Tailscale", connected: false, takesDefault: true)), WiFi, [Curl(Eth)], []);
        Assert.Equal(EnginePlan.PrimaryGroup, Selection(down, EnginePlan.UnruledGroup));

        var two = EnginePlan.Build(Net(V(Ts, "Tailscale", takesDefault: true, metric: 50), V(Wg, "home", takesDefault: true, metric: 10)), WiFi, [Curl(Eth)], []);
        Assert.Equal("vpn-e5f60718", Selection(two, EnginePlan.UnruledGroup));
    }

    [Fact]
    public void Unruled_Switch_Is_Live_But_A_New_Vpn_Outbound_Reloads()
    {
        var a = EnginePlan.Build(Net(V(Ts, "Tailscale")), WiFi, [Curl(Eth)], []);
        var b = EnginePlan.Build(Net(V(Ts, "Tailscale", takesDefault: true, dns: [])), WiFi, [Curl(Eth)], []);
        Assert.Equal(a.StructureKey, b.StructureKey); // only the selection changed (VPN sets no DNS)
        var none = EnginePlan.Build(Net(), WiFi, [Curl(Eth)], []);
        Assert.NotEqual(none.StructureKey, a.StructureKey);
    }

    [Fact]
    public void Dns_Follows_The_Vpn_While_Unruled_Traffic_Does()
    {
        var toWifi = new DestinationRule { Kind = DestinationKind.DomainSuffix, Value = "bank.example", TargetAdapterId = WiFi };
        var toEth = new DestinationRule { Kind = DestinationKind.DomainSuffix, Value = "cdn.example", TargetAdapterId = Eth };
        var nrpt = new List<NrptNamespace> { new(".corp.example", Ts, ["100.100.100.100"]), new("ts.net", Ts, ["100.100.100.100"]) };
        var plan = EnginePlan.Build(Net(V(Ts, "Tailscale", takesDefault: true, dns: ["100.100.100.100"])), WiFi, [], [], [toWifi, toEth], null, nrpt);
        var dns = plan.Dns!;
        Assert.Equal(["100.100.100.100#vpn-a1b2c3d4"], dns.Nameservers);
        Assert.Equal(["192.168.90.1#net-7d0119de"], dns.DefaultNameservers); // bootstrap stays on the Primary
        // Physical-rule domains keep their network's DNS, the Primary's included (the default is now the VPN's).
        Assert.Contains(dns.Policy, p => p.Domain == "+.bank.example" && p.Servers.SequenceEqual(["192.168.90.1#net-7d0119de"]));
        Assert.Contains(dns.Policy, p => p.Domain == "+.cdn.example" && p.Servers.SequenceEqual(["192.168.42.129#net-62939052"]));
        Assert.Contains(dns.Policy, p => p.Domain == "+.corp.example" && p.Servers.SequenceEqual(["100.100.100.100#vpn-a1b2c3d4"]));
        Assert.Contains(dns.Policy, p => p.Domain == "+.ts.net");
    }

    [Fact]
    public void A_Vpn_Without_Dns_Or_Skipped_Changes_Nothing_But_Nrpt()
    {
        var nrpt = new List<NrptNamespace> { new("corp.example", Ts, ["100.100.100.100"]) };
        var noDns = EnginePlan.Build(Net(V(Ts, "Tailscale", takesDefault: true)), WiFi, [Curl(Eth)], [], null, null, nrpt);
        Assert.Equal(["192.168.90.1#net-7d0119de"], noDns.Dns!.Nameservers);

        var skipped = EnginePlan.Build(Net(V(Ts, "Tailscale", takesDefault: true, skip: true, dns: ["100.100.100.100"])), WiFi, [Curl(Eth)], [], null, null, nrpt);
        Assert.Equal(["192.168.90.1#net-7d0119de"], skipped.Dns!.Nameservers);
        Assert.Contains(skipped.Dns.Policy, p => p.Domain == "+.corp.example");

        var down = EnginePlan.Build(Net(V(Ts, "Tailscale", connected: false)), WiFi, [Curl(Eth)], [], null, null, nrpt);
        Assert.DoesNotContain(down.Dns!.Policy, p => p.Domain == "+.corp.example");
    }

    [Fact]
    public void Unruled_Connections_Through_A_Vpn_Are_Described_As_Following_It()
    {
        var c = new EngineConnection("1", "tcp", "example.com", null, "93.184.216.34", 443, @"C:\x\app.exe", 0, 0, DateTimeOffset.Now,
            ["vpn-a1b2c3d4", "LEO-UNRULED"], "Match", "");
        Assert.Equal("No rule (follows the VPN)", ConnectionDescriber.Rule(c, [], [], []));
        var out1 = new List<OutboundAdapter> { new("vpn-a1b2c3d4", Ts, "Tailscale") };
        Assert.Equal((Ts, "Tailscale"), ConnectionDescriber.Network(c, out1));
    }
}

public sealed class KillSwitchDetectorTests
{
    private static readonly KillSwitchVpn VpnUp = new("vpn", true, true);
    private static readonly KillSwitchVpn VpnDown = new("vpn", false, false);
    private static KillSwitchPhysical P(string id, bool ok, ProbeError e = ProbeError.None) => new(id, ok, e);

    [Fact]
    public void All_Physicals_Denied_With_Vpn_Up_Blocks_Bypass_After_Two_Ticks()
    {
        var clock = new ManualClock();
        var d = new KillSwitchDetector(clock);
        d.Update([VpnUp], [P("wifi", true), P("eth", true)], null);
        clock.Advance(60);
        var denied = new[] { P("wifi", false, ProbeError.Denied), P("eth", false, ProbeError.Denied) };
        Assert.Equal(KillSwitchVerdict.None, d.Update([VpnUp], denied, null).Verdict);
        clock.Advance(5);
        var r = d.Update([VpnUp], denied, null);
        Assert.Equal(KillSwitchVerdict.BlocksBypass, r.Verdict);
        Assert.Equal("vpn", r.VpnId);

        // VPN disconnects: cleared at once.
        Assert.Equal(KillSwitchVerdict.None, d.Update([VpnDown], denied, null).Verdict);
    }

    [Fact]
    public void One_Working_Physical_Means_No_Block()
    {
        var d = new KillSwitchDetector(new ManualClock());
        for (var i = 0; i < 3; i++)
            Assert.Equal(KillSwitchVerdict.None, d.Update([VpnUp], [P("wifi", false, ProbeError.Denied), P("eth", true)], null).Verdict);
    }

    [Fact]
    public void Timeouts_Long_After_The_Vpn_Connected_Are_An_Outage()
    {
        var clock = new ManualClock();
        var d = new KillSwitchDetector(clock);
        d.Update([VpnUp], [P("wifi", true)], null);
        clock.Advance(120);
        for (var i = 0; i < 3; i++, clock.Advance(5))
            Assert.Equal(KillSwitchVerdict.None, d.Update([VpnUp], [P("wifi", false, ProbeError.Timeout)], null).Verdict);
    }

    [Fact]
    public void Failures_Starting_Right_When_The_Vpn_Connects_Block_Bypass()
    {
        var clock = new ManualClock();
        var d = new KillSwitchDetector(clock);
        d.Update([VpnDown], [P("wifi", true)], null);
        clock.Advance(5);
        d.Update([VpnUp], [P("wifi", false, ProbeError.Timeout)], null);
        clock.Advance(5);
        Assert.Equal(KillSwitchVerdict.BlocksBypass, d.Update([VpnUp], [P("wifi", false, ProbeError.Timeout)], null).Verdict);
    }

    [Fact]
    public void Self_Check_Failing_Blocks_Leo_And_Stays_Latched_Until_The_Vpn_Disconnects()
    {
        var d = new KillSwitchDetector(new ManualClock());
        var ok = new[] { P("wifi", true) };
        d.Update([VpnUp], ok, false);
        Assert.Equal(KillSwitchVerdict.BlocksLeo, d.Update([VpnUp], ok, false).Verdict);
        // Standing aside stops the engine: no more self-checks, the verdict holds.
        Assert.Equal(KillSwitchVerdict.BlocksLeo, d.Update([VpnUp], ok, null).Verdict);
        Assert.Equal(KillSwitchVerdict.BlocksLeo, d.Update([VpnUp], ok, true).Verdict);
        Assert.Equal(KillSwitchVerdict.None, d.Update([VpnDown], ok, null).Verdict);
    }

    [Fact]
    public void Vpn_Without_Internet_Never_Gets_A_Verdict()
    {
        var d = new KillSwitchDetector(new ManualClock());
        var privateOnly = new KillSwitchVpn("vpn", true, false);
        for (var i = 0; i < 3; i++)
            Assert.Equal(KillSwitchVerdict.None, d.Update([privateOnly], [P("wifi", false, ProbeError.Denied)], false).Verdict);
    }

    [Fact]
    public void BlockedByVpn_Freezes_Failover()
    {
        var clock = new ManualClock();
        var f = new FailoverController(clock);
        f.Update(new Dictionary<string, HealthState> { ["wifi"] = HealthState.Up, ["eth"] = HealthState.NoInternet });
        clock.Advance(10);
        f.Update(new Dictionary<string, HealthState> { ["wifi"] = HealthState.Up, ["eth"] = HealthState.NoInternet });
        Assert.True(f.IsDemoted("eth"));

        // A VPN's firewall now blocks both: neither gets demoted, and eth stays where it was.
        for (var i = 0; i < 20; i++, clock.Advance(5))
            f.Update(new Dictionary<string, HealthState> { ["wifi"] = HealthState.BlockedByVpn, ["eth"] = HealthState.BlockedByVpn });
        Assert.False(f.IsDemoted("wifi"));
        Assert.True(f.IsDemoted("eth"));
        Assert.False(HealthState.BlockedByVpn.IsFailing());
        Assert.False(HealthState.BlockedByVpn.IsUsable());
    }
}

public sealed class ProbeErrorTests
{
    [Fact]
    public void Classifies_Firewall_Refused_And_Timeouts()
    {
        Assert.Equal(ProbeError.Denied, ProbeErrors.Classify(new SocketException((int)SocketError.AccessDenied)));
        Assert.Equal(ProbeError.Denied, ProbeErrors.Classify(new HttpRequestException("x", new SocketException(10013))));
        Assert.Equal(ProbeError.Refused, ProbeErrors.Classify(new HttpRequestException("x", new SocketException(10061))));
        Assert.Equal(ProbeError.Timeout, ProbeErrors.Classify(new SocketException(10060)));
        Assert.Equal(ProbeError.Timeout, ProbeErrors.Classify(new TaskCanceledException()));
        Assert.Equal(ProbeError.Other, ProbeErrors.Classify(new SocketException((int)SocketError.HostNotFound)));
        Assert.Equal(ProbeError.Other, ProbeErrors.Classify(new InvalidOperationException()));
    }

    [Fact]
    public void Firewall_Owner_Is_Named_From_Provider_Names()
    {
        string[] providers = ["Microsoft Corporation", "Windows Defender Firewall", "Mullvad VPN", "WireGuard"];
        Assert.Equal("Mullvad VPN", Wfp.OwnerFor(providers, "Mullvad", "Mullvad Tunnel"));
        Assert.Equal("WireGuard", Wfp.OwnerFor(providers, "home", "WireGuard Tunnel"));
        Assert.Null(Wfp.OwnerFor(providers, "Tailscale", "Tailscale Tunnel"));
    }

    [Fact]
    public void Vpn_Info_Carries_No_Addresses()
    {
        var info = new VpnInfo("Tailscale Tunnel", true, false, false, false, 3, int.MaxValue, null, false, VpnFirewall.None, null, 1, 2, false, false);
        Assert.DoesNotMatch(new Regex(@"\d+\.\d+\.\d+\.\d+"), info.ToString());
        Assert.False(info.PrivateOnly);
    }
}

/// <summary>Read-only checks against this PC's route table (no service needed). Skipped unless LEO_LIVE_TESTS=1.</summary>
public sealed class LiveRouteTableTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Route_Table_Reads_Like_Windows()
    {
        if (Environment.GetEnvironmentVariable("LEO_LIVE_TESTS") != "1") return;
        var routes = RouteWatcher.ReadRoutes();
        Assert.NotEmpty(routes);
        // Counts and shapes only (the route table isn't printed).
        output.WriteLine($"{routes.Count} routes; IPv4 {routes.Count(r => r.Prefix.AddressFamily == AddressFamily.InterNetwork)}; " +
                         $"defaults {routes.Count(r => r.PrefixLength == 0)}; host routes {routes.Count(r => r.PrefixLength is 32 or 128)}");
        Assert.Contains(routes, r => r.PrefixLength == 0 && r.Prefix.Equals(IPAddress.Any));
        Assert.Contains(routes, r => r.PrefixLength == 8 && r.Prefix.Equals(IPAddress.Parse("127.0.0.0")) || r.PrefixLength == 32);
        Assert.All(routes, r => Assert.InRange(r.PrefixLength, 0, r.Prefix.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
    }
}

public sealed class FailClosedRulesTests
{
    private const string Ts = "{A1B2C3D4-0000-4000-8000-000000000001}";
    private const string Eth = "{62939052-A2ED-4485-87CE-E95E2151550D}";

    private static AppRule Rule(string id, string target, OnTargetDown mode, AppMatchKind kind = AppMatchKind.ExactPath,
        string pattern = @"C:\Apps\app.exe", string? key = null, bool enabled = true) =>
        new() { Id = id, DisplayName = id, Kind = kind, Pattern = pattern, TargetAdapterId = target, OnTargetDown = mode, TargetVpnKey = key, Enabled = enabled };

    [Fact]
    public void Only_Exact_Path_Vpn_Rules_With_Block_Get_A_Firewall_Rule()
    {
        bool IsVpn(string id) => id == Ts;
        var rules = new[]
        {
            Rule("vpnblock0", Ts, OnTargetDown.Block),
            Rule("vpnfallbk", Ts, OnTargetDown.FallbackPrimary),
            Rule("ethblock0", Eth, OnTargetDown.Block),
            Rule("folder000", Ts, OnTargetDown.Block, AppMatchKind.Folder, @"C:\Games"),
            Rule("pattern00", Ts, OnTargetDown.Block, AppMatchKind.NamePattern, "game*.exe"),
            Rule("disabled0", Ts, OnTargetDown.Block, enabled: false),
            Rule("gonevpn00", "{GONE}", OnTargetDown.Block, key: "Corp VPN"), // VPN adapter currently absent: still blocked
            Rule("wireguard", Ts, OnTargetDown.Block, pattern: @"C:\Program Files\WireGuard\wireguard.exe"),
        };
        var wanted = FailClosedRules.Plan(true, rules, IsVpn);
        Assert.Equal(["vpnblock0", "gonevpn00"], wanted.Select(w => w.RuleId));
        Assert.All(wanted, w => Assert.StartsWith("LEO: ", w.Name));
        Assert.Empty(FailClosedRules.Plan(false, rules, IsVpn)); // split tunneling off: no firewall rules
    }

    [Fact]
    public void Firewall_Scope_Is_Public_Addresses_Only()
    {
        var remote = FailClosedRules.PublicRemoteAddresses();
        Assert.Contains("2000::/3", remote);
        Assert.DoesNotContain("192.168.", remote);
        Assert.DoesNotContain("10.0.0.0", remote);
        Assert.Contains("32.0.0.0/3", remote);
    }
}
