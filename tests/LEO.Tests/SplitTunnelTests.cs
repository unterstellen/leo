using System.Text.RegularExpressions;
using Leo.Core.Networking;
using Leo.Core.SplitTunnel;

namespace Leo.Tests;

public sealed class AppRuleMatcherTests
{
    private static AppRule R(AppMatchKind kind, string pattern, string target = "{T}") =>
        new() { Kind = kind, Pattern = pattern, TargetAdapterId = target };

    [Theory]
    [InlineData(@"C:\Program Files\Steam\steam.exe", true)]
    [InlineData(@"c:\program files\steam\STEAM.EXE", true)]
    [InlineData(@"C:\Program Files\Steam\steamwebhelper.exe", false)]
    [InlineData(@"D:\Program Files\Steam\steam.exe", false)]
    public void Exact_Path_Is_Case_Insensitive_And_Exact(string path, bool expected) =>
        Assert.Equal(expected, AppRuleMatcher.Matches(R(AppMatchKind.ExactPath, @"C:\Program Files\Steam\steam.exe"), path));

    [Theory]
    [InlineData(@"C:\Games\Doom\doom.exe", true)]
    [InlineData(@"C:\Games\a\b\c.exe", true)]
    [InlineData(@"C:\GamesExtra\x.exe", false)]
    [InlineData(@"C:\Games", false)]
    public void Folder_Covers_Subfolders_Only(string path, bool expected) =>
        Assert.Equal(expected, AppRuleMatcher.Matches(R(AppMatchKind.Folder, @"C:\Games\"), path));

    [Theory]
    [InlineData(@"C:\Apps\chrome.exe", true)]
    [InlineData(@"C:\x\new_chrome_proxy.exe", true)]
    [InlineData(@"C:\chrome\other.exe", false)]
    public void Name_Pattern_Matches_File_Name_Only(string path, bool expected) =>
        Assert.Equal(expected, AppRuleMatcher.Matches(R(AppMatchKind.NamePattern, "*chrome*.exe"), path));

    [Fact]
    public void Special_Characters_Are_Escaped_And_Commas_Encoded()
    {
        var rule = R(AppMatchKind.ExactPath, @"C:\My Apps (x86)\a+b,c [1]\app.exe");
        var regex = AppRuleMatcher.ToRegex(rule);
        Assert.DoesNotContain(",", regex);
        Assert.Matches(new Regex(regex), @"C:\My Apps (x86)\a+b,c [1]\app.exe");
        Assert.DoesNotMatch(new Regex(regex), @"C:\My Apps (x86)\aab,c [1]\app.exe");
    }

    [Fact]
    public void Best_Rule_Prefers_Exact_Then_Deeper_Folder_Then_Name()
    {
        var name = R(AppMatchKind.NamePattern, "*.exe", "{N}");
        var folder = R(AppMatchKind.Folder, @"C:\Games", "{F}");
        var deeper = R(AppMatchKind.Folder, @"C:\Games\Doom", "{D}");
        var exact = R(AppMatchKind.ExactPath, @"C:\Games\Doom\doom.exe", "{E}");

        Assert.Same(exact, AppRuleMatcher.FindBest([name, folder, deeper, exact], @"C:\Games\Doom\doom.exe"));
        Assert.Same(deeper, AppRuleMatcher.FindBest([name, folder, deeper], @"C:\Games\Doom\doom.exe"));
        Assert.Same(folder, AppRuleMatcher.FindBest([name, folder], @"C:\Games\x.exe"));
        Assert.Same(name, AppRuleMatcher.FindBest([name], @"C:\x.exe"));
        Assert.Null(AppRuleMatcher.FindBest([exact], @"C:\other.exe"));
    }
}

public sealed class EnginePlanTests
{
    private const string WiFi = "{7D0119DE-B600-4B42-B91B-B95CCD146227}";
    private const string Eth = "{62939052-A2ED-4485-87CE-E95E2151550D}";

    private static List<EngineAdapter> Both(bool ethUp = true, bool ethDemoted = false, HealthState ethHealth = HealthState.Up) =>
    [
        new(WiFi, "WiFi", true, true, false, HealthState.Up),
        new(Eth, "Ethernet", true, ethUp, ethDemoted, ethHealth),
        new("{TAIL}", "Tailscale", false, true, false, HealthState.Unknown),
    ];

    private static AppRule Steam(OnTargetDown mode = OnTargetDown.FallbackPrimary) => new()
    {
        Id = "steam", DisplayName = "Steam", Kind = AppMatchKind.ExactPath, Pattern = @"C:\Steam\steam.exe",
        TargetAdapterId = Eth, OnTargetDown = mode,
    };

    [Fact]
    public void Outbounds_Only_For_Connected_Candidates_And_Primary_Follows_Leo()
    {
        var plan = EnginePlan.Build(Both(), WiFi, [], []);
        Assert.Equal(["net-62939052", "net-7d0119de"], plan.Outbounds.Select(o => o.Name));
        Assert.Contains(new GroupSelection(EnginePlan.PrimaryGroup, "net-7d0119de"), plan.Selections);
        Assert.Empty(plan.Providers);
    }

    [Fact]
    public void Rule_Target_Is_Selected_While_Healthy()
    {
        var plan = EnginePlan.Build(Both(), WiFi, [Steam()], []);
        Assert.Contains(new GroupSelection("to-62939052-fb", "net-62939052"), plan.Selections);
        Assert.Contains(plan.Providers, p => p.Name == "exact-to-62939052-fb" && p.Group == "to-62939052-fb");
    }

    [Theory]
    [InlineData(OnTargetDown.FallbackPrimary, EnginePlan.PrimaryGroup)]
    [InlineData(OnTargetDown.Block, EnginePlan.Reject)]
    public void Unhealthy_Target_Switches_To_Fallback_Or_Block(OnTargetDown mode, string expected)
    {
        var group = EnginePlan.GroupName(Eth, mode);
        Assert.Contains(new GroupSelection(group, expected), EnginePlan.Build(Both(ethDemoted: true), WiFi, [Steam(mode)], []).Selections);
        Assert.Contains(new GroupSelection(group, expected), EnginePlan.Build(Both(ethHealth: HealthState.NoInternet), WiFi, [Steam(mode)], []).Selections);
        Assert.Contains(new GroupSelection(group, expected), EnginePlan.Build(Both(ethUp: false), WiFi, [Steam(mode)], []).Selections);
    }

    [Fact]
    public void Health_Changes_Do_Not_Need_A_Reload_But_New_Adapters_Do()
    {
        var healthy = EnginePlan.Build(Both(), WiFi, [Steam()], []);
        var demoted = EnginePlan.Build(Both(ethDemoted: true), WiFi, [Steam()], []);
        var gone = EnginePlan.Build(Both(ethUp: false), WiFi, [Steam()], []);
        Assert.Equal(healthy.StructureKey, demoted.StructureKey);
        Assert.NotEqual(healthy.StructureKey, gone.StructureKey);
    }

    [Fact]
    public void Precedence_Order_Is_Exact_Child_Folder_Name()
    {
        var rules = new List<AppRule>
        {
            new() { Id = "n", Kind = AppMatchKind.NamePattern, Pattern = "*.exe", TargetAdapterId = WiFi },
            new() { Id = "f", Kind = AppMatchKind.Folder, Pattern = @"C:\Games", TargetAdapterId = Eth },
            Steam(),
        };
        var plan = EnginePlan.Build(Both(), WiFi, rules, [new ChildProgram(@"C:\Games\game.exe", "steam")]);
        var tiers = plan.Providers.Select(p => p.Name.Split('-')[0]).ToList();
        Assert.Equal(["exact", "child", "folder", "name"], tiers);
    }

    [Fact]
    public void Disabled_Rules_And_Orphan_Children_Are_Ignored()
    {
        var off = Steam();
        off.Enabled = false;
        var plan = EnginePlan.Build(Both(), WiFi, [off], [new ChildProgram(@"C:\x.exe", "steam")]);
        Assert.Empty(plan.Providers);
        Assert.Single(plan.Groups); // only LEO-PRIMARY
    }

    [Fact]
    public void Yaml_Contains_Bound_Outbounds_Excludes_And_Match()
    {
        var plan = EnginePlan.Build(Both(), WiFi, [Steam()], []);
        var yaml = plan.ToConfigYaml("127.0.0.1:19190", "s3cret");
        Assert.Contains("interface-name: 'Ethernet'", yaml);
        Assert.Contains("auto-detect-interface: false", yaml);
        Assert.Contains("- '192.168.0.0/16'", yaml);
        Assert.Contains("- 'RULE-SET,exact-to-62939052-fb,to-62939052-fb'", yaml);
        Assert.EndsWith("- 'MATCH,LEO-PRIMARY'" + Environment.NewLine, yaml);
        var file = plan.ProviderFiles()["exact-to-62939052-fb"];
        Assert.Contains(@"PROCESS-PATH-REGEX,(?i)^C:\\Steam\\steam\.exe$", file);
    }
}
