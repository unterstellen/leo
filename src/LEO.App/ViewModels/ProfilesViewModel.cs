using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leo.App.Services;
using Leo.Core.Networking;
using Leo.Core.Profiles;
using Leo.Ipc;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Leo.App.ViewModels;

/// <summary>An adapter a profile trigger can watch.</summary>
public sealed record TriggerAdapterOption(string Id, string? Mac, string Name);

/// <summary>Profiles page and tray switcher (LEO.md §6.6, §19). Every change comes from a click.</summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly DispatcherQueue _dispatcher;

    public ProfilesViewModel(ServiceConnection connection, DispatcherQueue dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _connection.ProfilesChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.NetworksChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.Changed += (_, _) => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    public ObservableCollection<ProfileCardViewModel> Profiles { get; } = [];

    public ProfilesInfo Info => _connection.Profiles;

    /// <summary>Raised after every refresh (the tray menu rebuilds itself).</summary>
    public event EventHandler? Refreshed;

    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string ActiveTitle { get; set; } = "";
    [ObservableProperty] public partial string ReasonText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity ReasonSeverity { get; set; } = InfoBarSeverity.Informational;
    [ObservableProperty] public partial bool ShowBackToAutomatic { get; set; }
    [ObservableProperty] public partial string AutoSwitchButtonText { get; set; } = "Turn off automatic switching";
    [ObservableProperty] public partial string? ErrorText { get; set; }

    public IReadOnlyList<TriggerAdapterOption> Adapters =>
        _connection.Networks.Adapters.Where(a => a.Kind != AdapterKind.System)
            .OrderBy(a => a.PriorityIndex ?? int.MaxValue)
            .Select(a => new TriggerAdapterOption(a.Id, a.Mac, a.DisplayName + (a.Status == AdapterStatus.Up ? "" : " (not connected)")))
            .ToList();

    /// <summary>Wi-Fi names to suggest: connected networks, networks in range and SSIDs used by existing triggers.</summary>
    public async Task<IReadOnlyList<string>> SuggestSsidsAsync()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in _connection.Networks.Adapters.Where(a => a.Kind == AdapterKind.WiFi))
        {
            if (a.Network?.Key.StartsWith("wifi:", StringComparison.Ordinal) == true) set.Add(a.Network.Key[5..]);
            foreach (var pin in a.PinnedSsids) set.Add(pin);
        }
        foreach (var t in Info.Profiles.SelectMany(p => p.Triggers).Where(t => t.Kind != ProfileTriggerKind.AdapterConnected)) set.Add(t.Value);
        var wifi = _connection.Networks.Adapters.FirstOrDefault(a => a.Kind == AdapterKind.WiFi && a.FollowsAdapterId is null);
        if (wifi is not null)
        {
            var (list, _) = await _connection.QueryAsync(c => c.GetWifiNetworksAsync(wifi.Id, false));
            foreach (var n in list ?? []) if (!string.IsNullOrEmpty(n.Ssid)) set.Add(n.Ssid);
        }
        return [.. set];
    }

    public void Refresh()
    {
        IsConnected = _connection.State == ConnectionState.Connected;
        CanEdit = IsConnected && _connection.Access == ClientAccess.Full;
        var info = Info;

        ActiveTitle = IsConnected ? $"Active profile: {info.ActiveProfileName}" : "Not connected to the LEO service";
        ReasonText = !IsConnected ? "" : info.PendingText is { } pending ? info.Reason + " " + pending : info.Reason;
        ReasonSeverity = info.Paused ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        ShowBackToAutomatic = CanEdit && (info.Manual || info.Paused) && info.AutoSwitch;
        AutoSwitchButtonText = info.AutoSwitch ? "Turn off automatic switching" : "Turn on automatic switching";

        var nonDefault = info.Profiles.Count(p => !p.IsDefault);
        Profiles.Clear();
        var index = 0;
        foreach (var p in info.Profiles)
        {
            Profiles.Add(new ProfileCardViewModel(p, CanEdit, canMoveUp: !p.IsDefault && index > 0, canMoveDown: !p.IsDefault && index < nonDefault - 1));
            index++;
        }
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    // ── Actions (from user events only) ─────────────────────────────────────

    public async Task ActivateAsync(string? profileId)
    {
        if (!CanEdit) return;
        UiLog.Write(profileId is null ? "Profiles: back to automatic" : $"Profiles: switch to {profileId[..Math.Min(8, profileId.Length)]}");
        Show(await _connection.TryInvokeAsync(c => c.ActivateProfileAsync(profileId)));
    }

    public async Task ToggleAutoSwitchAsync()
    {
        if (!CanEdit) return;
        var enable = !Info.AutoSwitch;
        UiLog.Write($"Profiles: automatic switching {(enable ? "on" : "off")}");
        Show(await _connection.TryInvokeAsync(c => c.SetProfileAutoSwitchAsync(enable)));
    }

    /// <summary>Saves name + triggers; returns the service's error (null = saved).</summary>
    public async Task<string?> SaveAsync(ProfileEdit edit)
    {
        if (!CanEdit) return "Only administrators can change profiles.";
        UiLog.Write($"Profiles: {(edit.Id is null ? "add" : "edit " + edit.Id[..Math.Min(8, edit.Id.Length)])} ({edit.Triggers.Count} trigger(s))");
        var error = await _connection.TryInvokeAsync(c => c.SaveProfileAsync(edit));
        Show(error);
        return error;
    }

    public async Task DeleteAsync(ProfileCardViewModel card)
    {
        if (!CanEdit) return;
        UiLog.Write($"Profiles: delete {card.Info.Id[..Math.Min(8, card.Info.Id.Length)]}");
        Show(await _connection.TryInvokeAsync(c => c.DeleteProfileAsync(card.Info.Id)));
    }

    public async Task MoveAsync(ProfileCardViewModel card, bool up)
    {
        if (!CanEdit) return;
        Show(await _connection.TryInvokeAsync(c => c.MoveProfileAsync(card.Info.Id, up)));
    }

    public void DismissError() => ErrorText = null;

    private void Show(string? error) => ErrorText = error;
}

public sealed class ProfileCardViewModel(ProfileInfo info, bool canEdit, bool canMoveUp, bool canMoveDown)
{
    public ProfileInfo Info { get; } = info;
    public string Name => Info.Name;
    public bool IsActive => Info.IsActive;
    public bool CanEdit => canEdit;
    public bool CanActivate => canEdit && !Info.IsActive;
    public bool CanDelete => canEdit && !Info.IsDefault;
    public bool CanMoveUp => canEdit && canMoveUp;
    public bool CanMoveDown => canEdit && canMoveDown;
    public bool IsNotDefault => !Info.IsDefault;
    public string Glyph => Info.IsDefault ? "" : "";
    public string ActivateText => $"Switch to {Info.Name}";

    public string TriggerText => Info.IsDefault
        ? "Used when no other profile's trigger matches."
        : Info.TriggerTexts.Count == 0 ? "Manual only: switch to it here or from the tray."
        : "Turns on when " + string.Join(", or ", Info.TriggerTexts) + "."
          + (Info.TriggerMatches && !Info.IsActive ? " (Matches right now.)" : "");

    public string SummaryText => Info.Summary;

    public string PriorityText => Info.PriorityNames.Count == 0
        ? "Priority: LEO's default order (wired first)"
        : "Priority: " + string.Join(" → ", Info.PriorityNames);
}
