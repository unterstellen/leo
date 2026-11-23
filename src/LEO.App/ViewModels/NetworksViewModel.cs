using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leo.App.Services;
using Leo.Ipc;
using Microsoft.UI.Dispatching;

namespace Leo.App.ViewModels;

/// <summary>Networks page + tray state: adapters, priority and Primary (LEO.md §6.1–6.3).</summary>
public sealed partial class NetworksViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly DispatcherQueue _dispatcher;

    public NetworksViewModel(ServiceConnection connection, DispatcherQueue dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _connection.NetworksChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.Changed += (_, _) => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    /// <summary>Primary candidates in priority order, then VPNs/others.</summary>
    public ObservableCollection<AdapterCardViewModel> Adapters { get; } = [];

    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool IsReadOnlyNoticeVisible { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string EmptyText { get; set; } = "Connecting to the LEO service…";
    [ObservableProperty] public partial string PrimaryTitle { get; set; } = "—";
    [ObservableProperty] public partial string PrimaryDetail { get; set; } = "";
    [ObservableProperty] public partial string PrimaryGlyph { get; set; } = "";
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial bool FailoverActive { get; set; }
    [ObservableProperty] public partial string FailoverText { get; set; } = "";
    [ObservableProperty] public partial bool AutoFailover { get; set; } = true;

    private bool _syncingAutoFailover;

    partial void OnAutoFailoverChanged(bool value)
    {
        // Only a real change versus the service's current setting is sent (never a binding echo).
        if (_syncingAutoFailover || value == _connection.Networks.AutoFailover || !CanEdit) return;
        _ = SetAutoFailoverAsync(value);
    }

    private async Task SetAutoFailoverAsync(bool value)
    {
        var error = await _connection.TryInvokeAsync(c => c.SetAutoFailoverAsync(value));
        if (error is not null)
        {
            ShowError(error);
            Sync(() => AutoFailover = _connection.Networks.AutoFailover);
        }
    }

    private void Sync(Action set)
    {
        _syncingAutoFailover = true;
        try { set(); } finally { _syncingAutoFailover = false; }
    }

    /// <summary>Raised after every refresh so the tray menu can rebuild.</summary>
    public event EventHandler? Refreshed;

    public NetworkSnapshot Snapshot => _connection.Networks;

    public ServiceConnection Connection => _connection;

    public async Task SetPrimaryAsync(AdapterCardViewModel card) =>
        ShowError(await _connection.TryInvokeAsync(c => c.SetPrimaryAsync(card.Id)));

    /// <summary>Used by the tray menu (UI thread).</summary>
    public async Task SetPrimaryAsync(string adapterId) =>
        ShowError(await _connection.TryInvokeAsync(c => c.SetPrimaryAsync(adapterId)));

    public async Task MoveAsync(AdapterCardViewModel card, int delta)
    {
        var order = Adapters.Where(a => a.Info.PriorityIndex is not null)
            .OrderBy(a => a.Info.PriorityIndex).Select(a => a.Id).ToList();
        var i = order.IndexOf(card.Id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        ShowError(await _connection.TryInvokeAsync(c => c.SetPriorityOrderAsync(order)));
    }

    public async Task RenameAsync(AdapterCardViewModel card, string? adapterNickname, string? networkNickname)
    {
        var error = await _connection.TryInvokeAsync(async c =>
        {
            if (!string.Equals(adapterNickname ?? "", card.Info.Nickname ?? "", StringComparison.Ordinal))
            {
                await c.SetAdapterNicknameAsync(card.Id, adapterNickname);
            }
            if (card.Info.Network is { } n && !string.Equals(networkNickname ?? "", n.Nickname ?? "", StringComparison.Ordinal))
            {
                await c.SetNetworkNicknameAsync(n.Key, networkNickname);
            }
        });
        ShowError(error);
    }

    public void DismissError() => ErrorText = null;

    /// <summary>Display name of an adapter by id (null if unknown).</summary>
    public string? NameOf(string? adapterId) =>
        adapterId is null ? null : _connection.Networks.Adapters.FirstOrDefault(a => a.Id == adapterId)?.DisplayName;

    /// <summary>Secondary stations that follow the given primary Wi-Fi adapter.</summary>
    public IEnumerable<AdapterInfo> SecondariesOf(string primaryId) =>
        _connection.Networks.Adapters.Where(a => a.FollowsAdapterId == primaryId);

    /// <summary>Only from a real button click on a card.</summary>
    public async Task ToggleDualLinkAsync(AdapterCardViewModel card)
    {
        if (!CanEdit || !card.Info.DualLinkSupported) return;
        var enable = !card.Info.DualLinkEnabled;
        UiLog.Write($"Dual link: {(enable ? "on" : "off")} for {card.Info.DisplayName}");
        ShowError(await _connection.TryInvokeAsync(c => c.SetDualLinkAsync(card.Id, enable)));
    }

    /// <summary>"Let unruled traffic skip this VPN" (M6), from the card's checkbox Click.</summary>
    public async Task SetSkipUnruledAsync(AdapterCardViewModel card, bool skip)
    {
        if (!CanEdit || card.Info.Vpn is not { } vpn) return;
        UiLog.Write($"VPN {card.Info.Id}: unruled traffic {(skip ? "skips" : "follows")} it");
        ShowError(await _connection.TryInvokeAsync(c => c.SetVpnSettingsAsync(vpn.Key, skip)));
    }

    public string? PrimaryName() => _connection.Networks.Adapters.FirstOrDefault(a => a.IsPrimary)?.DisplayName;

    private void ShowError(string? error)
    {
        if (error is not null) ErrorText = error;
    }

    private void Refresh()
    {
        var snapshot = _connection.Networks;
        var connected = _connection.State == ConnectionState.Connected;
        CanEdit = connected && _connection.Access == ClientAccess.Full;
        IsReadOnlyNoticeVisible = connected && !CanEdit;
        Sync(() => AutoFailover = snapshot.AutoFailover);

        FailoverActive = connected && snapshot.FailoverActive;
        if (FailoverActive)
        {
            var preferred = snapshot.Adapters.Where(a => a.IsDemoted).OrderBy(a => a.PriorityIndex).FirstOrDefault();
            var current = snapshot.Adapters.FirstOrDefault(a => a.IsPrimary);
            var why = preferred?.Health == Leo.Core.Networking.HealthState.Captive ? "needs a sign-in" : "has no internet";
            var back = preferred?.FailbackInSeconds is { } s ? $" LEO switches back about {s}s after it recovers." : " LEO switches back once it recovers.";
            FailoverText = preferred is null || current is null
                ? "LEO is routing around a network without internet."
                : $"{preferred.DisplayName} {why}, so traffic is going through {current.DisplayName}.{back}";
        }

        var candidates = snapshot.Adapters.Count(a => a.PriorityIndex is not null);

        // Update in place by id to keep the list stable (no flicker, focus kept).
        var incoming = snapshot.Adapters.ToList();
        for (var i = Adapters.Count - 1; i >= 0; i--)
        {
            if (!incoming.Any(a => a.Id == Adapters[i].Id)) Adapters.RemoveAt(i);
        }
        for (var i = 0; i < incoming.Count; i++)
        {
            var info = incoming[i];
            var existing = Adapters.FirstOrDefault(a => a.Id == info.Id);
            if (existing is null)
            {
                existing = new AdapterCardViewModel(this, info);
                Adapters.Insert(Math.Min(i, Adapters.Count), existing);
            }
            else
            {
                var at = Adapters.IndexOf(existing);
                if (at != i) Adapters.Move(at, Math.Min(i, Adapters.Count - 1));
            }
            existing.Update(info, candidates, CanEdit);
        }

        IsEmpty = Adapters.Count == 0;
        EmptyText = connected ? "No network adapters found." : "The LEO service is not running, so networks can't be shown.";

        var primary = snapshot.Adapters.FirstOrDefault(a => a.IsPrimary);
        if (primary is not null)
        {
            PrimaryTitle = primary.DisplayName;
            var networkName = primary.Network is { } n ? (n.Nickname ?? n.Name) : "";
            PrimaryDetail = string.Join("  ·  ", new[] { networkName, AdapterCardViewModel.HealthText(primary) }.Where(s => s.Length > 0));
            PrimaryGlyph = AdapterCardViewModel.GlyphFor(primary.Kind);
        }
        else
        {
            PrimaryTitle = connected ? "No network connected" : "—";
            PrimaryDetail = "";
            PrimaryGlyph = "";
        }

        Refreshed?.Invoke(this, EventArgs.Empty);
    }
}
