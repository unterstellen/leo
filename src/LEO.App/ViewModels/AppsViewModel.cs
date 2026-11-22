using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leo.App.Services;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leo.App.ViewModels;

/// <summary>A network an app can be sent through.</summary>
public sealed record NetworkOption(string Id, string Name, bool IsUp, bool IsPrimary);

/// <summary>Apps page: split tunneling by app (LEO.md §6.4, M3). All changes come from explicit user actions.</summary>
public sealed partial class AppsViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly DispatcherQueue _dispatcher;

    public AppsViewModel(ServiceConnection connection, DispatcherQueue dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _connection.SplitTunnelChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.NetworksChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.Changed += (_, _) => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    public ObservableCollection<AppRuleCardViewModel> Rules { get; } = [];

    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool MasterEnabled { get; set; } = true;
    [ObservableProperty] public partial string MasterButtonText { get; set; } = "Turn off";
    [ObservableProperty] public partial string StatusTitle { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string? ErrorText { get; set; }

    public IReadOnlyList<NetworkOption> Networks =>
        _connection.Networks.Adapters.Where(a => a.IsPrimaryCandidate)
            .OrderBy(a => a.PriorityIndex ?? int.MaxValue)
            .Select(a => new NetworkOption(a.Id, a.DisplayName, a.Status == AdapterStatus.Up, a.IsPrimary)).ToList();

    public string NetworkName(string adapterId) =>
        _connection.Networks.Adapters.FirstOrDefault(a => a.Id == adapterId)?.DisplayName ?? "Network not connected";

    // ── Actions (from user events only) ─────────────────────────────────────

    public async Task ToggleMasterAsync()
    {
        if (!CanEdit) return;
        var enable = !_connection.SplitTunnel.Enabled;
        UiLog.Write($"Split tunneling: switch {(enable ? "on" : "off")}");
        Show(await _connection.TryInvokeAsync(c => c.SetSplitTunnelEnabledAsync(enable)));
    }

    /// <summary>Creates a rule for a picked app, sending it through the best network that isn't the Primary.</summary>
    public async Task AddAsync(AppCandidate app)
    {
        if (!CanEdit) return;
        if (_connection.SplitTunnel.Rules.Any(r => string.Equals(r.Pattern, app.Path, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorText = $"{app.Name} already has a rule.";
            return;
        }
        var nets = Networks;
        var target = nets.FirstOrDefault(n => n.IsUp && !n.IsPrimary) ?? nets.FirstOrDefault(n => n.IsPrimary) ?? nets.FirstOrDefault();
        if (target is null)
        {
            ErrorText = "No network is available to send apps through.";
            return;
        }
        var rule = new AppRule { DisplayName = app.Name, Kind = app.Kind, Pattern = app.Path, TargetAdapterId = target.Id };
        UiLog.Write($"App rule: add '{app.Name}' ({app.Kind}) -> {target.Name}");
        Show(await _connection.TryInvokeAsync(c => c.SaveAppRuleAsync(rule)));
    }

    public async Task SaveAsync(AppRule rule, string what)
    {
        if (!CanEdit) return;
        UiLog.Write($"App rule '{rule.DisplayName}': {what}");
        Show(await _connection.TryInvokeAsync(c => c.SaveAppRuleAsync(rule)));
    }

    public async Task DeleteAsync(AppRuleCardViewModel card)
    {
        if (!CanEdit) return;
        UiLog.Write($"App rule: delete '{card.Info.DisplayName}'");
        Show(await _connection.TryInvokeAsync(c => c.DeleteAppRuleAsync(card.Info.Id)));
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
        MasterEnabled = info.Enabled;
        MasterButtonText = info.Enabled ? "Turn off" : "Turn on";

        (StatusSeverity, StatusTitle, StatusText) = !connected
            ? (InfoBarSeverity.Warning, "LEO service not connected", "App rules can't be shown or changed right now.")
            : info.State switch
            {
                SplitTunnelEngineState.Running => (InfoBarSeverity.Success, "Split tunneling is on",
                    "Apps below use their chosen network. Everything else uses your Primary network."),
                SplitTunnelEngineState.Starting => (InfoBarSeverity.Informational, "Starting…", "LEO is starting its routing engine."),
                SplitTunnelEngineState.Error => (InfoBarSeverity.Error, "Split tunneling paused", info.Message ?? "The routing engine stopped."),
                SplitTunnelEngineState.NotInstalled => (InfoBarSeverity.Error, "Routing engine missing", info.Message ?? "Reinstall LEO."),
                _ when !info.Enabled => (InfoBarSeverity.Informational, "Split tunneling is off",
                    "Your rules are kept, but every app uses the Primary network until you turn it back on."),
                _ => (InfoBarSeverity.Informational, "No active rules",
                    "Add an app to choose which network it uses. Until then LEO doesn't touch app traffic at all."),
            };

        // Update cards in place by id.
        var incoming = info.Rules;
        for (var i = Rules.Count - 1; i >= 0; i--)
            if (incoming.All(r => r.Id != Rules[i].Info.Id)) Rules.RemoveAt(i);
        for (var i = 0; i < incoming.Count; i++)
        {
            var existing = Rules.FirstOrDefault(c => c.Info.Id == incoming[i].Id);
            if (existing is null)
            {
                existing = new AppRuleCardViewModel(this);
                Rules.Insert(Math.Min(i, Rules.Count), existing);
            }
            existing.Update(incoming[i], CanEdit);
        }
        IsEmpty = Rules.Count == 0;
    }
}

public sealed partial class AppRuleCardViewModel(AppsViewModel owner) : ObservableObject
{
    public AppRuleInfo Info { get; private set; } = null!;
    public AppsViewModel Owner => owner;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string PatternText { get; set; } = "";
    [ObservableProperty] public partial string TargetText { get; set; } = "";
    [ObservableProperty] public partial string DownText { get; set; } = "";
    [ObservableProperty] public partial string RouteText { get; set; } = "";
    [ObservableProperty] public partial string ChildrenText { get; set; } = "";
    [ObservableProperty] public partial bool IncludeChildren { get; set; }
    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool RouteIsWarning { get; set; }
    [ObservableProperty] public partial BitmapImage? Icon { get; set; }
    [ObservableProperty] public partial bool HasIcon { get; set; }
    [ObservableProperty] public partial bool NoIcon { get; set; } = true;

    private string? _iconFor;

    public void Update(AppRuleInfo info, bool canEdit)
    {
        Info = info;
        CanEdit = canEdit;
        Name = info.DisplayName;
        PatternText = info.Kind switch
        {
            AppMatchKind.Folder => $"{info.Pattern} (and its subfolders)",
            AppMatchKind.NamePattern => $"{info.Pattern} in any folder",
            _ => info.Pattern,
        };
        TargetText = owner.NetworkName(info.TargetAdapterId);
        DownText = info.OnTargetDown == OnTargetDown.Block ? "If unavailable: block" : "If unavailable: use Primary";
        RouteText = "Now: " + info.RouteNow;
        RouteIsWarning = info.Enabled && !info.TargetUsable;
        IncludeChildren = info.IncludeChildren;
        IsEnabled = info.Enabled;
        ChildrenText = info.IncludeChildren
            ? (info.ChildPrograms > 0 ? $"Also covers programs it starts ({info.ChildPrograms} so far)" : "Also covers programs it starts")
            : "Only this program";
        _ = LoadIconAsync(info);
    }

    private async Task LoadIconAsync(AppRuleInfo info)
    {
        if (info.Kind != AppMatchKind.ExactPath || _iconFor == info.Pattern) return;
        _iconFor = info.Pattern;
        Icon = await AppCatalog.IconAsync(info.Pattern);
        HasIcon = Icon is not null;
        NoIcon = !HasIcon;
    }

    public AppRule ToRule() => new()
    {
        Id = Info.Id, DisplayName = Info.DisplayName, Kind = Info.Kind, Pattern = Info.Pattern,
        TargetAdapterId = Info.TargetAdapterId, OnTargetDown = Info.OnTargetDown,
        IncludeChildren = Info.IncludeChildren, Enabled = Info.Enabled,
    };
}
