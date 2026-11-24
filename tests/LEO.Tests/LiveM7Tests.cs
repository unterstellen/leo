using Leo.Core;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Xunit.Abstractions;

namespace Leo.Tests;

/// <summary>
/// M7 live helper against the installed service, for the dev PC only (LEO.md §23). Skipped unless LEO_LIVE_M7 names an
/// action: "show" (read-only: bonds, rules, engine state), "add" (bond "M7 live" of the adapters named in LEO_M7_MEMBERS,
/// default "WiFi,Ethernet", mode LEO_M7_MODE = BySite|Weighted|EveryConnection|Fastest, plus rule curl.exe → bond),
/// "unruled" / "unruled-off" (everything else uses the bond / the Primary), "remove" (deletes the rule and the bond).
/// Prints counts and states, never destinations.
/// </summary>
public sealed class LiveM7Tests(ITestOutputHelper output)
{
    private const string RuleId = "m7-live-curl";
    private const string BondId = "m7live000000000000000000000000000";

    [Fact]
    public async Task M7_Live_Action()
    {
        var action = Environment.GetEnvironmentVariable("LEO_LIVE_M7");
        if (string.IsNullOrEmpty(action)) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var stream = await PipeTransport.ConnectClientAsync(ct: cts.Token);
        using var rpc = PipeTransport.CreateRpc(stream);
        var control = rpc.Attach<ILeoControl>();
        rpc.StartListening();
        var hello = await control.HelloAsync(new HelloRequest(LeoInfo.ProtocolVersion, "m7-live", "1"), cts.Token);
        Assert.True(hello.Compatible);

        if (action == "add")
        {
            var names = (Environment.GetEnvironmentVariable("LEO_M7_MEMBERS") ?? "WiFi,Ethernet").Split(',', StringSplitOptions.TrimEntries);
            var weights = (Environment.GetEnvironmentVariable("LEO_M7_WEIGHTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var mode = Enum.Parse<BondMode>(Environment.GetEnvironmentVariable("LEO_M7_MODE") ?? "BySite");
            var nets = await control.GetNetworksAsync(cts.Token);
            var members = names.Select((n, i) => new BondMember
            {
                AdapterId = nets.Adapters.First(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase) || a.DisplayName.Equals(n, StringComparison.OrdinalIgnoreCase)).Id,
                Weight = i < weights.Length ? int.Parse(weights[i]) : 1,
            }).ToList();
            await control.SaveBondAsync(new Bond { Id = BondId, Name = "M7 live", Mode = mode, Members = members }, cts.Token);
            await control.SaveAppRuleAsync(new AppRule
            {
                Id = RuleId, DisplayName = "curl (M7 live test)", Kind = AppMatchKind.ExactPath,
                Pattern = @"C:\Windows\System32\curl.exe", TargetAdapterId = BondTargets.For(BondId), IncludeChildren = false,
            }, cts.Token);
            output.WriteLine($"bond {mode} of {string.Join(" + ", names)}; rule curl -> bond");
            await Task.Delay(TimeSpan.FromSeconds(6), cts.Token);
        }
        else if (action is "unruled" or "unruled-off")
        {
            await control.SetUnruledBondAsync(action == "unruled" ? BondId : null, cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(6), cts.Token);
        }
        else if (action == "restart-engine")
        {
            // Split tunneling off and on again (the engine process restarts); the setting ends as it was.
            await control.SetSplitTunnelEnabledAsync(false, cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
            await control.SetSplitTunnelEnabledAsync(true, cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
        }
        else if (action == "conns")
        {
            // Diagnosis only (console, not stored): what the engine knows about curl's open connections.
            var conns = await control.GetConnectionsAsync(cts.Token);
            foreach (var c in conns.Connections.Where(c => c.App.Equals("curl.exe", StringComparison.OrdinalIgnoreCase)))
                output.WriteLine($"  conn dest={c.Destination} network={c.Network} rule={c.Rule}");
        }
        else if (action == "remove")
        {
            try { await control.SetUnruledBondAsync(null, cts.Token); } catch (Exception ex) { output.WriteLine("unruled: " + ex.Message); }
            try { await control.DeleteAppRuleAsync(RuleId, cts.Token); output.WriteLine("rule removed"); } catch (Exception ex) { output.WriteLine("rule: " + ex.Message); }
            try { await control.DeleteBondAsync(BondId, cts.Token); output.WriteLine("bond removed"); } catch (Exception ex) { output.WriteLine("bond: " + ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
        }

        var split = await control.GetSplitTunnelAsync(cts.Token);
        output.WriteLine($"split: enabled={split.Enabled} state={split.State} unruledBond={split.UnruledBondId ?? "-"} now={split.UnruledBondNow ?? "-"} message={split.Message ?? "-"}");
        foreach (var b in split.Bonds ?? [])
            output.WriteLine($"  bond {b.Name}: {b.Mode} usable={b.UsableCount}/{b.Members.Count} rules={b.RuleCount} inUse={b.InUse} now={b.RouteNow} " +
                             $"members=[{string.Join(", ", b.Members.Select(m => $"{m.Name} w{m.Weight} {(m.Usable ? "ok" : m.Connected ? "no-internet" : "down")}"))}] hints={b.Hints.Count}");
        foreach (var r in split.Rules) output.WriteLine($"  app rule {r.DisplayName}: now={r.RouteNow} usable={r.TargetUsable}");
        var live = await control.GetBondsLiveAsync(cts.Token);
        foreach (var b in live.Bonds)
            output.WriteLine($"  live {b.BondId[..6]}: " + string.Join(" | ", b.Members.Select(m => $"{m.AdapterId[..9]} conns={m.Connections} down={m.Download} alive={m.EngineAlive?.ToString() ?? "-"}")));
        var test = await control.TestRouteAsync("example.com", null, cts.Token);
        output.WriteLine($"test example.com: {test.RuleText} -> {test.NetworkName}");
    }
}
