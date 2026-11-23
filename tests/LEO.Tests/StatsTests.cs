using System.Text.Json;
using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Core.SplitTunnel;
using Leo.Core.Stats;
using Leo.Ipc;
using Leo.Service;
using Leo.Service.Networking;
using Leo.Service.SplitTunnel;
using Leo.Service.Stats;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leo.Tests;

public sealed class StatsMathTests
{
    [Theory]
    [InlineData(100, 250, 150)]
    [InlineData(1000, 40, 40)]   // counter reset (re-plug): count from zero
    [InlineData(5, 5, 0)]
    public void Counter_Delta(long previous, long current, long expected) => Assert.Equal(expected, CounterDelta.Compute(previous, current));

    [Fact]
    public void Retention_Cutoffs()
    {
        var today = new DateOnly(2026, 11, 23);
        Assert.Equal("2026-08-26", StatsRetention.Cutoff(today, StatsRetention.AppDays)); // 90 days including today
        Assert.Equal("2026-11-01", StatsRetention.MonthStart(today));
    }

    [Fact]
    public void Ring_Keeps_The_Newest()
    {
        var r = new Ring<int>(3);
        for (var i = 1; i <= 5; i++) r.Add(i);
        Assert.Equal([3, 4, 5], r.ToList());
    }

    private static EngineConnection C(string id, long up, long down, string? path = @"C:\Apps\curl.exe", params string[] chains) =>
        new(id, "tcp", "example.com", null, "93.184.215.14", 443, path, up, down, DateTimeOffset.Now,
            chains.Length == 0 ? ["net-eeeeeeee", "to-eeeeeeee-fb"] : chains, "DomainSuffix", "example.com");

    [Fact]
    public void Connection_Deltas()
    {
        var t = new ConnectionDeltaTracker();
        Assert.Empty(t.Update([C("1", 100, 1000)], baselineOnly: true));       // existing at start: not counted
        var d = t.Update([C("1", 150, 1500), C("2", 10, 20)]);
        Assert.Equal([("1", 50L, 500L), ("2", 10L, 20L)], d.Select(x => (x.Connection.Id, x.Up, x.Down)));
        Assert.Empty(t.Update([C("2", 10, 20)]));                               // closed and idle
        Assert.Single(t.Update([C("1", 5, 5)]));                                // id gone and back = new connection
    }

    private static readonly IReadOnlyList<OutboundAdapter> Outbounds =
    [
        new("net-wwwwwwww", "{WWWWWWWW}", "WiFi"),
        new("net-eeeeeeee", "{EEEEEEEE}", "Ethernet"),
    ];

    [Fact]
    public void Connection_Network_Labels()
    {
        Assert.Equal(("{EEEEEEEE}", "Ethernet"), ConnectionDescriber.Network(C("1", 0, 0), Outbounds));
        Assert.Equal(("{WWWWWWWW}", "WiFi (fallback)"), ConnectionDescriber.Network(C("1", 0, 0, null, "net-wwwwwwww", "to-eeeeeeee-fb"), Outbounds));
        Assert.Equal(("{WWWWWWWW}", "WiFi"), ConnectionDescriber.Network(C("1", 0, 0, null, "net-wwwwwwww", "LEO-PRIMARY"), Outbounds));
        Assert.Equal(((string?)null, "Blocked"), ConnectionDescriber.Network(C("1", 0, 0, null, "REJECT", "to-eeeeeeee-block"), Outbounds));
    }

