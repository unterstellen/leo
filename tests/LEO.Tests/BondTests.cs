using System.Diagnostics;
using System.Text.Json;
using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Core.Profiles;
using Leo.Core.SplitTunnel;
using Leo.Core.Stats;
using Leo.Service.SplitTunnel;
using Xunit;

namespace Leo.Tests;

/// <summary>M7 bonding (LEO.md §22): bond validation, engine plan, health check, connections, profiles.</summary>
public sealed class BondTests
{
    private const string WiFi = "{7D0119DE-B600-4B42-B91B-B95CCD146227}";
    private const string Eth = "{62939052-A2ED-4485-87CE-E95E2151550D}";
    private const string Ts = "{A1B2C3D4-0000-4000-8000-000000000001}";
    private const string BondId = "b0a1c2d3e4f5060708090a0b0c0d0e0f";

    private static EngineAdapter Phys(string id, string alias, HealthState health = HealthState.Up, bool up = true, string dns = "192.168.1.1") =>
        new(id, alias, true, up, false, health, [dns]);

    private static EngineAdapter Vpn(string id, bool takesDefault = false, bool skip = false) =>
        new(id, "Tailscale", false, true, false, HealthState.Up, ["100.100.100.100"], IsVpn: true, TakesDefault: takesDefault, SkipUnruled: skip, RouteMetric: 5);

    private static Bond MakeBond(BondMode mode = BondMode.BySite, OnTargetDown allDown = OnTargetDown.FallbackPrimary, int wifiWeight = 1, int ethWeight = 1) => new()
    {
        Id = BondId, Name = "Fast", Mode = mode, OnAllDown = allDown,
        Members = [new BondMember { AdapterId = WiFi, Weight = wifiWeight }, new BondMember { AdapterId = Eth, Weight = ethWeight }],
    };

    private static AppRule Curl(string target, OnTargetDown mode = OnTargetDown.FallbackPrimary) =>
        new() { Id = "curl", Kind = AppMatchKind.ExactPath, Pattern = @"C:\Windows\System32\curl.exe", TargetAdapterId = target, OnTargetDown = mode };

    private static string BondTarget => BondTargets.For(BondId);
    private static string Group => EnginePlan.BondGroupName(BondId);
    private static string Wrapper(OnTargetDown mode = OnTargetDown.FallbackPrimary) => EnginePlan.GroupName(BondTarget, mode);
    private static string Selection(EnginePlan p, string group) => p.Selections.Single(s => s.Group == group).Choice;

    private static EnginePlan Plan(IReadOnlyList<EngineAdapter> adapters, IReadOnlyList<AppRule> rules, Bond? bond = null,
        string? unruled = null, IReadOnlyList<DestinationRule>? dests = null) =>
        EnginePlan.Build(adapters, WiFi, rules, [], dests, null, null, bond is null ? [] : [bond], unruled);

    // ── Validation ───────────────────────────────────────────────────────────

    private static readonly List<BondAdapter> Adapters =
    [
        new(WiFi, "AA-AA", "WiFi", AdapterKind.WiFi, true, true, GatewayMac: "wifi:Zixel"),
        new(Eth, "EE-EE", "Ethernet", AdapterKind.UsbTether, true, true, Metered: true, GatewayMac: "gw:11-22"),
        new(Ts, null, "Tailscale", AdapterKind.Vpn, true, false),
    ];

