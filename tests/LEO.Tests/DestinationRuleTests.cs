using System.Net;
using Leo.Core.Networking;
using Leo.Core.SplitTunnel;
using Xunit;

namespace Leo.Tests;

public sealed class DestinationMatcherTests
{
    private const string Eth = "{62939052-A2ED-4485-87CE-E95E2151550D}";

    private static DestinationRule Rule(DestinationKind kind, string value, string ports = "", DestinationProtocol proto = DestinationProtocol.Any,
        string? app = null, string id = "")
    {
        var r = new DestinationRule { Id = id.Length > 0 ? id : Guid.NewGuid().ToString("N"), Kind = kind, Value = value, Ports = ports, Protocol = proto, AppRuleId = app, TargetAdapterId = Eth };
        Assert.Null(DestinationMatcher.Normalize(r));
        return r;
    }

    private static RouteQuery Q(string host, int port = 443, string? app = null, params string[] ips) =>
        new(host, ips.Select(IPAddress.Parse).ToList(), port, DestinationProtocol.Tcp, app);

    [Theory]
    [InlineData("https://www.YouTube.com/watch?v=1", DestinationKind.DomainSuffix, "www.youtube.com")]
    [InlineData("*.example.com", DestinationKind.DomainSuffix, "example.com")]
    [InlineData(".example.com.", DestinationKind.DomainSuffix, "example.com")]
    [InlineData("example.com:8443", DestinationKind.DomainSuffix, "example.com")]
    [InlineData("bücher.de", DestinationKind.DomainSuffix, "xn--bcher-kva.de")]
    [InlineData("203.0.113.7", DestinationKind.IpRange, "203.0.113.7/32")]
    [InlineData("203.0.113.77/24", DestinationKind.IpRange, "203.0.113.0/24")]
    public void Normalize_Cleans_Up_What_Users_Paste(string input, DestinationKind kind, string expected)
    {
        var r = new DestinationRule { Kind = DestinationKind.DomainSuffix, Value = input };
        Assert.Null(DestinationMatcher.Normalize(r));
        Assert.Equal(kind, r.Kind);
        Assert.Equal(expected, r.Value);
    }

    [Theory]
    [InlineData(DestinationKind.DomainSuffix, "", "")]
    [InlineData(DestinationKind.DomainSuffix, "bad domain.com", "")]
    [InlineData(DestinationKind.DomainSuffix, "a,b.com", "")]
    [InlineData(DestinationKind.IpRange, "192.168.1.0/24", "")]   // private: never tunneled
    [InlineData(DestinationKind.IpRange, "10.1.2.3", "")]
    [InlineData(DestinationKind.IpRange, "2001:db8::/32", "")]     // IPv6 not routed yet
    [InlineData(DestinationKind.IpRange, "300.1.1.1", "")]
    [InlineData(DestinationKind.AnyHost, "", "")]                  // needs ports
    [InlineData(DestinationKind.DomainSuffix, "example.com", "0")]
    [InlineData(DestinationKind.DomainSuffix, "example.com", "70000")]
    [InlineData(DestinationKind.DomainSuffix, "example.com", "200-100")]
    [InlineData(DestinationKind.DomainKeyword, "a(b", "")]
    public void Normalize_Rejects_Bad_Input(DestinationKind kind, string value, string ports) =>
        Assert.NotNull(DestinationMatcher.Normalize(new DestinationRule { Kind = kind, Value = value, Ports = ports }));

    [Fact]
    public void Ports_Are_Normalised()
    {
        var r = Rule(DestinationKind.AnyHost, "", "443 ,80;8000-8100,80");
        Assert.Equal("80, 443, 8000-8100", r.Ports);
    }

    [Theory]
    [InlineData("youtube.com", true)]
    [InlineData("www.youtube.com", true)]
    [InlineData("notyoutube.com", false)]
    [InlineData("youtube.com.evil.net", false)]
    public void Suffix_Matches_Domain_And_Subdomains_Only(string host, bool expected) =>
        Assert.Equal(expected, DestinationMatcher.Matches(Rule(DestinationKind.DomainSuffix, "youtube.com"), Q(host)));