    [Fact]
    public void Connection_Rule_Uses_LEOs_Precedence()
    {
        var app = new AppRule { Id = "a", DisplayName = "curl", Pattern = @"C:\Apps\curl.exe", TargetAdapterId = "{EEEEEEEE}" };
        var dest = new DestinationRule { Id = "d", Kind = DestinationKind.DomainSuffix, Value = "example.com", TargetAdapterId = "{EEEEEEEE}" };
        var other = new DestinationRule { Id = "o", Kind = DestinationKind.DomainSuffix, Value = "other.org", TargetAdapterId = "{EEEEEEEE}" };

        Assert.Equal("example.com and its subdomains", ConnectionDescriber.Rule(C("1", 0, 0), [app], [dest], []));
        Assert.Equal("App: curl", ConnectionDescriber.Rule(C("1", 0, 0), [app], [other], []));
        Assert.Equal("No rule (Primary)", ConnectionDescriber.Rule(C("1", 0, 0) with { Rule = "Match" }, [app], [dest], []));

        var child = C("1", 0, 0, @"C:\Games\game.exe");
        Assert.Equal("App: curl (started by it)", ConnectionDescriber.Rule(child with { Rule = "RuleSet" }, [app], [], [new ChildProgram(@"C:\Games\game.exe", "a")]));
    }

    [Fact]
    public void Engine_Connection_Json_Is_Parsed()
    {
        using var doc = JsonDocument.Parse("""
            {"downloadTotal":1,"uploadTotal":2,"connections":[{"id":"c1","metadata":{"network":"tcp","type":"Tun","sourceIP":"198.18.0.1",
            "destinationIP":"93.184.215.14","sourcePort":"50000","destinationPort":"443","host":"example.com","dnsMode":"normal",
            "processPath":"C:\\Apps\\curl.exe","sniffHost":"example.com"},"upload":120,"download":4096,"start":"2026-11-23T12:00:00.1+01:00",
            "chains":["net-eeeeeeee","to-eeeeeeee-fb"],"rule":"DomainSuffix","rulePayload":"example.com"}]}
            """);
        var c = Assert.Single(MihomoEngine.ParseConnections(doc.RootElement));
        Assert.Equal("c1", c.Id);
        Assert.Equal(443, c.DestinationPort);
        Assert.Equal(@"C:\Apps\curl.exe", c.ProcessPath);
        Assert.Equal(4096, c.Download);
        Assert.Equal(["net-eeeeeeee", "to-eeeeeeee-fb"], c.Chains);
        Assert.Equal("curl.exe", ConnectionDescriber.AppName(c.ProcessPath));
        Assert.Equal("example.com", ConnectionDescriber.Destination(c));
    }
}

public sealed class StatsStoreTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), "leo-tests-" + Guid.NewGuid().ToString("N"), "stats.db");

    [Fact]
    public void Daily_Totals_Add_Up_And_Group_By_Adapter()
    {
        using var store = new StatsStore(TempDb());
        store.Add([new AdapterDelta("2026-11-22", "{W}", "wifi:Zixel", "WiFi", 100, 10), new AdapterDelta("2026-11-23", "{W}", "wifi:Zixel", "WiFi", 50, 5)],
            [new AppDelta("2026-11-23", "curl.exe", "{E}", "gw:x", 1000, 1)]);
        store.Add([new AdapterDelta("2026-11-23", "{W}", "wifi:Zixel", "WiFi", 25, 5), new AdapterDelta("2026-11-23", "{W}", "wifi:Other", "WiFi", 1, 1)],
            [new AppDelta("2026-11-23", "curl.exe", "{E}", "gw:x", 500, 1)]);

        var today = Assert.Single(store.AdapterTotals("2026-11-23"));
        Assert.Equal((76L, 11L), (today.Received, today.Sent));
        Assert.Equal(176, Assert.Single(store.AdapterTotals("2026-11-01")).Received);
        var app = Assert.Single(store.AppTotals("2026-11-23"));
        Assert.Equal(("curl.exe", 1500L, 2L), (app.App, app.Received, app.Sent));
    }

    [Fact]
    public void Retention_Prunes_Old_Days()
    {
        using var store = new StatsStore(TempDb());
        var today = new DateOnly(2026, 11, 23);
        store.Add(
            [new AdapterDelta(StatsRetention.Day(today.AddDays(-399)), "{W}", "", "WiFi", 1, 1), new AdapterDelta(StatsRetention.Day(today.AddDays(-400)), "{W}", "", "WiFi", 1, 1)],
            [new AppDelta(StatsRetention.Day(today.AddDays(-89)), "a.exe", "{W}", "", 1, 1), new AppDelta(StatsRetention.Day(today.AddDays(-90)), "a.exe", "{W}", "", 1, 1)]);
        Assert.Equal(2, store.Prune(today));
        Assert.Equal([StatsRetention.Day(today.AddDays(-399))], store.Days("adapter"));
        Assert.Equal([StatsRetention.Day(today.AddDays(-89))], store.Days("app"));
    }

    [Fact]
    public void Clear_Removes_Everything_And_The_Schema_Has_No_Destinations()
    {
        var path = TempDb();
        using (var store = new StatsStore(path))
        {
            store.Add([new AdapterDelta("2026-11-23", "{W}", "", "WiFi", 1, 1)], [new AppDelta("2026-11-23", "a.exe", "{W}", "", 1, 1)]);
            store.Clear();
            Assert.Empty(store.AdapterTotals("2000-01-01"));
            Assert.Empty(store.AppTotals("2000-01-01"));
        }
        // Privacy guard: the only tables/columns are daily totals.
        using var db = new SqliteDb(path);
        var columns = db.Query("SELECT m.name || '.' || p.name FROM sqlite_master m JOIN pragma_table_info(m.name) p WHERE m.type = 'table' ORDER BY 1")
            .Select(r => r.Text(0)).ToList();
        Assert.Equal(["adapter_daily.adapter_id", "adapter_daily.adapter_name", "adapter_daily.day", "adapter_daily.network_key", "adapter_daily.rx", "adapter_daily.tx",
            "app_daily.adapter_id", "app_daily.app", "app_daily.day", "app_daily.network_key", "app_daily.rx", "app_daily.tx",
            "meta.key", "meta.value"], columns);
    }
}

