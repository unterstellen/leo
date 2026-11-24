using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leo.App.Services;
using Leo.Ipc;
using Microsoft.UI.Dispatching;

namespace Leo.App.ViewModels;

/// <summary>
/// Stats page (LEO.md §6.7, §19): per-adapter throughput/totals/latency, per-app usage per network, live
/// connections. Polls the service every 2 s while the page is open; nothing here is stored by the UI.
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly DispatcherQueue _dispatcher;
    private DispatcherQueueTimer? _timer;
    private bool _busy;
    private IReadOnlyList<ConnectionInfo> _allConnections = [];

    public StatsViewModel(ServiceConnection connection, DispatcherQueue dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<AdapterStatsCardViewModel> Adapters { get; } = [];
    public ObservableCollection<AppUsageRowViewModel> Apps { get; } = [];
    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = [];

    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string AppsNote { get; set; } = "";
    [ObservableProperty] public partial string ConnectionsNote { get; set; } = "";
    [ObservableProperty] public partial bool ConnectionsPaused { get; set; }
    [ObservableProperty] public partial string PauseText { get; set; } = "Pause";
    [ObservableProperty] public partial string ConnectionsFilter { get; set; } = "";
    [ObservableProperty] public partial string RetentionText { get; set; } = "";
    [ObservableProperty] public partial bool HasNoApps { get; set; }
    [ObservableProperty] public partial string? ErrorText { get; set; }

    /// <summary>Starts polling (page shown).</summary>
    public void Start()
    {
        _timer ??= CreateTimer();
        _timer.Start();
        _ = PollAsync();
    }

    /// <summary>Stops polling (page hidden).</summary>
    public void Stop() => _timer?.Stop();

    private DispatcherQueueTimer CreateTimer()
    {
        var t = _dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.Tick += async (_, _) => await PollAsync();
        return t;
    }

    public void TogglePause()
    {
        ConnectionsPaused = !ConnectionsPaused;
        PauseText = ConnectionsPaused ? "Resume" : "Pause";
    }

    public void ApplyFilter(string text)
    {
        ConnectionsFilter = text;
        ShowConnections();
    }

    public async Task ClearAsync()
    {
        if (!CanEdit) return;
        UiLog.Write("Stats: clear all saved statistics");
        ErrorText = await _connection.TryInvokeAsync(c => c.ClearStatsAsync());
        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            IsConnected = _connection.State == ConnectionState.Connected;
            CanEdit = IsConnected && _connection.Access == ClientAccess.Full;
            if (!IsConnected) return;

            var (stats, _) = await _connection.QueryAsync(c => c.GetStatsAsync());
            if (stats is not null) ShowStats(stats);

            if (!CanEdit)
            {
                ConnectionsNote = "Only administrators can see live connections, because they show what every app on this PC connects to.";
                _allConnections = [];
                ShowConnections();
            }
            else if (!ConnectionsPaused)
            {
                var (conns, error) = await _connection.QueryAsync(c => c.GetConnectionsAsync());
                if (conns is not null)
                {
                    _allConnections = conns.Connections;
                    ConnectionsNote = conns.Note ?? (conns.Connections.Count == 0 ? "No connections through LEO's routing engine right now."
                        : $"{conns.Connections.Count} connection(s) through LEO's routing engine.");
                    ShowConnections();
                }
                else if (error is not null) ConnectionsNote = error.Message;
            }
        }
        catch (Exception ex)
        {
            UiLog.Write("Stats refresh failed", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowStats(StatsSnapshot s)
    {
        Sync(Adapters, s.Adapters, a => a.AdapterId, a => new AdapterStatsCardViewModel(a), (vm, a) => vm.Update(a));
        Sync(Apps, s.Apps, a => a.App + "|" + a.AdapterId, a => new AppUsageRowViewModel(a), (vm, a) => vm.Update(a));
        HasNoApps = s.Apps.Count == 0;
        AppsNote = s.PerAppLive
            ? "Counted from LEO's routing engine while split tunneling runs. Traffic inside your local network isn't included."
            : "Per-app numbers are counted while split tunneling runs (at least one active app or destination rule). Totals below are from earlier.";
        RetentionText = $"Saved on this PC only (stats.db): daily byte totals per network for {s.AdapterRetentionDays} days and per app per network for {s.AppRetentionDays} days. Connections and destinations are never saved.";
    }

    private void ShowConnections()
    {
        var filter = ConnectionsFilter.Trim();
        var list = filter.Length == 0 ? _allConnections
            : _allConnections.Where(c => c.App.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                         || c.Destination.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                         || c.Network.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                         || c.Rule.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        Sync(Connections, list.Take(300).ToList(), c => c.Id, c => new ConnectionRowViewModel(c), (vm, c) => vm.Update(c));
    }

    /// <summary>Updates rows in place when the keys line up, so lists don't flicker or lose their scroll position.</summary>
    private static void Sync<TVm, T>(ObservableCollection<TVm> target, IReadOnlyList<T> items, Func<T, string> key,
        Func<T, TVm> create, Action<TVm, T> update) where TVm : IKeyed
    {
        if (target.Count == items.Count && target.Select(t => t.Key).SequenceEqual(items.Select(key)))
        {
            for (var i = 0; i < items.Count; i++) update(target[i], items[i]);
            return;
        }
        // Remove rows that went away, then insert/move to match the new order.
        var wanted = items.Select(key).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--) if (!wanted.Contains(target[i].Key)) target.RemoveAt(i);
        for (var i = 0; i < items.Count; i++)
        {
            var k = key(items[i]);
            var at = -1;
            for (var j = i; j < target.Count; j++) if (target[j].Key == k) { at = j; break; }
            if (at < 0) target.Insert(i, create(items[i]));
            else
            {
                if (at != i) target.Move(at, i);
                update(target[i], items[i]);
            }
        }
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    public static string Bytes(long b) => b switch
    {
        < 1000 => $"{b} B",
        < 1_000_000 => $"{b / 1e3:0.#} KB",
        < 1_000_000_000 => $"{b / 1e6:0.##} MB",
        < 1_000_000_000_000 => $"{b / 1e9:0.##} GB",
        _ => $"{b / 1e12:0.##} TB",
    };

    public static string Rate(long bps) => Bytes(bps) + "/s";

    public static string Totals(TrafficTotals t) => $"↓ {Bytes(t.Received)}  ↑ {Bytes(t.Sent)}";
}

public interface IKeyed
{
    string Key { get; }
}

public sealed partial class AdapterStatsCardViewModel : ObservableObject, IKeyed
{
    public AdapterStatsCardViewModel(AdapterStatsInfo info)
    {
        Key = info.AdapterId;
        Update(info);
    }

    public string Key { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Glyph { get; set; } = "";
    [ObservableProperty] public partial string DetailText { get; set; } = "";
    [ObservableProperty] public partial string RateText { get; set; } = "";
    [ObservableProperty] public partial string SessionText { get; set; } = "";
    [ObservableProperty] public partial string TodayText { get; set; } = "";
    [ObservableProperty] public partial string MonthText { get; set; } = "";
    [ObservableProperty] public partial string LatencyText { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<double> ReceiveHistory { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<double> SendHistory { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<double> LatencyHistory { get; set; } = [];

    public void Update(AdapterStatsInfo a)
    {
        Name = a.Name;
        Glyph = AdapterCardViewModel.GlyphFor(a.Kind);
        DetailText = a.IsUp ? (a.NetworkName is { } n ? $"Connected to {n}" : "Connected") : "Not connected";
        RateText = a.IsUp ? $"↓ {StatsViewModel.Rate(a.ReceiveBps)}   ↑ {StatsViewModel.Rate(a.SendBps)}" : "";
        SessionText = "This session: " + StatsViewModel.Totals(a.Session);
        TodayText = "Today: " + StatsViewModel.Totals(a.Today);
        MonthText = "This month: " + StatsViewModel.Totals(a.Month);
        var checks = a.LatencyHistory.Count;
        LatencyText = checks == 0 ? "Latency: no internet checks yet"
            : $"Latency (last hour): {(a.LatencyAverageMs is { } ms ? $"{ms} ms average" : "no answers")} · {a.FailedChecksPercent:0.#}% of {checks} checks failed";
        ReceiveHistory = a.ReceiveHistory.Select(v => (double)v).ToList();
        SendHistory = a.SendHistory.Select(v => (double)v).ToList();
        LatencyHistory = a.LatencyHistory.Select(v => v is { } x ? x : double.NaN).ToList();
    }
}

public sealed partial class AppUsageRowViewModel : ObservableObject, IKeyed
{
    public AppUsageRowViewModel(AppUsageInfo info)
    {
        Key = info.App + "|" + info.AdapterId;
        Update(info);
    }

    public string Key { get; }

    [ObservableProperty] public partial string App { get; set; } = "";
    [ObservableProperty] public partial string Network { get; set; } = "";
    [ObservableProperty] public partial string NowText { get; set; } = "";
    [ObservableProperty] public partial string TodayText { get; set; } = "";
    [ObservableProperty] public partial string MonthText { get; set; } = "";

    public void Update(AppUsageInfo a)
    {
        App = a.App;
        Network = a.NetworkName;
        NowText = a.ReceiveBps + a.SendBps > 0 ? $"↓ {StatsViewModel.Rate(a.ReceiveBps)} ↑ {StatsViewModel.Rate(a.SendBps)}" : "Idle";
        TodayText = StatsViewModel.Bytes(a.Today.Received + a.Today.Sent);
        MonthText = StatsViewModel.Bytes(a.Month.Received + a.Month.Sent);
    }
}

public sealed partial class ConnectionRowViewModel : ObservableObject, IKeyed
{
    public ConnectionRowViewModel(ConnectionInfo info)
    {
        Key = info.Id;
        Update(info);
    }

    public string Key { get; }

    [ObservableProperty] public partial string App { get; set; } = "";
    [ObservableProperty] public partial string Destination { get; set; } = "";
    [ObservableProperty] public partial string Network { get; set; } = "";
    [ObservableProperty] public partial string Rule { get; set; } = "";
    [ObservableProperty] public partial string BytesText { get; set; } = "";
    [ObservableProperty] public partial string AccessibleText { get; set; } = "";

    public void Update(ConnectionInfo c)
    {
        App = c.App;
        Destination = $"{c.Destination}:{c.Port} · {c.Protocol}";
        Network = c.Network;
        Rule = c.Rule;
        BytesText = $"↓ {StatsViewModel.Bytes(c.Download)} ↑ {StatsViewModel.Bytes(c.Upload)}";
        AccessibleText = $"{App} to {Destination} via {Network}, rule {Rule}";
    }
}