    [Fact]
    public void Exact_Keyword_Ip_And_Ports()
    {
        Assert.True(DestinationMatcher.Matches(Rule(DestinationKind.DomainExact, "www.example.com"), Q("www.example.com")));
        Assert.False(DestinationMatcher.Matches(Rule(DestinationKind.DomainExact, "www.example.com"), Q("a.www.example.com")));
        Assert.True(DestinationMatcher.Matches(Rule(DestinationKind.DomainKeyword, "googlevideo"), Q("r1.googlevideo.com")));
        Assert.True(DestinationMatcher.Matches(Rule(DestinationKind.IpRange, "203.0.113.0/24"), Q("x.test", 443, null, "203.0.113.9")));
        Assert.False(DestinationMatcher.Matches(Rule(DestinationKind.IpRange, "203.0.113.0/24"), Q("x.test", 443, null, "203.0.114.9")));
        Assert.True(DestinationMatcher.Matches(Rule(DestinationKind.DomainSuffix, "example.com", "80, 8000-8100"), Q("example.com", 8050)));
        Assert.False(DestinationMatcher.Matches(Rule(DestinationKind.DomainSuffix, "example.com", "80"), Q("example.com", 443)));
        Assert.False(DestinationMatcher.Matches(Rule(DestinationKind.AnyHost, "", "3478", DestinationProtocol.Udp), Q("x.test", 3478)));
    }

    [Fact]
    public void Precedence_Combo_Then_Specific_Destination_Then_User_Order()
    {
        var app = new AppRule { Id = "chrome", Kind = AppMatchKind.ExactPath, Pattern = @"C:\Chrome\chrome.exe", TargetAdapterId = Eth };
        var any = Rule(DestinationKind.AnyHost, "", "443", id: "any");
        var ip = Rule(DestinationKind.IpRange, "203.0.0.0/16", id: "ip16");
        var ip24 = Rule(DestinationKind.IpRange, "203.0.113.0/24", id: "ip24");
        var kw = Rule(DestinationKind.DomainKeyword, "tube", id: "kw");
        var suffix = Rule(DestinationKind.DomainSuffix, "youtube.com", id: "suffix");
        var deeper = Rule(DestinationKind.DomainSuffix, "m.youtube.com", id: "deeper");
        var withPort = Rule(DestinationKind.DomainSuffix, "youtube.com", "443", id: "suffix443");
        var exact = Rule(DestinationKind.DomainExact, "www.youtube.com", id: "exact");
        var combo = Rule(DestinationKind.DomainSuffix, "youtube.com", app: "chrome", id: "combo");

        var order = DestinationMatcher.Ordered([any, ip, ip24, kw, suffix, deeper, withPort, exact, combo], [app]).Select(r => r.Id);
        Assert.Equal(["combo", "exact", "deeper", "suffix443", "suffix", "kw", "ip24", "ip16", "any"], order);

        // Same specificity: the user's order decides.
        var a = Rule(DestinationKind.DomainSuffix, "aaaa.com", id: "a");
        var b = Rule(DestinationKind.DomainSuffix, "bbbb.com", id: "b");
        Assert.Equal(["b", "a"], DestinationMatcher.Ordered([b, a], []).Select(r => r.Id));
    }

    [Fact]
    public void Decide_Destination_Beats_App_And_Combos_Need_Their_App()
    {
        var chrome = new AppRule { Id = "chrome", DisplayName = "Chrome", Kind = AppMatchKind.ExactPath, Pattern = @"C:\Chrome\chrome.exe", TargetAdapterId = "{WIFI}" };
        var dest = Rule(DestinationKind.DomainSuffix, "example.com", id: "dest");
        var combo = Rule(DestinationKind.DomainSuffix, "youtube.com", app: "chrome", id: "combo");
        var rules = new[] { dest, combo };

        Assert.Equal("dest", DestinationMatcher.Decide(rules, [chrome], Q("example.com", app: @"C:\Chrome\chrome.exe")).Destination?.Id);
        Assert.Equal("combo", DestinationMatcher.Decide(rules, [chrome], Q("www.youtube.com", app: @"C:\Chrome\chrome.exe")).Destination?.Id);
        Assert.Null(DestinationMatcher.Decide(rules, [chrome], Q("www.youtube.com", app: @"C:\Other\x.exe")).Destination);
        var appOnly = DestinationMatcher.Decide(rules, [chrome], Q("other.org", app: @"C:\Chrome\chrome.exe"));
        Assert.Null(appOnly.Destination);
        Assert.Equal("chrome", appOnly.App?.Id);
        Assert.Equal(new RouteDecision(null, null), DestinationMatcher.Decide(rules, [chrome], Q("other.org")));

        chrome.Enabled = false; // a combo with a disabled app is inactive
        Assert.Null(DestinationMatcher.Decide(rules, [chrome], Q("www.youtube.com", app: @"C:\Chrome\chrome.exe")).Destination);
    }