public sealed class StatsCollectorTests
{
    private static (StatsCollector Collector, string Db) Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leo-tests-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigStore(Path.Combine(dir, "config.json"));
        var hub = new NotificationHub(NullLogger<NotificationHub>.Instance);
        var networks = new NetworkManager(new NetworkInventory(NullLogger<NetworkInventory>.Instance), config, hub,
            NullLogger<NetworkManager>.Instance) { ProbesEnabled = false };
        var db = Path.Combine(dir, "stats.db");
        return (new StatsCollector(networks, config, NullLogger<StatsCollector>.Instance, dbPath: db), db);
    }

    private static AdapterInfo A(string id, AdapterStatus status, HealthState health = HealthState.Up, int? latency = 12) =>
        new(id, "Ethernet", "RNDIS", AdapterKind.Ethernet, null, "02-00", status, 0, [], [], [], [],
            new NetworkInfo("gw:aa", "Phone", null, Connectivity.Internet, false), 10, false, 0, true, true, health, latency, null, false, null, []);

    private static NetworkSnapshot Snap(params AdapterInfo[] a) => new(a, a.FirstOrDefault()?.Id, true, DateTimeOffset.Now);

    [Fact]
    public void Counters_Become_Session_And_Daily_Totals()
    {
        var (s, _) = Create();
        var t0 = DateTimeOffset.Now;
        var up = Snap(A("{E}", AdapterStatus.Up));
        s.SampleAdapters(up, new Dictionary<string, (long, long)> { ["{E}"] = (1_000_000, 50_000) }, t0); // baseline
        s.SampleAdapters(up, new Dictionary<string, (long, long)> { ["{E}"] = (3_000_000, 70_000) }, t0.AddSeconds(2));
        s.SampleAdapters(up, new Dictionary<string, (long, long)> { ["{E}"] = (500, 100) }, t0.AddSeconds(4)); // counter reset
        s.Flush();

        var e = Assert.Single(s.GetSnapshot().Adapters); // unplugged in the live snapshot, but used this month
        Assert.Equal(2_000_500, e.Today.Received);
        Assert.Equal(20_100, e.Today.Sent);
        Assert.Equal(2_000_500, e.Session.Received);
        Assert.Equal(e.Today, e.Month);
        Assert.Equal(250, e.ReceiveBps); // 500 bytes over 2 s
        Assert.Equal([0L, 1_000_000L, 250L], e.ReceiveHistory);
    }

    [Fact]
    public void Session_Restarts_When_The_Adapter_Reconnects()
    {
        var (s, _) = Create();
        var t = DateTimeOffset.Now;
        s.SampleAdapters(Snap(A("{E}", AdapterStatus.Up)), new Dictionary<string, (long, long)> { ["{E}"] = (0, 0) }, t);
        s.SampleAdapters(Snap(A("{E}", AdapterStatus.Up)), new Dictionary<string, (long, long)> { ["{E}"] = (100, 0) }, t.AddSeconds(2));
        s.SampleAdapters(Snap(A("{E}", AdapterStatus.Disconnected)), new Dictionary<string, (long, long)>(), t.AddSeconds(4));
        s.SampleAdapters(Snap(A("{E}", AdapterStatus.Up)), new Dictionary<string, (long, long)> { ["{E}"] = (40, 0) }, t.AddSeconds(6)); // baseline again
        s.SampleAdapters(Snap(A("{E}", AdapterStatus.Up)), new Dictionary<string, (long, long)> { ["{E}"] = (70, 0) }, t.AddSeconds(8));
        s.Flush();
        var e = Assert.Single(s.GetSnapshot().Adapters);
        Assert.Equal(30, e.Session.Received);
        Assert.Equal(130, e.Today.Received);
    }

    [Fact]
    public void Latency_History_Marks_Failed_Checks()
    {
        var (s, _) = Create();
        var t = DateTimeOffset.Now;
        s.SampleAdapters(Snap(A("{E}", AdapterStatus.Up, HealthState.Up, 10)), new Dictionary<string, (long, long)> { ["{E}"] = (0, 0) }, t);
        s.SampleAdapters(Snap(A("{E}", AdapterStatus.Up, HealthState.Up, 30)), new Dictionary<string, (long, long)> { ["{E}"] = (10, 0) }, t.AddSeconds(2)); // same check round
        s.SampleAdapters(Snap(A("{E}", AdapterStatus.Up, HealthState.NoInternet, null)), new Dictionary<string, (long, long)> { ["{E}"] = (10, 0) }, t.AddSeconds(5));
        s.SampleAdapters(Snap(A("{E}", AdapterStatus.Up, HealthState.Up, 20)), new Dictionary<string, (long, long)> { ["{E}"] = (10, 0) }, t.AddSeconds(10));
        s.Flush();
        var e = Assert.Single(s.GetSnapshot().Adapters);
        Assert.Equal([10, null, 20], e.LatencyHistory);
        Assert.Equal(15, e.LatencyAverageMs);
        Assert.Equal(33.3, e.FailedChecksPercent);
    }

    [Fact]
    public void Per_App_Bytes_Go_To_The_Network_That_Carried_Them()
    {
        var (s, db) = Create();
        var adapterId = "{EEEEEEEE-0000}";
        var snap = Snap(A(adapterId, AdapterStatus.Up));
        var outbound = EnginePlan.OutboundName(adapterId);
        EngineConnection C(string id, long up, long down, string first) =>
            new(id, "tcp", "example.com", null, "93.184.215.14", 443, @"C:\Apps\curl.exe", up, down, DateTimeOffset.Now, [first, "to-x-fb"], "DomainSuffix", "example.com");

        var t = DateTimeOffset.Now;
        s.AddConnectionDeltas([C("1", 10, 100, outbound)], snap, t);                                  // baseline
        s.AddConnectionDeltas([C("1", 20, 1100, outbound), C("2", 5, 5, "REJECT")], snap, t.AddSeconds(2)); // blocked bytes aren't usage
        s.Flush();

        using var store = new StatsStore(db);
        var app = Assert.Single(store.AppTotals("2000-01-01"));
        Assert.Equal(("curl.exe", adapterId, 1000L, 10L), (app.App, app.AdapterId, app.Received, app.Sent));
    }
}
