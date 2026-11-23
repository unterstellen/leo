using System.Diagnostics;
using Leo.Core.Networking;
using Leo.Core.SplitTunnel;
using Xunit;

namespace Leo.Tests;

/// <summary>
/// Runs the real engine's config check (<c>mihomo -t</c>) on a generated config with every rule kind. Uses the dev
/// engine from <c>tools\get-engine.ps1</c> (artifacts\engine); passes trivially where it isn't present (CI).
/// </summary>
public sealed class EngineConfigCheckTests
{
    private const string WiFi = "{7D0119DE-B600-4B42-B91B-B95CCD146227}";
    private const string Eth = "{62939052-A2ED-4485-87CE-E95E2151550D}";

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
    public async Task Generated_Config_Passes_The_Engines_Own_Check()
    {
        var engine = FindEngine();
        if (engine is null) return;

        var chrome = new AppRule { Id = "chrome", Kind = AppMatchKind.ExactPath, Pattern = @"C:\Program Files (x86)\Google\Chrome\chrome.exe", TargetAdapterId = WiFi };
        var games = new AppRule { Id = "games", Kind = AppMatchKind.Folder, Pattern = @"C:\Games", TargetAdapterId = Eth, OnTargetDown = OnTargetDown.Block };
        var dests = new List<DestinationRule>
        {
            new() { Kind = DestinationKind.DomainSuffix, Value = "youtube.com", TargetAdapterId = Eth },
            new() { Kind = DestinationKind.DomainExact, Value = "www.example.com", TargetAdapterId = Eth, OnTargetDown = OnTargetDown.Block },
            new() { Kind = DestinationKind.DomainKeyword, Value = "googlevideo", TargetAdapterId = Eth },
            new() { Kind = DestinationKind.IpRange, Value = "203.0.113.0/24", Ports = "443, 8000-8100", Protocol = DestinationProtocol.Tcp, TargetAdapterId = Eth },
            new() { Kind = DestinationKind.AnyHost, Ports = "3478-3481", Protocol = DestinationProtocol.Udp, TargetAdapterId = Eth },
            new() { Kind = DestinationKind.DomainSuffix, Value = "twitch.tv", AppRuleId = "chrome", TargetAdapterId = Eth },
        };
        foreach (var d in dests) Assert.Null(DestinationMatcher.Normalize(d));
        List<EngineAdapter> adapters =
        [
            new(WiFi, "WiFi", true, true, false, HealthState.Up, ["192.168.90.1"]),
            new(Eth, "Ethernet", true, true, false, HealthState.Up, ["192.168.42.129"]),
        ];
        var custom = new List<NetworkDnsSetting> { new() { AdapterId = Eth, Servers = ["https://dns.google/dns-query", "1.1.1.1"] } };
        var plan = EnginePlan.Build(adapters, WiFi, [chrome, games], [new ChildProgram(@"C:\Games\x\game.exe", "games")], dests, custom);
        Assert.Equal(6, plan.DestinationRules.Count);

        var dir = Path.Combine(Path.GetTempPath(), "leo-enginecheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "rules"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.yaml"), plan.ToConfigYaml("127.0.0.1:9", "secret"));
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