    [Fact]
    public void Disabled_And_Unnormalised_Rules_Are_Ignored()
    {
        var off = Rule(DestinationKind.DomainSuffix, "example.com");
        off.Enabled = false;
        var raw = new DestinationRule { Kind = DestinationKind.DomainSuffix, Value = "Example.COM ", TargetAdapterId = Eth };
        Assert.Empty(DestinationMatcher.Ordered([off, raw], []));
    }

    [Theory]
    [InlineData("youtube.com", "youtube.com", 443, true)]
    [InlineData("https://www.example.com/a/b?c", "www.example.com", 443, false)]
    [InlineData("http://example.com", "example.com", 80, false)]
    [InlineData("example.com:8080", "example.com", 8080, false)]
    [InlineData("203.0.113.7:22", "203.0.113.7", 22, false)]
    [InlineData("[2001:db8::1]:443", "2001:db8::1", 443, false)]
    public void Test_Input_Is_Parsed(string input, string host, int port, bool guessed)
    {
        Assert.True(DestinationMatcher.TryParseTarget(input, out var h, out var p, out var g));
        Assert.Equal((host, port, guessed), (h, p, g));
    }

    [Fact]
    public void Engine_Rule_Lines()
    {
        Assert.Equal("DOMAIN-SUFFIX,youtube.com,G", DestinationMatcher.ToEngineRule(Rule(DestinationKind.DomainSuffix, "youtube.com"), "G"));
        Assert.Equal("DOMAIN,www.x.com,G", DestinationMatcher.ToEngineRule(Rule(DestinationKind.DomainExact, "www.x.com"), "G"));
        Assert.Equal("DOMAIN-KEYWORD,tube,G", DestinationMatcher.ToEngineRule(Rule(DestinationKind.DomainKeyword, "tube"), "G"));
        Assert.Equal("IP-CIDR,203.0.113.0/24,G,no-resolve", DestinationMatcher.ToEngineRule(Rule(DestinationKind.IpRange, "203.0.113.0/24"), "G"));
        Assert.Equal("DST-PORT,3478-3481,G", DestinationMatcher.ToEngineRule(Rule(DestinationKind.AnyHost, "", "3478-3481"), "G"));
        Assert.Equal("AND,((IP-CIDR,203.0.113.0/24,no-resolve),(DST-PORT,80/443),(NETWORK,TCP)),G",
            DestinationMatcher.ToEngineRule(Rule(DestinationKind.IpRange, "203.0.113.0/24", "443,80", DestinationProtocol.Tcp), "G"));
        var app = new AppRule { Kind = AppMatchKind.ExactPath, Pattern = @"C:\Program Files (x86)\App\a.exe" };
        Assert.Equal(@"AND,((PROCESS-PATH-REGEX,(?i)^C:\\Program Files \x28x86\x29\\App\\a\.exe$),(DOMAIN-SUFFIX,x.com)),G",
            DestinationMatcher.ToEngineRule(Rule(DestinationKind.DomainSuffix, "x.com"), "G", AppRuleMatcher.ToRegex(app)));
    }
}

public sealed class EnginePlanDestinationTests
{
    private const string WiFi = "{7D0119DE-B600-4B42-B91B-B95CCD146227}";
    private const string Eth = "{62939052-A2ED-4485-87CE-E95E2151550D}";

    private static List<EngineAdapter> Both(bool ethUp = true, bool ethDemoted = false) =>
    [
        new(WiFi, "WiFi", true, true, false, HealthState.Up, ["192.168.90.1"]),
        new(Eth, "Ethernet", true, ethUp, ethDemoted, HealthState.Up, ["192.168.42.129", "fe80::1"]),
    ];

    private static DestinationRule Dest(string value, string target = Eth, OnTargetDown mode = OnTargetDown.FallbackPrimary,
        DestinationKind kind = DestinationKind.DomainSuffix)
    {
        var r = new DestinationRule { Kind = kind, Value = value, TargetAdapterId = target, OnTargetDown = mode };
        Assert.Null(DestinationMatcher.Normalize(r));
        return r;
    }

