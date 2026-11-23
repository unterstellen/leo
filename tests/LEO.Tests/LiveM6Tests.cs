using Leo.Core;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Xunit.Abstractions;

namespace Leo.Tests;

/// <summary>
/// M6 live helper against the installed service, for the dev PC only (LEO.md §21). Skipped unless LEO_LIVE_M6 names an
/// action: "show" (read-only: VPN cards, route targets, split-tunnel state), "add" (rule curl.exe → adapter named in
/// LEO_M6_TARGET, id "m6-live-curl"), "remove" (deletes that rule). Prints counts and states, never destinations.
/// </summary>
public sealed class LiveM6Tests(ITestOutputHelper output)
{
    private const string RuleId = "m6-live-curl";

    [Fact]
    public async Task M6_Live_Action()
    {
        var action = Environment.GetEnvironmentVariable("LEO_LIVE_M6");
        if (string.IsNullOrEmpty(action)) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var stream = await PipeTransport.ConnectClientAsync(ct: cts.Token);
        using var rpc = PipeTransport.CreateRpc(stream);
        var control = rpc.Attach<ILeoControl>();
        rpc.StartListening();
        var hello = await control.HelloAsync(new HelloRequest(LeoInfo.ProtocolVersion, "m6-live", "1"), cts.Token);
        Assert.True(hello.Compatible);

        if (action == "add")
        {
            var name = Environment.GetEnvironmentVariable("LEO_M6_TARGET") ?? "WiFi";
            var nets = await control.GetNetworksAsync(cts.Token);
            var target = nets.Adapters.First(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || a.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
            var mode = Environment.GetEnvironmentVariable("LEO_M6_MODE") == "fallback" ? OnTargetDown.FallbackPrimary : RuleTargets.DefaultOnTargetDown(target.Kind);
            await control.SaveAppRuleAsync(new AppRule
            {
                Id = RuleId, DisplayName = "curl (M6 live test)", Kind = AppMatchKind.ExactPath,
                Pattern = @"C:\Windows\System32\curl.exe", TargetAdapterId = target.Id, OnTargetDown = mode, IncludeChildren = false,
            }, cts.Token);
            output.WriteLine($"rule -> {target.Name} ({mode})");
            await Task.Delay(TimeSpan.FromSeconds(6), cts.Token);
        }
        else if (action == "remove")
        {
            try { await control.DeleteAppRuleAsync(RuleId, cts.Token); output.WriteLine("rule removed"); }
            catch (Exception ex) { output.WriteLine("remove: " + ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
        }
        else if (action == "vpn-skip" || action == "vpn-follow")
        {
            var nets = await control.GetNetworksAsync(cts.Token);
            var vpn = nets.Adapters.First(a => a.Vpn is not null);
            await control.SetVpnSettingsAsync(vpn.Vpn!.Key, action == "vpn-skip", cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
        }

        var snap = await control.GetNetworksAsync(cts.Token);
        foreach (var a in snap.Adapters)
        {
            output.WriteLine($"{a.DisplayName} [{a.Kind}] status={a.Status} health={a.Health} routeTarget={a.IsRouteTarget} primary={a.IsPrimary}" +
                             (a.Vpn is { } v ? $" | vpn key='{v.Key}' connected={v.Connected} internet={v.Internet} default4={v.TakesDefault4} default6={v.TakesDefault6} " +
                                               $"publicRanges={v.PublicRanges} own={(v.OwnTrafficRuled ? v.OwnTrafficVia : "follows Primary")} firewall={v.Firewall} " +
                                               $"dns={v.DnsServers} nrpt={v.NrptRules} skip={v.SkipUnruled} unruledFollows={v.UnruledFollows}" : ""));
        }
        var split = await control.GetSplitTunnelAsync(cts.Token);
        output.WriteLine($"split: enabled={split.Enabled} state={split.State} unruledVia={split.UnruledVia ?? "-"} banner={split.VpnBanner ?? "-"} message={split.Message ?? "-"}");
        foreach (var r in split.Rules) output.WriteLine($"  app rule {r.DisplayName}: now={r.RouteNow} usable={r.TargetUsable} mode={r.OnTargetDown}");
        var test = await control.TestRouteAsync("example.com", null, cts.Token);
        output.WriteLine($"test example.com: {test.RuleText} -> {test.NetworkName}");
    }
}