    [Fact]
    public void Validation_Needs_A_Name_Two_Physical_Members_And_Clamps_Weights()
    {
        Assert.Equal("Give the bond a name.", BondRules.Validate(new Bond { Members = MakeBond().Members }, Adapters, []));
        Assert.Equal("Choose at least two networks for the bond.",
            BondRules.Validate(new Bond { Name = "x", Members = [new BondMember { AdapterId = WiFi }, new BondMember { AdapterId = WiFi.ToLowerInvariant() }] }, Adapters, []));
        Assert.Contains("is a VPN", BondRules.Validate(new Bond { Name = "x", Members = [new() { AdapterId = WiFi }, new() { AdapterId = Ts }] }, Adapters, []));
        Assert.Contains("already a bond", BondRules.Validate(new Bond { Id = "other", Name = "fast", Members = MakeBond().Members }, Adapters, [MakeBond()]));
        var tooMany = new Bond { Name = "x", Members = Enumerable.Range(0, 9).Select(i => new BondMember { AdapterId = "{" + i + "}" }).ToList() };
        Assert.Contains("up to 8", BondRules.Validate(tooMany, Adapters, []));

        var ok = MakeBond(BondMode.Weighted, wifiWeight: 9, ethWeight: 0);
        ok.Name = "  Fast  ";
        Assert.Null(BondRules.Validate(ok, Adapters, [MakeBond()])); // editing itself isn't a duplicate
        Assert.Equal("Fast", ok.Name);
        Assert.Equal([5, 1], ok.Members.Select(m => m.Weight));
        Assert.Equal(["AA-AA", "EE-EE"], ok.Members.Select(m => m.Mac)); // MAC remembered for re-plugged tethers
    }

    [Fact]
    public void Vpn_Client_Can_Not_Go_Into_A_Bond()
    {
        var wg = new AppRule { Kind = AppMatchKind.ExactPath, Pattern = @"C:\Program Files\WireGuard\wireguard.exe", TargetAdapterId = BondTarget };
        Assert.Contains("one connection", RuleTargets.CheckBond(wg));
        Assert.Null(RuleTargets.CheckBond(Curl(BondTarget)));
        Assert.Null(RuleTargets.CheckBond(null));
    }

    [Fact]
    public void Members_Resolve_By_Mac_And_Hints()
    {
        var bond = MakeBond();
        bond.Members[1].AdapterId = "{OLD-TETHER-GUID}";
        bond.Members[1].Mac = "EE-EE";
        Assert.Equal([WiFi, Eth], BondRules.Usable(bond, Adapters).Select(a => a.Id));
        Assert.Null(BondRules.SameRouterHint(bond, Adapters));
        var same = Adapters.Select(a => a.Id == Eth ? a with { GatewayMac = "wifi:Zixel" } : a).ToList();
        Assert.Contains("use the same router", BondRules.SameRouterHint(MakeBond(), same));

        Assert.Equal(WiFi, BondRules.DnsMember(MakeBond(), Adapters, WiFi));            // Primary is a usable member
        Assert.Equal(WiFi, BondRules.DnsMember(MakeBond(), Adapters, "{SOMETHING-ELSE}")); // else first usable in order
        var wifiDown = Adapters.Select(a => a.Id == WiFi ? a with { Usable = false } : a).ToList();
        Assert.Equal(Eth, BondRules.DnsMember(MakeBond(), wifiDown, WiFi));
        Assert.Equal("bond:" + BondId, BondTarget);
        Assert.Equal(BondId, BondTargets.BondId(BondTarget));
        Assert.False(BondTargets.IsBond(WiFi));
    }

    // ── Engine plan ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BondMode.BySite, "load-balance", "consistent-hashing")]
    [InlineData(BondMode.Weighted, "load-balance", "consistent-hashing")]
    [InlineData(BondMode.EveryConnection, "load-balance", "round-robin")]
    [InlineData(BondMode.Fastest, "url-test", null)]
    public void Modes_Map_To_Engine_Groups_With_Leos_Own_Health_Check(BondMode mode, string type, string? strategy)
    {
        var plan = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet")], [Curl(BondTarget)], MakeBond(mode));
        var yaml = plan.ToConfigYaml("127.0.0.1:9");
        var block = GroupBlock(yaml, Group);
        Assert.Contains($"type: {type}", block);
        if (strategy is null) Assert.DoesNotContain("strategy:", block); else Assert.Contains($"strategy: {strategy}", block);
        if (mode == BondMode.Fastest) Assert.Contains("tolerance: 50", block);
        Assert.Contains("url: 'http://cp.cloudflare.com/generate_204'", block);
        Assert.Contains("expected-status: '204'", block);
        Assert.Contains("lazy: true", block);
        Assert.Contains("timeout: 5000", block);
        Assert.DoesNotContain("gstatic", yaml);
    }