    [Fact]
    public void Destination_Rules_Come_Before_App_Lists_And_Get_Their_Groups()
    {
        var steam = new AppRule { Id = "steam", Kind = AppMatchKind.ExactPath, Pattern = @"C:\Steam\steam.exe", TargetAdapterId = WiFi };
        var plan = EnginePlan.Build(Both(), WiFi, [steam], [], [Dest("example.com"), Dest("203.0.113.0/24", kind: DestinationKind.IpRange, mode: OnTargetDown.Block)]);
        Assert.Equal(["DOMAIN-SUFFIX,example.com,to-62939052-fb", "IP-CIDR,203.0.113.0/24,to-62939052-block,no-resolve"], plan.DestinationRules);
        Assert.Contains(plan.Groups, g => g.Name == "to-62939052-block");
        Assert.Contains(new GroupSelection("to-62939052-block", "net-62939052"), plan.Selections);
        Assert.True(plan.Sniff);

        var yaml = plan.ToConfigYaml("127.0.0.1:1");
        var rules = yaml[yaml.IndexOf("rules:", StringComparison.Ordinal)..];
        Assert.True(rules.IndexOf("DOMAIN-SUFFIX,example.com", StringComparison.Ordinal) < rules.IndexOf("RULE-SET,exact-", StringComparison.Ordinal));
        Assert.True(rules.IndexOf("RULE-SET,exact-", StringComparison.Ordinal) < rules.IndexOf("MATCH,", StringComparison.Ordinal));
    }

    [Fact]
    public void Dns_Uses_Primary_Bound_And_Target_Network_For_Ruled_Domains()
    {
        var plan = EnginePlan.Build(Both(), WiFi, [], [], [Dest("example.com"), Dest("www.only.test", kind: DestinationKind.DomainExact)]);
        var dns = Assert.IsType<EngineDns>(plan.Dns);
        Assert.Equal(["192.168.90.1#net-7d0119de"], dns.Nameservers);
        Assert.Equal(["www.only.test", "+.example.com"], dns.Policy.Select(p => p.Domain)); // match order: exact first
        Assert.Equal(["192.168.42.129#net-62939052"], dns.Policy[1].Servers); // IPv6 link-local DNS left out

        var yaml = plan.ToConfigYaml("127.0.0.1:1");
        Assert.Contains("enhanced-mode: redir-host", yaml);
        Assert.Contains("dns-hijack: ['any:53']", yaml);
        Assert.Contains("  nameserver-policy:\n".Replace("\n", Environment.NewLine), yaml);
        Assert.Contains("    '+.example.com':", yaml);
        Assert.Contains("      - '192.168.42.129#net-62939052'", yaml);
        Assert.Contains("  override-destination: false", yaml);
    }

    [Fact]
    public void Custom_Dns_Wins_And_Down_Targets_Get_No_Policy()
    {
        var custom = new List<NetworkDnsSetting>
        {
            new() { AdapterId = WiFi, Servers = ["https://dns.google/dns-query", "1.1.1.1"] },
        };
        var plan = EnginePlan.Build(Both(ethUp: false), WiFi, [], [], [Dest("example.com")], custom);
        var dns = plan.Dns!;
        Assert.Equal(["https://dns.google/dns-query#net-7d0119de", "1.1.1.1#net-7d0119de"], dns.Nameservers);
        Assert.Equal(["1.1.1.1#net-7d0119de"], dns.DefaultNameservers);
        Assert.Empty(dns.Policy); // Ethernet is down: its domains fall back to the Primary, DNS included
    }

    [Fact]
    public void No_Domain_Rules_Means_No_Sniffing_And_Rule_Edits_Need_A_Reload()
    {
        var none = EnginePlan.Build(Both(), WiFi, [], []);
        Assert.False(none.Sniff);
        Assert.Contains("sniffer:" + Environment.NewLine + "  enable: false", none.ToConfigYaml("127.0.0.1:1"));
        var ipOnly = EnginePlan.Build(Both(), WiFi, [], [], [Dest("203.0.113.0/24", kind: DestinationKind.IpRange)]);
        Assert.False(ipOnly.Sniff);
        Assert.NotEqual(none.StructureKey, ipOnly.StructureKey);
        var demoted = EnginePlan.Build(Both(ethDemoted: true), WiFi, [], [], [Dest("203.0.113.0/24", kind: DestinationKind.IpRange)]);
        Assert.Equal(ipOnly.StructureKey, demoted.StructureKey); // health changes stay live
    }

    [Theory]
    [InlineData("1.1.1.1", null)]
    [InlineData("2606:4700:4700::1111", null)]
    [InlineData("https://dns.google/dns-query", null)]
    [InlineData("tls://1.1.1.1", null)]
    [InlineData("dns.google", "x")]
    [InlineData("https://dns.google/dns-query#x", "x")]
    [InlineData("ftp://x", "x")]
    public void Dns_Server_Validation(string server, string? problem) =>
        Assert.Equal(problem is null, EnginePlan.ValidateDnsServer(server) is null);

    [Fact]
    public void Dns_Spec_Formats_Ipv6_With_Brackets() =>
        Assert.Equal("udp://[2606:4700:4700::1111]:53#net-x", EnginePlan.DnsServerSpec("2606:4700:4700::1111", "net-x"));
}
