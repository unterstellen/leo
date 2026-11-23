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
/// <summary>A network a rule may target: physical networks, plus VPNs (M6; <see cref="IsVpn"/>, IsUp = connected).</summary>
public sealed record NetworkOption(string Id, string Name, bool IsUp, bool IsPrimary, bool IsVpn = false)
{
    /// <summary>Menu text: "WiFi (Primary)", "Tailscale · VPN (not connected)".</summary>
    public string Label => Name + (IsVpn ? " · VPN" : "") + (IsPrimary ? " (Primary)" : "") + (IsUp ? "" : " (not connected)");

    /// <summary>Rule targets for a picker, from the service's snapshot: physical networks by priority, then VPNs.</summary>
    public static IReadOnlyList<NetworkOption> From(NetworkSnapshot snap) =>
        snap.Adapters.Where(a => a.IsPrimaryCandidate || a.Vpn is not null)
            .OrderBy(a => a.Vpn is not null).ThenBy(a => a.PriorityIndex ?? int.MaxValue).ThenBy(a => a.DisplayName)
            .Select(a => new NetworkOption(a.Id, a.DisplayName, a.Vpn?.Connected ?? a.Status == AdapterStatus.Up, a.IsPrimary, a.Vpn is not null)).ToList();
}

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

    public IReadOnlyList<NetworkOption> Networks => NetworkOption.From(_connection.Networks);

    [ObservableProperty] public partial string? VpnBanner { get; set; }

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
        var physical = nets.Where(n => !n.IsVpn).ToList();
        var target = physical.FirstOrDefault(n => n.IsUp && !n.IsPrimary) ?? physical.FirstOrDefault(n => n.IsPrimary) ?? physical.FirstOrDefault();
        if (target is null)
        {
            ErrorText = "No network is available to send apps through.";
            return;
        }
        var rule = new AppRule { DisplayName = app.Name, Kind = app.Kind, Pattern = app.Path, TargetAdapterId = target.Id };
        UiLog.Write($"App rule: add '{app.Name}' ({app.Kind}) -> {target.Name}");
        Show(await _connection.TryInvokeAsync(c => c.SaveAppRuleAsync(rule)));
    }

    /// <summary>Installed VPN clients LEO knows (from the service), as app candidates.</summary>
    public async Task<List<AppCandidate>> VpnClientsAsync()
    {
        IReadOnlyList<VpnClientInfo> list = [];
        var error = await _connection.TryInvokeAsync(async c => list = await c.GetVpnClientPresetsAsync());
        if (error is not null) ErrorText = error;
        return list.Select(v => new AppCandidate(v.Name, v.ExePath, AppMatchKind.ExactPath, "VPN client · " + v.ExePath)).ToList();
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
                    info.UnruledVia is { } via ? $"Apps below use their chosen network. Everything else goes through {via} (that VPN takes all unruled traffic)." : "Apps below use their chosen network. Everything else uses your Primary network."),
                SplitTunnelEngineState.Starting => (InfoBarSeverity.Informational, "Starting…", "LEO is starting its routing engine."),
                SplitTunnelEngineState.Error => (InfoBarSeverity.Error, "Split tunneling paused", info.Message ?? "The routing engine stopped."),
                SplitTunnelEngineState.NotInstalled => (InfoBarSeverity.Error, "Routing engine missing", info.Message ?? "Reinstall LEO."),
                SplitTunnelEngineState.StoodAside => (InfoBarSeverity.Warning, "Split tunneling paused by a VPN", info.Message ?? "A VPN only allows its own tunnel."),
                _ when !info.Enabled => (InfoBarSeverity.Informational, "Split tunneling is off",
                    "Your rules are kept, but every app uses the Primary network until you turn it back on."),
                _ => (InfoBarSeverity.Informational, "No active rules",
                    "Add an app to choose which network it uses. Until then LEO doesn't touch app traffic at all."),
            };

        VpnBanner = connected ? info.VpnBanner : null;

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
