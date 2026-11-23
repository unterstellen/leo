using System.Net.NetworkInformation;
using Leo.Core;
using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Core.SplitTunnel;
using Leo.Core.Stats;
using Leo.Ipc;
using Leo.Service.Networking;
using Leo.Service.SplitTunnel;

namespace Leo.Service.Stats;

/// <summary>
/// Live stats (LEO.md §6.7, §19). Every 2 s: per-adapter byte counters (Windows' own interface counters, the same
/// ones Get-NetAdapterStatistics shows) → throughput, session/day/month totals; the engine's connection list →
/// bytes per app per network. Latency comes from the existing internet checks. Only daily byte totals reach
/// stats.db; connections and destinations stay in memory and are never logged.
/// </summary>
public sealed class StatsCollector(
    NetworkManager networks, ConfigStore config, ILogger<StatsCollector> log,
    SplitTunnelManager? splitTunnel = null, ChildProcessTracker? children = null, string? dbPath = null) : BackgroundService
{
    public const int SampleSeconds = 2;
    private const int HistoryPoints = 150; // 5 minutes
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AdapterState> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<(string Day, string Adapter, string Network), (string Name, long Rx, long Tx)> _pendingAdapters = [];
    private Dictionary<(string Day, string App, string Adapter, string Network), (long Rx, long Tx)> _pendingApps = [];
    private Dictionary<(string App, string Adapter), (long Rx, long Tx)> _appRates = [];
    private readonly ConnectionDeltaTracker _connections = new();
    private bool _connectionsBaselined;
    private StatsStore? _store;
    private bool _storeFailed;
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;
    private DateOnly _prunedOn;

    private sealed class AdapterState(string name, AdapterKind kind, int latencyPoints)
    {
        public string Name = name;
        public AdapterKind Kind = kind;
        public string? NetworkName;
        public bool IsUp;
        public bool HasBaseline;
        public long LastRx, LastTx;
        public long SessionRx, SessionTx;
        public long RxBps, TxBps;
        public readonly Ring<long> RxHistory = new(HistoryPoints);
        public readonly Ring<long> TxHistory = new(HistoryPoints);
        public Ring<int?> Latency = new(latencyPoints);
        public DateTimeOffset LastLatencyAt = DateTimeOffset.MinValue;
    }

    // ── API ─────────────────────────────────────────────────────────────────

    public StatsSnapshot GetSnapshot()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dayKey = StatsRetention.Day(today);
        var monthKey = StatsRetention.MonthStart(today);
        var store = Store();
        IReadOnlyList<AdapterTotal> dbToday = [], dbMonth = [];
        IReadOnlyList<AppTotal> appToday = [], appMonth = [];
        try
        {
            if (store is not null)
            {
                dbToday = store.AdapterTotals(dayKey);
                dbMonth = store.AdapterTotals(monthKey);
                appToday = store.AppTotals(dayKey);
                appMonth = store.AppTotals(monthKey);
            }
        }
        catch (Exception ex) { log.LogWarning("Reading stats.db failed: {Message}", ex.Message); }

        var snap = networks.Snapshot;
        lock (_gate)
        {
            TrafficTotals Sum(string adapterId, IReadOnlyList<AdapterTotal> db, bool monthly)
            {
                long rx = 0, tx = 0;
                foreach (var t in db.Where(t => t.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase))) { rx += t.Received; tx += t.Sent; }
                foreach (var (k, v) in _pendingAdapters)
                    if (k.Adapter.Equals(adapterId, StringComparison.OrdinalIgnoreCase) && (monthly ? string.CompareOrdinal(k.Day, monthKey) >= 0 : k.Day == dayKey)) { rx += v.Rx; tx += v.Tx; }
                return new TrafficTotals(rx, tx);
            }

            var ids = snap.Adapters.Where(a => a.IsPrimaryCandidate).OrderBy(a => a.PriorityIndex ?? int.MaxValue).Select(a => a.Id).ToList();
            foreach (var t in dbMonth) if (!ids.Contains(t.AdapterId, StringComparer.OrdinalIgnoreCase)) ids.Add(t.AdapterId); // unplugged, used this month

            var adapters = ids.Select(id =>
            {
                var live = snap.Adapters.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                _adapters.TryGetValue(id, out var st);
                var name = live?.DisplayName ?? st?.Name ?? dbMonth.FirstOrDefault(t => t.AdapterId.Equals(id, StringComparison.OrdinalIgnoreCase))?.AdapterName ?? "Adapter";
                var latency = st?.Latency.ToList() ?? [];
                var ok = latency.Where(l => l is not null).Select(l => l!.Value).ToList();
                return new AdapterStatsInfo(id, name, live?.Kind ?? st?.Kind ?? AdapterKind.Other,
                    live?.Network is { } n ? n.Nickname ?? n.Name : null, live?.Status == AdapterStatus.Up,
                    st?.RxBps ?? 0, st?.TxBps ?? 0,
                    st is null ? TrafficTotals.Zero : new TrafficTotals(st.SessionRx, st.SessionTx),
                    Sum(id, dbToday, false), Sum(id, dbMonth, true),
                    st?.RxHistory.ToList() ?? [], st?.TxHistory.ToList() ?? [], latency,
                    ok.Count > 0 ? (int)Math.Round(ok.Average()) : null,
                    latency.Count > 0 ? Math.Round(100.0 * (latency.Count - ok.Count) / latency.Count, 1) : 0);
            }).ToList();

            // Per app per network: today + month from the db, plus what isn't flushed yet, plus live rates.
            var apps = new Dictionary<(string App, string Adapter), (long TRx, long TTx, long MRx, long MTx)>();
            void AddApp(string app, string adapter, long trx, long ttx, long mrx, long mtx)
            {
                var key = (app, adapter.ToUpperInvariant());
                var cur = apps.GetValueOrDefault(key);
                apps[key] = (cur.TRx + trx, cur.TTx + ttx, cur.MRx + mrx, cur.MTx + mtx);
            }
            foreach (var t in appToday) AddApp(t.App, t.AdapterId, t.Received, t.Sent, 0, 0);
            foreach (var t in appMonth) AddApp(t.App, t.AdapterId, 0, 0, t.Received, t.Sent);
            foreach (var (k, v) in _pendingApps)
            {
                if (string.CompareOrdinal(k.Day, monthKey) < 0) continue;
                AddApp(k.App, k.Adapter, k.Day == dayKey ? v.Rx : 0, k.Day == dayKey ? v.Tx : 0, v.Rx, v.Tx);
            }
            foreach (var (k, _) in _appRates) AddApp(k.App, k.Adapter, 0, 0, 0, 0);

            string NetworkName(string adapterId) =>
                snap.Adapters.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? (_adapters.TryGetValue(adapterId, out var s) ? s.Name : null)
                ?? dbMonth.FirstOrDefault(t => t.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase))?.AdapterName ?? "Adapter";

            var appList = apps.Select(kv =>
            {
                var rate = _appRates.FirstOrDefault(r => r.Key.App == kv.Key.App && r.Key.Adapter.Equals(kv.Key.Adapter, StringComparison.OrdinalIgnoreCase)).Value;
                return new AppUsageInfo(kv.Key.App, kv.Key.Adapter, NetworkName(kv.Key.Adapter), rate.Rx, rate.Tx,
                    new TrafficTotals(kv.Value.TRx, kv.Value.TTx), new TrafficTotals(kv.Value.MRx, kv.Value.MTx));
            })
            .OrderByDescending(a => a.ReceiveBps + a.SendBps).ThenByDescending(a => a.Month.Received + a.Month.Sent)
            .Take(60).ToList();

            return new StatsSnapshot(DateTimeOffset.Now, SampleSeconds, adapters, appList, splitTunnel?.RunningEngine is not null,
                StatsRetention.AdapterDays, StatsRetention.AppDays);
        }
    }

    /// <summary>Live connections, mapped to app → destination → network → LEO rule. Memory only.</summary>
    public async Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken ct)
    {
        var engine = splitTunnel?.RunningEngine;
        if (engine is null)
            return new ConnectionsSnapshot(false, "Live connections appear while split tunneling is running (at least one active app or destination rule).", []);

        IReadOnlyList<EngineConnection> conns;
        try { conns = await engine.GetConnectionsAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ConnectionsSnapshot(false, "The routing engine didn't answer; try again in a moment.", []);
        }

        var outbounds = Outbounds(networks.Snapshot);
        var (apps, dests) = config.Read(c => (ProfileOpsClone(c.SplitTunnel.AppRules), ProfileOpsClone(c.SplitTunnel.DestinationRules)));
        var kids = children?.Children ?? [];
        var list = conns.OrderByDescending(c => c.Start).Take(500).Select(c =>
        {
            var (adapterId, network) = ConnectionDescriber.Network(c, outbounds);
            return new ConnectionInfo(c.Id, ConnectionDescriber.AppName(c.ProcessPath), c.ProcessPath, ConnectionDescriber.Destination(c),
                c.DestinationPort, c.Network.ToUpperInvariant(), adapterId, network, ConnectionDescriber.Rule(c, apps, dests, kids),
                c.Upload, c.Download, c.Start);
        }).ToList();
        return new ConnectionsSnapshot(true, null, list);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pendingAdapters = [];
            _pendingApps = [];
            foreach (var st in _adapters.Values) { st.SessionRx = st.SessionTx = 0; st.RxHistory.Clear(); st.TxHistory.Clear(); st.Latency.Clear(); }
        }
        Store()?.Clear();
        log.LogInformation("Statistics cleared by the user");
    }

    // ── Loop ─────────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                SampleAdapters(networks.Snapshot, ReadCounters(), DateTimeOffset.UtcNow);
                await SampleAppsAsync(stoppingToken);
                if (DateTimeOffset.UtcNow - _lastFlush > FlushEvery) Flush();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Stats sample failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(SampleSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { Flush(); } catch (Exception ex) { log.LogWarning("Saving stats on stop failed: {Message}", ex.Message); }
        lock (_gate) { _store?.Dispose(); _store = null; }
    }

    private static Dictionary<string, (long Rx, long Tx)> ReadCounters()
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var s = n.GetIPStatistics();
                map[n.Id] = (s.BytesReceived, s.BytesSent);
            }
            catch (NetworkInformationException) { }
        }
        return map;
    }

    /// <summary>One counter sample (internal for tests: snapshot + counters + clock are injected).</summary>
    internal void SampleAdapters(NetworkSnapshot snap, IReadOnlyDictionary<string, (long Rx, long Tx)> counters, DateTimeOffset now)
    {
        var day = StatsRetention.Day(DateOnly.FromDateTime(now.ToLocalTime().DateTime));
        var interval = Math.Clamp(config.Read(c => c.Health.ProbeIntervalSeconds), 2, 300);
        var latencyPoints = Math.Clamp(3600 / interval, 12, 1800);
        lock (_gate)
        {
            var elapsed = _lastSample == DateTimeOffset.MinValue ? SampleSeconds : Math.Max(0.5, (now - _lastSample).TotalSeconds);
            _lastSample = now;
            foreach (var a in snap.Adapters.Where(a => a.IsPrimaryCandidate))
            {
                if (!_adapters.TryGetValue(a.Id, out var st)) _adapters[a.Id] = st = new AdapterState(a.DisplayName, a.Kind, latencyPoints);
                st.Name = a.DisplayName;
                st.Kind = a.Kind;
                st.NetworkName = a.Network is { } n ? n.Nickname ?? n.Name : null;
                if (st.Latency.Capacity != latencyPoints) st.Latency = new Ring<int?>(latencyPoints);

                var up = a.Status == AdapterStatus.Up;
                if (up && !st.IsUp) { st.SessionRx = st.SessionTx = 0; } // a new session starts when it connects
                st.IsUp = up;

                long drx = 0, dtx = 0;
                if (counters.TryGetValue(a.Id, out var c))
                {
                    if (st.HasBaseline) { drx = CounterDelta.Compute(st.LastRx, c.Rx); dtx = CounterDelta.Compute(st.LastTx, c.Tx); }
                    st.LastRx = c.Rx;
                    st.LastTx = c.Tx;
                    st.HasBaseline = true;
                }
                else
                {
                    st.HasBaseline = false; // unplugged: its counter starts over when it comes back
                }

                st.RxBps = (long)(drx / elapsed);
                st.TxBps = (long)(dtx / elapsed);
                st.RxHistory.Add(st.RxBps);
                st.TxHistory.Add(st.TxBps);
                st.SessionRx += drx;
                st.SessionTx += dtx;
                if (drx > 0 || dtx > 0)
                {
                    var key = (day, a.Id, a.Network?.Key ?? "");
                    var cur = _pendingAdapters.GetValueOrDefault(key);
                    _pendingAdapters[key] = (a.DisplayName, cur.Rx + drx, cur.Tx + dtx);
                }

                // Latency history: one point per internet check (null = the check failed).
                if (now - st.LastLatencyAt >= TimeSpan.FromSeconds(interval) && up)
                {
                    if (a.Health is HealthState.Up or HealthState.Degraded && a.LatencyMs is { } ms) { st.Latency.Add(ms); st.LastLatencyAt = now; }
                    else if (a.Health.IsFailing()) { st.Latency.Add(null); st.LastLatencyAt = now; }
                }
            }
        }
    }

    private async Task SampleAppsAsync(CancellationToken ct)
    {
        var engine = splitTunnel?.RunningEngine;
        if (engine is null)
        {
            lock (_gate) { _connections.Reset(); _connectionsBaselined = false; _appRates = []; }
            return;
        }
        IReadOnlyList<EngineConnection> conns;
        try { conns = await engine.GetConnectionsAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested) { return; }
        AddConnectionDeltas(conns, networks.Snapshot, DateTimeOffset.UtcNow);
    }

    /// <summary>Attributes per-connection deltas to (app, network). Internal for tests.</summary>
    internal void AddConnectionDeltas(IReadOnlyList<EngineConnection> conns, NetworkSnapshot snap, DateTimeOffset now)
    {
        var day = StatsRetention.Day(DateOnly.FromDateTime(now.ToLocalTime().DateTime));
        var outbounds = Outbounds(snap);
        lock (_gate)
        {
            var deltas = _connections.Update(conns, baselineOnly: !_connectionsBaselined);
            _connectionsBaselined = true;
            var rates = new Dictionary<(string, string), (long, long)>();
            foreach (var (c, up, down) in deltas)
            {
                var (adapterId, _) = ConnectionDescriber.Network(c, outbounds);
                if (adapterId is null) continue; // blocked
                var app = ConnectionDescriber.AppName(c.ProcessPath);
                var network = snap.Adapters.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))?.Network?.Key ?? "";
                var key = (day, app, adapterId, network);
                var cur = _pendingApps.GetValueOrDefault(key);
                _pendingApps[key] = (cur.Rx + down, cur.Tx + up);
                var rk = (app, adapterId);
                var r = rates.GetValueOrDefault(rk);
                rates[rk] = (r.Item1 + down / SampleSeconds, r.Item2 + up / SampleSeconds);
            }
            _appRates = rates;
        }
    }

    /// <summary>Writes pending totals to stats.db; prunes old rows once a day.</summary>
    internal void Flush()
    {
        _lastFlush = DateTimeOffset.UtcNow;
        Dictionary<(string Day, string Adapter, string Network), (string Name, long Rx, long Tx)> adapters;
        Dictionary<(string Day, string App, string Adapter, string Network), (long Rx, long Tx)> apps;
        lock (_gate)
        {
            adapters = _pendingAdapters;
            apps = _pendingApps;
            _pendingAdapters = [];
            _pendingApps = [];
        }
        var store = Store();
        if (store is null) return;
        try
        {
            store.Add(adapters.Select(kv => new AdapterDelta(kv.Key.Day, kv.Key.Adapter, kv.Key.Network, kv.Value.Name, kv.Value.Rx, kv.Value.Tx)).ToList(),
                apps.Select(kv => new AppDelta(kv.Key.Day, kv.Key.App, kv.Key.Adapter, kv.Key.Network, kv.Value.Rx, kv.Value.Tx)).ToList());
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_prunedOn != today)
            {
                _prunedOn = today;
                var removed = store.Prune(today);
                if (removed > 0) log.LogInformation("Stats: removed {Count} day total(s) past the retention period", removed);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("Saving stats failed: {Message}", ex.Message);
        }
    }

    private StatsStore? Store()
    {
        lock (_gate)
        {
            if (_store is not null || _storeFailed) return _store;
            try { _store = new StatsStore(dbPath ?? LeoPaths.StatsDb); }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
            {
                _storeFailed = true;
                log.LogWarning("Stats database unavailable, keeping stats in memory only: {Message}", ex.Message);
            }
            return _store;
        }
    }

    private static IReadOnlyList<OutboundAdapter> Outbounds(NetworkSnapshot snap) =>
        snap.Adapters.Select(a => new OutboundAdapter(EnginePlan.OutboundName(a.Id), a.Id, a.DisplayName)).ToList();

    private static List<T> ProfileOpsClone<T>(List<T> list) => Leo.Core.Profiles.ProfileOps.Clone(list);
}
