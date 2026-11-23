using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Leo.App.Services;
using Leo.Ipc;

namespace Leo.App.ViewModels;

/// <summary>
/// "Wi-Fi networks" dialog for one Wi-Fi adapter: scan, pin ("keep connected"), connect (LEO.md §5.5).
/// <para>
/// Every action goes through <see cref="TryBeginAction"/>: one at a time, a short settle window after each,
/// and only for rows that are still in the current list. The list is rebuilt after each action, and its
/// recycled row elements once replayed a queued click onto a *different* network — this gate makes that
/// impossible.
/// </para>
/// </summary>
public sealed partial class WifiNetworksViewModel(ServiceConnection connection, AdapterInfo adapter, bool canEdit) : ObservableObject
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);

    private List<string> _pins = [.. adapter.PinnedSsids];
    private DateTime _quietUntil = DateTime.MinValue;

    public string AdapterName => adapter.DisplayName;
    public bool CanEdit => canEdit;
    public bool IsPrimaryAdapter => adapter.IsPrimary;
    public ObservableCollection<WifiRowViewModel> Networks { get; } = [];

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string? ErrorText { get; set; }

    /// <summary>
    /// Dialog opened: show what Windows already knows at once, then scan fresh (Windows' cached list can be minutes
    /// old and mark networks nearby as out of range). The list refreshes by itself when the scan is done.
    /// </summary>
    public async Task OpenAsync()
    {
        await LoadAsync(rescan: false);
        if (ErrorText is null) await LoadAsync(rescan: true);
    }

    public async Task LoadAsync(bool rescan)
    {
        IsBusy = true;
        ErrorText = null;
        StatusText = rescan ? "Scanning…" : "Loading…";
        var (list, error) = await connection.QueryAsync(c => c.GetWifiNetworksAsync(adapter.Id, rescan));
        IsBusy = false;
        if (error is not null || list is null)
        {
            ErrorText = error?.Message ?? "Couldn't read Wi-Fi networks.";
            StatusText = "";
            return;
        }

        Networks.Clear();
        foreach (var n in list) Networks.Add(new WifiRowViewModel(this, n));
        _pins = list.Where(n => n.IsPinned).Select(n => n.Ssid)
            .OrderBy(s => _pins.IndexOf(s) is var i && i >= 0 ? i : int.MaxValue).ToList();
        _quietUntil = DateTime.UtcNow + SettleTime;

        StatusText = list.Count == 0 ? "No networks in range." : $"{list.Count(n => n.IsVisible)} network(s) in range.";
    }

    [RelayCommand]
    private Task RescanAsync() => IsBusy ? Task.CompletedTask : LoadAsync(rescan: true);

    /// <summary>Gate for every user action (see class remarks).</summary>
    private bool TryBeginAction(WifiRowViewModel row, string what)
    {
        if (!CanEdit) return false;
        if (IsBusy || DateTime.UtcNow < _quietUntil || !Networks.Contains(row))
        {
            UiLog.Write($"Wi-Fi: ignored {what} '{row.Ssid}' on {AdapterName} (busy={IsBusy}, stale={!Networks.Contains(row)})");
            return false;
        }
        UiLog.Write($"Wi-Fi: {what} '{row.Ssid}' on {AdapterName}");
        return true;
    }

    internal async Task SetPinAsync(WifiRowViewModel row, bool pinned)
    {
        if (!TryBeginAction(row, pinned ? "pin" : "unpin"))
        {
            row.IsPinned = _pins.Contains(row.Ssid); // undo the checkbox's visual toggle
            return;
        }
        var pins = _pins.Where(p => p != row.Ssid).ToList();
        if (pinned) pins.Add(row.Ssid);

        IsBusy = true;
        var error = await connection.TryInvokeAsync(c => c.SetWifiPinsAsync(adapter.Id, pins));
        IsBusy = false;
        _quietUntil = DateTime.UtcNow + SettleTime;
        if (error is not null)
        {
            ErrorText = error;
            row.IsPinned = _pins.Contains(row.Ssid);
            return;
        }
        _pins = pins;
        row.IsPinned = pinned;
        StatusText = _pins.Count == 0
            ? $"{AdapterName} won't be kept on any particular network."
            : $"{AdapterName} will stay on: {string.Join(", ", _pins)}.";
    }

    internal async Task ConnectAsync(WifiRowViewModel row)
    {
        if (!TryBeginAction(row, "connect")) return;

        if (row.ShowPassword && string.IsNullOrEmpty(row.Password))
        {
            ErrorText = $"Enter the password for '{row.Ssid}'.";
            return;
        }

        IsBusy = true;
        ErrorText = null;
        StatusText = $"Connecting to '{row.Ssid}'…";
        var password = row.ShowPassword ? row.Password : null;
        var error = await connection.InvokeAsync(c => c.ConnectWifiAsync(adapter.Id, row.Ssid, password));
        IsBusy = false;
        row.Password = ""; // never keep it around

        if (error?.Code == LeoErrorCodes.PasswordRequired)
        {
            row.ShowPassword = true;
            _quietUntil = DateTime.UtcNow + SettleTime;
            StatusText = $"'{row.Ssid}' needs its password the first time.";
            return;
        }
        if (error is not null)
        {
            ErrorText = error.Message;
            StatusText = "";
            _quietUntil = DateTime.UtcNow + SettleTime;
            return;
        }
        await LoadAsync(rescan: false);
        StatusText = $"Connected to '{row.Ssid}'.";
    }

    internal async Task DisconnectAsync(WifiRowViewModel row)
    {
        if (!TryBeginAction(row, "disconnect")) return;
        IsBusy = true;
        var error = await connection.TryInvokeAsync(c => c.DisconnectWifiAsync(adapter.Id));
        IsBusy = false;
        if (error is not null) ErrorText = error;
        await LoadAsync(rescan: false);
    }
}