    [Fact]
    public void Every_Load_Balance_Or_Url_Test_Group_Has_An_Explicit_Url()
    {
        var plan = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet")], [Curl(BondTarget)], MakeBond(BondMode.Fastest));
        var yaml = plan.ToConfigYaml("127.0.0.1:9");
        var groups = yaml.Split("\n  - name: ").Skip(1).Where(g => g.Contains("type: load-balance") || g.Contains("type: url-test")).ToList();
        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.Contains("    url: ", g));
    }

    [Fact]
    public void Health_Check_Uses_The_First_Status_Probe_From_Settings()
    {
        Assert.Equal(new BondHealthCheck("http://cp.cloudflare.com/generate_204", "204"),
            BondHealthCheck.From(new HealthSettings().ProbeTargets.Select(t => (t.Url, t.Expect))));
        Assert.Equal(new BondHealthCheck("https://probe.example/ok", "200"),
            BondHealthCheck.From([("http://www.msftconnecttest.com/connecttest.txt", "Microsoft Connect Test"), ("https://probe.example/ok", "200")]));
        Assert.Equal(BondHealthCheck.Default, BondHealthCheck.From([("http://x/", "some body text")]));
    }

    [Fact]
    public void Weighted_Repeats_Members_And_Only_Connected_Members_Are_Listed()
    {
        var weighted = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet")], [Curl(BondTarget)], MakeBond(BondMode.Weighted, wifiWeight: 3, ethWeight: 1));
        Assert.Equal(["net-7d0119de", "net-7d0119de", "net-7d0119de", "net-62939052"], weighted.Bonds.Single().Members);

        var bySite = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet")], [Curl(BondTarget)], MakeBond(BondMode.BySite, wifiWeight: 3));
        Assert.Equal(["net-7d0119de", "net-62939052"], bySite.Bonds.Single().Members); // By site ignores weights

        var every = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet")], [Curl(BondTarget)], MakeBond(BondMode.EveryConnection, wifiWeight: 4));
        Assert.Equal(5, every.Bonds.Single().Members.Count); // round-robin over the repeated list = 4 : 1 per connection

        var unplugged = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet", up: false)], [Curl(BondTarget)], MakeBond());
        Assert.Equal(["net-7d0119de"], unplugged.Bonds.Single().Members);
    }

    [Fact]
    public void Only_Referenced_Bonds_Go_Into_The_Engine()
    {
        var unused = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet")], [Curl(Eth)], MakeBond());
        Assert.Empty(unused.Bonds);
        Assert.DoesNotContain("load-balance", unused.ToConfigYaml("127.0.0.1:9"));

        var disabled = Curl(BondTarget);
        disabled.Enabled = false;
        Assert.Empty(Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet")], [disabled, with_id("x")], MakeBond()).Bonds);
    }

    private static AppRule with_id(string id) => new() { Id = id, Kind = AppMatchKind.ExactPath, Pattern = @"C:\x\" + id + ".exe", TargetAdapterId = Eth };

    [Fact]
    public void Wrapper_Switches_Away_Live_When_No_Member_Is_Usable()
    {
        var ok = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet")], [Curl(BondTarget)], MakeBond());
        Assert.Equal(Group, Selection(ok, Wrapper()));
        Assert.Equal([Group, EnginePlan.PrimaryGroup], ok.Groups.Single(g => g.Name == Wrapper()).Members);

        var oneDown = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet", HealthState.NoInternet)], [Curl(BondTarget)], MakeBond());
        Assert.Equal(Group, Selection(oneDown, Wrapper()));
        Assert.Equal(ok.StructureKey, oneDown.StructureKey); // health is live, not a reload

        var allDown = Plan([Phys(WiFi, "WiFi", HealthState.NoInternet), Phys(Eth, "Ethernet", HealthState.Captive)], [Curl(BondTarget)], MakeBond());
        Assert.Equal(EnginePlan.PrimaryGroup, Selection(allDown, Wrapper()));
        Assert.Equal(ok.StructureKey, allDown.StructureKey);

        var block = Plan([Phys(WiFi, "WiFi", HealthState.NoInternet), Phys(Eth, "Ethernet", HealthState.NoInternet)],
            [Curl(BondTarget, OnTargetDown.Block)], MakeBond());
        Assert.Equal(EnginePlan.Reject, Selection(block, Wrapper(OnTargetDown.Block)));

        // Nothing connected: no bond group at all, the wrapper only has its alternative.
        var none = Plan([Phys(WiFi, "WiFi", up: false), Phys(Eth, "Ethernet", up: false)], [Curl(BondTarget)], MakeBond());
        Assert.Empty(none.Bonds);
        Assert.Equal([EnginePlan.PrimaryGroup], none.Groups.Single(g => g.Name == Wrapper()).Members);

        // Member up/down and bond edits are reloads.
        Assert.NotEqual(ok.StructureKey, Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet", up: false)], [Curl(BondTarget)], MakeBond()).StructureKey);
        Assert.NotEqual(ok.StructureKey, Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet")], [Curl(BondTarget)], MakeBond(BondMode.EveryConnection)).StructureKey);
    }

    [Fact]
    public void Unruled_Order_Is_Vpn_Then_Bond_Then_Primary()
    {
        var net = new List<EngineAdapter> { Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet") };
        var none = Plan(net, [], MakeBond());
        Assert.Equal(EnginePlan.PrimaryGroup, Selection(none, EnginePlan.UnruledGroup));
        Assert.Null(none.UnruledBondId);

        var bond = Plan(net, [], MakeBond(), unruled: BondId);
        Assert.Equal(Group, Selection(bond, EnginePlan.UnruledGroup));
        Assert.Equal([Group, EnginePlan.PrimaryGroup], bond.Groups.Single(g => g.Name == EnginePlan.UnruledGroup).Members);
        Assert.Equal(BondId, bond.UnruledBondId);
        Assert.Contains("MATCH,LEO-UNRULED", bond.ToConfigYaml("127.0.0.1:9"));

        var vpnWins = Plan([.. net, Vpn(Ts, takesDefault: true)], [], MakeBond(), unruled: BondId);
        Assert.Equal("vpn-a1b2c3d4", Selection(vpnWins, EnginePlan.UnruledGroup));
        Assert.Null(vpnWins.UnruledBondId);

        var skipped = Plan([.. net, Vpn(Ts, takesDefault: true, skip: true)], [], MakeBond(), unruled: BondId);
        Assert.Equal(Group, Selection(skipped, EnginePlan.UnruledGroup));

        var allDown = Plan([Phys(WiFi, "WiFi", HealthState.NoInternet), Phys(Eth, "Ethernet", HealthState.NoInternet)], [], MakeBond(), unruled: BondId);
        Assert.Equal(EnginePlan.PrimaryGroup, Selection(allDown, EnginePlan.UnruledGroup));

        var missing = Plan(net, [], null, unruled: BondId); // bond deleted
        Assert.Equal(EnginePlan.PrimaryGroup, Selection(missing, EnginePlan.UnruledGroup));
    }

    [Fact]
    public void Vpns_Are_Never_Bond_Members_In_The_Engine()
    {
        var bond = MakeBond();
        bond.Members.Add(new BondMember { AdapterId = Ts }); // e.g. an old config
        var plan = Plan([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet"), Vpn(Ts)], [Curl(BondTarget)], bond);
        Assert.DoesNotContain(plan.Bonds.Single().Members, m => m.StartsWith("vpn-", StringComparison.Ordinal));
    }

    [Fact]
    public void Bond_Domains_Resolve_Through_One_Member()
    {
        var dest = new DestinationRule { Kind = DestinationKind.DomainSuffix, Value = "steamcontent.com", TargetAdapterId = BondTarget };
        Assert.Null(DestinationMatcher.Normalize(dest));
        // Primary (WiFi) is a usable member: its DNS is the default already, so no policy entry is needed.
        var viaPrimary = Plan([Phys(WiFi, "WiFi", dns: "192.168.1.1"), Phys(Eth, "Ethernet", dns: "10.0.0.1")], [], MakeBond(), dests: [dest]);
        Assert.DoesNotContain(viaPrimary.Dns!.Policy, p => p.Domain == "+.steamcontent.com");
        // WiFi has no internet: the first usable member (Ethernet) answers, bound to it.
        var viaEth = Plan([Phys(WiFi, "WiFi", HealthState.NoInternet, dns: "192.168.1.1"), Phys(Eth, "Ethernet", dns: "10.0.0.1")], [], MakeBond(), dests: [dest]);
        Assert.Equal(["10.0.0.1#net-62939052"], viaEth.Dns!.Policy.Single(p => p.Domain == "+.steamcontent.com").Servers);
        Assert.Equal([EnginePlan.BondGroupName(BondId)], viaEth.Bonds.Select(b => EnginePlan.BondGroupName(b.BondId)));
    }

    private static string GroupBlock(string yaml, string group)
    {
        var start = yaml.IndexOf($"  - name: '{group}'", StringComparison.Ordinal);
        Assert.True(start >= 0, $"group {group} missing");
        var next = yaml.IndexOf("\n  - name: ", start + 5, StringComparison.Ordinal);
        var end = yaml.IndexOf("\nrule-providers:", start, StringComparison.Ordinal);
        return yaml[start..(next >= 0 && next < end ? next : end)];
    }

    // ── Connections, engine API, profiles ────────────────────────────────────

    private static EngineConnection Conn(string rule, params string[] chains) =>
        new("1", "tcp", "example.com", null, "93.184.215.14", 443, @"C:\x\curl.exe", 0, 0, DateTimeOffset.Now, chains, rule, "");

    [Fact]
    public void Bonded_Connections_Are_Attributed_To_The_Member_Not_A_Fallback()
    {
        IReadOnlyList<OutboundAdapter> outbounds = [new("net-7d0119de", WiFi, "WiFi"), new("net-62939052", Eth, "Ethernet")];
        var names = new Dictionary<string, string> { [Group] = "Fast" };
        Assert.Equal((Eth, "Ethernet via bond 'Fast'"),
            ConnectionDescriber.Network(Conn("ProcessPath", "net-62939052", Group, Wrapper()), outbounds, names));
        Assert.Equal((WiFi, "WiFi via a bond"), ConnectionDescriber.Network(Conn("ProcessPath", "net-7d0119de", Group, Wrapper()), outbounds));
        Assert.Equal((WiFi, "WiFi (fallback)"), ConnectionDescriber.Network(Conn("ProcessPath", "net-7d0119de", EnginePlan.PrimaryGroup, Wrapper()), outbounds));
        Assert.Equal("No rule (everything else uses the bond)", ConnectionDescriber.Rule(Conn("Match", "net-7d0119de", Group, EnginePlan.UnruledGroup), [], [], []));
    }

    [Fact]
    public void Engine_Alive_Flag_Prefers_The_Health_Check_Url()
    {
        using var doc = JsonDocument.Parse("""{"alive":true,"extra":{"http://cp.cloudflare.com/generate_204":{"alive":false,"history":[]}}}""");
        Assert.False(MihomoEngine.ParseAlive(doc.RootElement, "http://cp.cloudflare.com/generate_204"));
        Assert.True(MihomoEngine.ParseAlive(doc.RootElement, "http://other/"));
        using var none = JsonDocument.Parse("{}");
        Assert.Null(MihomoEngine.ParseAlive(none.RootElement, "http://other/"));
    }

    [Fact]
    public void Bonds_Are_Machine_Wide_And_Survive_Profile_Switches()
    {
        var c = new LeoConfig();
        c.Profiles = [new Profile { Id = "travel", Name = "Travel" }, new Profile { Id = Profile.DefaultId, Name = "Default" }];
        c.ProfileState.ActiveProfileId = Profile.DefaultId;
        c.SplitTunnel.Bonds = [MakeBond()];
        c.SplitTunnel.UnruledBondId = BondId;
        c.SplitTunnel.AppRules = [Curl(BondTarget)];

        Assert.True(ProfileOps.Switch(c, "travel", manual: true));
        Assert.Single(c.SplitTunnel.Bonds);
        Assert.Equal(BondId, c.SplitTunnel.UnruledBondId);
        Assert.Empty(c.SplitTunnel.AppRules); // rules are per profile

        Assert.True(ProfileOps.Switch(c, Profile.DefaultId, manual: true));
        Assert.Equal(BondTarget, Assert.Single(c.SplitTunnel.AppRules).TargetAdapterId);

        // Old config files without bonds load with none.
        var old = JsonSerializer.Deserialize<SplitTunnelSettings>("""{"Enabled":true,"AppRules":[]}""")!;
        Assert.Empty(old.Bonds);
        Assert.Null(old.UnruledBondId);
    }

    // ── Real engine ──────────────────────────────────────────────────────────

    private static string? FindEngine()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var exe = Path.Combine(dir.FullName, "artifacts", "engine", "mihomo.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    [Fact]
    public async Task Bond_Configs_Pass_The_Engines_Own_Check()
    {
        var engine = FindEngine();
        if (engine is null) return;
        const string Bond2 = "c0ffee00c0ffee00c0ffee00c0ffee00";
        var bonds = new List<Bond>
        {
            MakeBond(BondMode.Weighted, wifiWeight: 3),
            new() { Id = Bond2, Name = "Quick", Mode = BondMode.Fastest, Members = MakeBond().Members },
        };
        var dests = new List<DestinationRule>
        {
            new() { Kind = DestinationKind.DomainSuffix, Value = "steamcontent.com", TargetAdapterId = BondTargets.For(Bond2), OnTargetDown = OnTargetDown.Block },
        };
        foreach (var d in dests) Assert.Null(DestinationMatcher.Normalize(d));
        var wg = new AppRule { Id = "wg", Kind = AppMatchKind.ExactPath, Pattern = @"C:\Program Files\WireGuard\wireguard.exe", TargetAdapterId = Eth };
        foreach (var mode in new[] { BondMode.BySite, BondMode.EveryConnection })
        {
            bonds[0].Mode = mode == BondMode.BySite ? BondMode.Weighted : mode;
            var plan = EnginePlan.Build([Phys(WiFi, "WiFi"), Phys(Eth, "Ethernet"), Vpn(Ts, takesDefault: true, skip: true)], WiFi,
                [Curl(BondTarget), wg], [], dests, null, null, bonds, BondId);
            Assert.Equal(2, plan.Bonds.Count);
            Assert.NotEmpty(plan.GuardRules);
            await CheckAsync(engine, plan);
        }
    }

    private static async Task CheckAsync(string engine, EnginePlan plan)
    {
        var dir = Path.Combine(Path.GetTempPath(), "leo-bondcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "rules"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.yaml"), plan.ToConfigYaml("127.0.0.1:9"));
            foreach (var (name, content) in plan.ProviderFiles()) File.WriteAllText(Path.Combine(dir, "rules", name + ".yaml"), content);
            var psi = new ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "-t", "-d", dir, "-f", Path.Combine(dir, "config.yaml") }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await p.WaitForExitAsync(cts.Token);
            var text = await output + await error;
            Assert.True(p.ExitCode == 0, text);
            Assert.Contains("test is successful", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
