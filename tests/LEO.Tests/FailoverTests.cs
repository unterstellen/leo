using Leo.Core.Networking;

namespace Leo.Tests;

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(double seconds) => _now = _now.AddSeconds(seconds);
}

public sealed class FailoverControllerTests
{
    private static Dictionary<string, HealthState> H(params (string Id, HealthState State)[] items) =>
        items.ToDictionary(i => i.Id, i => i.State);

    [Fact]
    public void Demotes_Only_After_Failing_For_FailoverDelay()
    {
        var clock = new ManualClock();
        var f = new FailoverController(clock);

        f.Update(H(("wifi", HealthState.Up)));
        f.Update(H(("wifi", HealthState.NoInternet)));
        clock.Advance(5);
        Assert.False(f.Update(H(("wifi", HealthState.NoInternet))));
        Assert.False(f.IsDemoted("wifi"));

        clock.Advance(1.5);
        Assert.True(f.Update(H(("wifi", HealthState.NoInternet))));
        Assert.True(f.IsDemoted("wifi"));
    }

    [Fact]
    public void Restores_Only_After_Healthy_For_FailbackDelay()
    {
        var clock = new ManualClock();
        var f = new FailoverController(clock);
        f.Update(H(("wifi", HealthState.Captive)));
        clock.Advance(7);
        f.Update(H(("wifi", HealthState.Captive)));
        Assert.True(f.IsDemoted("wifi"));

        f.Update(H(("wifi", HealthState.Up)));
        clock.Advance(20);
        f.Update(H(("wifi", HealthState.Up)));
        Assert.True(f.IsDemoted("wifi"));
        Assert.Equal(10, f.SecondsUntilFailback("wifi"));

        clock.Advance(10);
        Assert.True(f.Update(H(("wifi", HealthState.Up))));
        Assert.False(f.IsDemoted("wifi"));
    }

    [Fact]
    public void Flapping_Restarts_The_Failback_Clock()
    {
        var clock = new ManualClock();
        var f = new FailoverController(clock);
        f.Update(H(("wifi", HealthState.NoInternet)));
        clock.Advance(7);
        f.Update(H(("wifi", HealthState.NoInternet)));

        f.Update(H(("wifi", HealthState.Up)));
        clock.Advance(25);
        f.Update(H(("wifi", HealthState.NoInternet))); // blip
        f.Update(H(("wifi", HealthState.Up)));
        clock.Advance(25);
        f.Update(H(("wifi", HealthState.Up)));
        Assert.True(f.IsDemoted("wifi")); // only 25 s of the new healthy streak
    }

    [Fact]
    public void Degraded_And_Up_Share_A_Streak()
    {
        var clock = new ManualClock();
        var f = new FailoverController(clock);
        f.Update(H(("wifi", HealthState.NoInternet)));
        clock.Advance(7);
        f.Update(H(("wifi", HealthState.NoInternet)));
        f.Update(H(("wifi", HealthState.Up)));
        clock.Advance(15);
        f.Update(H(("wifi", HealthState.Degraded)));
        clock.Advance(16);
        f.Update(H(("wifi", HealthState.Up)));
        Assert.False(f.IsDemoted("wifi"));
    }

    [Fact]
    public void Disabled_Never_Demotes()
    {
        var clock = new ManualClock();
        var f = new FailoverController(clock) { Enabled = false };
        f.Update(H(("wifi", HealthState.NoInternet)));
        clock.Advance(60);
        f.Update(H(("wifi", HealthState.NoInternet)));
        Assert.False(f.IsDemoted("wifi"));
    }

    [Fact]
    public void Vanished_Adapter_Is_Forgotten()
    {
        var clock = new ManualClock();
        var f = new FailoverController(clock);
        f.Update(H(("phone", HealthState.NoInternet)));
        clock.Advance(7);
        f.Update(H(("phone", HealthState.NoInternet)));
        Assert.True(f.Update(H()));
        Assert.Empty(f.Demoted);
    }
}

public sealed class PlannerDemotionTests
{
    private static PlannerAdapter A(string id, AdapterKind kind, bool up = true) => new(id, kind, up, up, 50);

    [Fact]
    public void Demoted_Adapter_Keeps_Rank_But_Moves_Behind_Healthy_Ones()
    {
        var plan = PriorityPlanner.Plan(
            [A("eth", AdapterKind.Ethernet), A("wifi", AdapterKind.WiFi), A("phone", AdapterKind.UsbTether)],
            ["eth", "wifi", "phone"],
            new HashSet<string> { "eth" });

        Assert.Equal(["eth", "wifi", "phone"], plan.Ranking);
        Assert.Equal(["wifi", "phone", "eth"], plan.Order);
        Assert.Equal(10, plan.Metrics["wifi"]);
        Assert.Equal(30, plan.Metrics["eth"]);
        Assert.Equal("wifi", plan.PrimaryId);
    }

    [Fact]
    public void If_Everything_Is_Demoted_Primary_Is_Still_The_Top_Routable()
    {
        var plan = PriorityPlanner.Plan([A("eth", AdapterKind.Ethernet), A("wifi", AdapterKind.WiFi)], ["eth", "wifi"],
            new HashSet<string> { "eth", "wifi" });
        Assert.Equal("eth", plan.PrimaryId);
    }
}
