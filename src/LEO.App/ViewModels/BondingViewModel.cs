using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leo.App.Services;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Microsoft.UI.Dispatching;

namespace Leo.App.ViewModels;

/// <summary>
/// Bonding page (M7, LEO.md §6.8, §22): bond groups, "Everything else uses", live per-member numbers. Every change comes
/// from an explicit user action; live numbers are polled every 2 s only while the page is open.
/// </summary>
public sealed partial class BondingViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly DispatcherQueue _dispatcher;
    private DispatcherQueueTimer? _timer;
    private bool _polling;
    private BondsLiveInfo _live = new(false, []);

    public BondingViewModel(ServiceConnection connection, DispatcherQueue dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _connection.SplitTunnelChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.NetworksChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.Changed += (_, _) => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    public ObservableCollection<BondCardViewModel> Bonds { get; } = [];

    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string UnruledText { get; set; } = "Everything else uses: Primary network";
    [ObservableProperty] public partial string? StatusText { get; set; }
    [ObservableProperty] public partial string? ErrorText { get; set; }

    public IReadOnlyList<BondInfo> BondInfos => _connection.SplitTunnel.Bonds ?? [];
    public string? UnruledBondId => _connection.SplitTunnel.UnruledBondId;

    /// <summary>Networks that can be bond members: physical networks by priority (VPNs can't be members).</summary>
    public IReadOnlyList<AdapterInfo> Candidates => _connection.Networks.Adapters.Where(a => a.IsPrimaryCandidate)
        .OrderBy(a => a.PriorityIndex ?? int.MaxValue).ThenBy(a => a.DisplayName).ToList();

    // ── Polling (page shown) ────────────────────────────────────────────────

    public void Start()
    {
        _timer ??= CreateTimer();
        _timer.Start();
        _ = PollAsync();
    }

    public void Stop() => _timer?.Stop();

    private DispatcherQueueTimer CreateTimer()
    {
        var t = _dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.Tick += async (_, _) => await PollAsync();
        return t;
    }

    private async Task PollAsync()
    {
        if (_polling || _connection.State != ConnectionState.Connected) return;
        _polling = true;
        try
        {
            var (live, _) = await _connection.QueryAsync(c => c.GetBondsLiveAsync());
            if (live is not null)
            {
                _live = live;
                foreach (var card in Bonds) card.UpdateLive(live.Bonds.FirstOrDefault(b => b.BondId == card.Info.Id), live.EngineRunning);
            }
        }
        catch (Exception ex)
        {
            UiLog.Write("Bond numbers refresh failed", ex);
        }
        finally
        {
            _polling = false;
        }
    }

    // ── Actions (from user events only) ─────────────────────────────────────

    /// <summary>Saves a bond; returns the service's error (null = saved).</summary>
    public async Task<string?> SaveAsync(Bond bond)
    {
        if (!CanEdit) return "Only administrators can change bonds.";
        UiLog.Write($"Bond: save '{bond.Name}' ({bond.Mode}, {bond.Members.Count} networks)");
        var (_, error) = await _connection.QueryAsync(c => c.SaveBondAsync(bond));
        return error?.Message;
    }

    public async Task DeleteAsync(BondCardViewModel card)
    {
        if (!CanEdit) return;
        UiLog.Write($"Bond: delete '{card.Info.Name}'");
        Show(await _connection.TryInvokeAsync(c => c.DeleteBondAsync(card.Info.Id)));
    }

    public async Task MoveAsync(BondCardViewModel card, bool up)
    {
        if (!CanEdit) return;
        Show(await _connection.TryInvokeAsync(c => c.MoveBondAsync(card.Info.Id, up)));
    }

    public async Task SetUnruledAsync(string? bondId)
    {
        if (!CanEdit) return;
        UiLog.Write($"Bond: everything else uses {(bondId is null ? "the Primary" : $"bond {bondId}")}");
        Show(await _connection.TryInvokeAsync(c => c.SetUnruledBondAsync(bondId)));
    }

    public void DismissError() => ErrorText = null;

    private void Show(string? error)
    {
        if (error is not null) ErrorText = error;
    }

    // ── Refresh ─────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var info = _connection.SplitTunnel;
        var connected = _connection.State == ConnectionState.Connected;
        CanEdit = connected && _connection.Access == ClientAccess.Full;
        var bonds = info.Bonds ?? [];
        IsEmpty = bonds.Count == 0;

        var unruled = bonds.FirstOrDefault(b => b.Id == info.UnruledBondId);
        UnruledText = unruled is null ? "Everything else uses: Primary network" : $"Everything else uses: bond '{unruled.Name}'";
        StatusText = !connected ? "The LEO service isn't connected, so bonds can't be shown or changed right now."
            : !info.Enabled ? "Split tunneling is switched off (Apps page), so bonds aren't used right now."
            : info.UnruledVia is { } via && unruled is not null ? $"{via} takes all unruled traffic right now, so 'Everything else' goes through that VPN instead of the bond."
            : unruled is not null && info.UnruledBondNow is null && info.State == SplitTunnelEngineState.Running
                ? $"No network in bond '{unruled.Name}' is available, so everything else uses your Primary network for now."
            : null;

        for (var i = Bonds.Count - 1; i >= 0; i--)
            if (bonds.All(b => b.Id != Bonds[i].Info.Id)) Bonds.RemoveAt(i);
        for (var i = 0; i < bonds.Count; i++)
        {
            var existing = Bonds.FirstOrDefault(c => c.Info.Id == bonds[i].Id);
            if (existing is null)
            {
                existing = new BondCardViewModel();
                Bonds.Insert(i, existing);
            }
            else if (Bonds.IndexOf(existing) != i)
            {
                Bonds.Move(Bonds.IndexOf(existing), i);
            }
            existing.Update(bonds[i], CanEdit, i > 0, i < bonds.Count - 1, bonds[i].Id == info.UnruledBondId);
            existing.UpdateLive(_live.Bonds.FirstOrDefault(b => b.BondId == bonds[i].Id), _live.EngineRunning);
        }
    }
}