public sealed partial class WifiRowViewModel : ObservableObject
{
    private readonly WifiNetworksViewModel _owner;

    public WifiRowViewModel(WifiNetworksViewModel owner, WifiNetworkInfo info)
    {
        _owner = owner;
        Info = info;
        IsPinned = info.IsPinned;
        ShowPassword = info.IsVisible && info.CanJoinWithPassword && info.IsSecured && !info.HasProfile && !info.KnownElsewhere;
    }

    public WifiNetworkInfo Info { get; }
    public string Ssid => Info.Ssid;
    public bool IsConnected => Info.IsConnected;
    public bool CanEdit => _owner.CanEdit;
    public bool CanConnect => _owner.CanEdit && Info.IsVisible && !Info.IsConnected;
    public bool CanDisconnect => _owner.CanEdit && Info.IsConnected;
    public string SignalText => Info.IsVisible ? $"{Info.SignalPercent}%" : "—";

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!Info.IsVisible) parts.Add("Out of range");
            else if (Info.Security.Length > 0) parts.Add(Info.Security);
            if (Info.IsConnected) parts.Add("Connected");
            else if (Info.HasProfile) parts.Add("Saved");
            else if (Info.KnownElsewhere) parts.Add("Saved on another adapter");
            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty] public partial bool IsPinned { get; set; }
    [ObservableProperty] public partial bool ShowPassword { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";

    public string ConnectLabel => "Connect";

    // Screen readers (and UI automation) see each row's controls flat, so names must say which network.
    public string ConnectAutomationName => $"Connect to {Ssid}";
    public string PinAutomationName => $"Keep connected to {Ssid}";
    public string DisconnectAutomationName => $"Disconnect from {Ssid}";
    public string PasswordAutomationName => $"Password for {Ssid}";

    // Called only from real user Click events in WifiNetworksPanel.
    public Task OnPinClickedAsync(bool pinned) => _owner.SetPinAsync(this, pinned);
    public Task OnConnectClickedAsync() => _owner.ConnectAsync(this);
    public Task OnDisconnectClickedAsync() => _owner.DisconnectAsync(this);
}