public sealed partial class BondCardViewModel : ObservableObject
{
    public BondInfo Info { get; private set; } = null!;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string ModeText { get; set; } = "";
    [ObservableProperty] public partial string MembersText { get; set; } = "";
    [ObservableProperty] public partial string RouteText { get; set; } = "";
    [ObservableProperty] public partial string UsageText { get; set; } = "";
    [ObservableProperty] public partial string HintsText { get; set; } = "";
    [ObservableProperty] public partial bool HasHints { get; set; }
    [ObservableProperty] public partial string LiveText { get; set; } = "";
    [ObservableProperty] public partial bool HasLive { get; set; }
    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool CanMoveUp { get; set; }
    [ObservableProperty] public partial bool CanMoveDown { get; set; }

    public string EditAutomationName => $"Edit bond {Name}";
    public string DeleteAutomationName => $"Delete bond {Name}";
    public string UpAutomationName => $"Move bond {Name} up";
    public string DownAutomationName => $"Move bond {Name} down";

    public void Update(BondInfo info, bool canEdit, bool canUp, bool canDown, bool isUnruled)
    {
        Info = info;
        Name = info.Name;
        ModeText = BondRules.ModeName(info.Mode) + " · " + ModeExplanation(info.Mode)
            + (info.OnAllDown == OnTargetDown.Block ? " · Blocks when no network in it works" : "");
        MembersText = string.Join("\n", info.Members.Select(m =>
            $"{m.Name}{(info.Mode is BondMode.Weighted or BondMode.EveryConnection ? $" · weight {m.Weight}" : "")}{(m.Metered ? " · metered" : "")} · "
            + (!m.Present ? "not plugged in" : m.Usable ? "working" : m.Connected ? "no internet" : "not connected")));
        RouteText = "Now: " + info.RouteNow;
        var uses = new List<string>();
        if (info.RuleCount > 0) uses.Add($"{info.RuleCount} rule(s) use it");
        if (isUnruled) uses.Add("everything else uses it");
        UsageText = uses.Count == 0 ? "Not used yet: pick it as the network of an app or URL rule, or for everything else." : string.Join(" · ", uses).ToUpperFirst();
        HintsText = string.Join("\n", info.Hints);
        HasHints = info.Hints.Count > 0;
        CanEdit = canEdit;
        CanMoveUp = canEdit && canUp;
        CanMoveDown = canEdit && canDown;
        OnPropertyChanged(nameof(EditAutomationName));
        OnPropertyChanged(nameof(DeleteAutomationName));
        OnPropertyChanged(nameof(UpAutomationName));
        OnPropertyChanged(nameof(DownAutomationName));
    }

    public void UpdateLive(BondLive? live, bool engineRunning)
    {
        if (Info is null) return;
        if (!engineRunning || live is null || !Info.InUse)
        {
            HasLive = false;
            LiveText = "";
            return;
        }
        var total = live.Members.Sum(m => m.Connections);
        LiveText = "Open connections: " + string.Join(" · ", live.Members.Select(m =>
        {
            var name = Info.Members.FirstOrDefault(x => x.AdapterId.Equals(m.AdapterId, StringComparison.OrdinalIgnoreCase))?.Name ?? "Network";
            var share = total > 0 ? $" ({100.0 * m.Connections / total:0}%)" : "";
            var engine = m.EngineAlive == false ? ", engine check failed" : "";
            return $"{name} {m.Connections}{share}, {Bytes(m.Download)} down{engine}";
        }));
        HasLive = true;
    }

    public static string ModeExplanation(BondMode mode) => mode switch
    {
        BondMode.BySite => "each site stays on one network; different sites spread",
        BondMode.Weighted => "like By site, with more sites on networks with a higher weight",
        BondMode.EveryConnection => "every new connection takes the next network, more often the ones with a higher weight (best for big downloads; a site may see two addresses; give a slower network a lower weight)",
        BondMode.Fastest => "everything uses the network with the lowest delay",
        _ => "",
    };

    private static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B",
    };
}

internal static class StringExtensions
{
    public static string ToUpperFirst(this string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
