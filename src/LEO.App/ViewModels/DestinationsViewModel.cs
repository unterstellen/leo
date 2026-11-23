using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leo.App.Services;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Leo.App.ViewModels;

/// <summary>An app rule a destination rule can be combined with.</summary>
public sealed record AppOption(string? Id, string Name, string? TestPath);

/// <summary>URLs &amp; IPs page: destination rules, per-network DNS and "test a rule" (LEO.md §6.5, M4). Changes come from clicks only.</summary>
public sealed partial class DestinationsViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly DispatcherQueue _dispatcher;

    public DestinationsViewModel(ServiceConnection connection, DispatcherQueue dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _connection.SplitTunnelChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.NetworksChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.Changed += (_, _) => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    public ObservableCollection<DestinationCardViewModel> Rules { get; } = [];
    public ObservableCollection<NetworkDnsCardViewModel> Dns { get; } = [];

    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string MasterButtonText { get; set; } = "Turn off";
    [ObservableProperty] public partial string StatusTitle { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string? ErrorText { get; set; }

    [ObservableProperty] public partial bool TestBusy { get; set; }
    [ObservableProperty] public partial bool HasTestResult { get; set; }
    [ObservableProperty] public partial string TestRuleText { get; set; } = "";
    [ObservableProperty] public partial string TestNetworkText { get; set; } = "";
    [ObservableProperty] public partial string TestWhyText { get; set; } = "";

    public IReadOnlyList<NetworkOption> Networks => NetworkOption.From(_connection.Networks);

    [ObservableProperty] public partial string? VpnBanner { get; set; }

    /// <summary>"Any app" plus every app rule (combos). Only single-program rules can be used in a test.</summary>
    public IReadOnlyList<AppOption> Apps =>
        [new AppOption(null, "Any app", null),
         .. _connection.SplitTunnel.Rules.Select(r => new AppOption(r.Id, r.DisplayName, r.Kind == AppMatchKind.ExactPath ? r.Pattern : null))];

    public string NetworkName(string adapterId) =>
        _connection.Networks.Adapters.FirstOrDefault(a => a.Id == adapterId)?.DisplayName ?? "Network not connected";

    // ── Actions (from user events only) ─────────────────────────────────────

    public async Task ToggleMasterAsync()
    {
        if (!CanEdit) return;
        var enable = !_connection.SplitTunnel.Enabled;
        UiLog.Write($"Split tunneling: switch {(enable ? "on" : "off")} (URLs & IPs page)");
        Show(await _connection.TryInvokeAsync(c => c.SetSplitTunnelEnabledAsync(enable)));
    }

    /// <summary>Default network for a new rule: the best connected network that isn't the Primary.</summary>
    public NetworkOption? DefaultTarget()
    {
        var nets = Networks;
        var physical = nets.Where(n => !n.IsVpn).ToList();
        return physical.FirstOrDefault(n => n.IsUp && !n.IsPrimary) ?? physical.FirstOrDefault(n => n.IsPrimary) ?? nets.FirstOrDefault();
    }

    /// <summary>Saves a rule; returns the service's error (null = saved). The UI log never records the destination itself.</summary>
    public async Task<string?> SaveAsync(DestinationRule rule, string what)
    {
        if (!CanEdit) return "Only administrators can change rules.";
        UiLog.Write($"Destination rule {rule.Id[..Math.Min(8, rule.Id.Length)]}: {what}");
        var error = await _connection.TryInvokeAsync(c => c.SaveDestinationRuleAsync(rule));
        Show(error);
        return error;
    }

    public async Task DeleteAsync(DestinationCardViewModel card)
    {
        if (!CanEdit) return;
        UiLog.Write($"Destination rule {card.Info.Id[..Math.Min(8, card.Info.Id.Length)]}: delete");
        Show(await _connection.TryInvokeAsync(c => c.DeleteDestinationRuleAsync(card.Info.Id)));
    }

    public async Task MoveAsync(DestinationCardViewModel card, bool up)
    {
        if (!CanEdit) return;
        Show(await _connection.TryInvokeAsync(c => c.MoveDestinationRuleAsync(card.Info.Id, up)));
    }

    public async Task<string?> SetDnsAsync(string adapterId, IReadOnlyList<string> servers)
    {
        if (!CanEdit) return "Only administrators can change DNS.";
        UiLog.Write($"DNS for {NetworkName(adapterId)}: {(servers.Count == 0 ? "automatic" : string.Join(", ", servers))}");
        var error = await _connection.TryInvokeAsync(c => c.SetNetworkDnsAsync(adapterId, servers));
        Show(error);
        return error;
    }

    public async Task TestAsync(string target, string? appPath)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(target) || TestBusy) return;
        TestBusy = true;
        try
        {
            RouteTestResult? result = null;
            var error = await _connection.TryInvokeAsync(async c => result = await c.TestRouteAsync(target.Trim(), appPath));
            if (error is not null || result is null)
            {
                TestRuleText = "Couldn't test that";
                TestNetworkText = "";
                TestWhyText = error ?? "No answer from the LEO service.";
            }
            else
            {
                TestRuleText = result.RuleText;
                TestNetworkText = "Uses: " + result.NetworkName;
                TestWhyText = result.Explanation;
            }
            HasTestResult = true;
        }
        finally
        {
            TestBusy = false;
        }
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
        IsConnected = _connection.State == ConnectionState.Connected;
        CanEdit = IsConnected && _connection.Access == ClientAccess.Full;
        MasterButtonText = info.Enabled ? "Turn off" : "Turn on";
        var dests = info.Destinations ?? [];

        (StatusSeverity, StatusTitle, StatusText) = !IsConnected
            ? (InfoBarSeverity.Warning, "LEO service not connected", "Rules can't be shown or changed right now.")
            : info.State switch
            {
                SplitTunnelEngineState.Running => (InfoBarSeverity.Success, "Split tunneling is on",
                    info.UnruledVia is { } via ? $"Destinations below use their chosen network, even from apps that have their own rule. Everything else goes through {via} (that VPN takes all unruled traffic)." : "Destinations below use their chosen network, even from apps that have their own rule. Everything else uses your Primary network."),
                SplitTunnelEngineState.Starting => (InfoBarSeverity.Informational, "Starting…", "LEO is starting its routing engine."),
                SplitTunnelEngineState.Error => (InfoBarSeverity.Error, "Split tunneling paused", info.Message ?? "The routing engine stopped."),
                SplitTunnelEngineState.NotInstalled => (InfoBarSeverity.Error, "Routing engine missing", info.Message ?? "Reinstall LEO."),
                SplitTunnelEngineState.StoodAside => (InfoBarSeverity.Warning, "Split tunneling paused by a VPN", info.Message ?? "A VPN only allows its own tunnel."),
                _ when !info.Enabled => (InfoBarSeverity.Informational, "Split tunneling is off",
                    "Your rules are kept, but everything uses the Primary network until you turn it back on."),
                _ => (InfoBarSeverity.Informational, "No active rules",
                    "Add a website or address to choose which network it uses. Until then LEO doesn't touch traffic at all."),
            };

        VpnBanner = IsConnected ? info.VpnBanner : null;
        Sync(Rules, dests, d => d.Id, c => c.Info.Id, () => new DestinationCardViewModel(this), (c, d) => c.Update(d, CanEdit, dests.Count));
        Sync(Dns, info.NetworkDns ?? [], d => d.AdapterId, c => c.Info.AdapterId, () => new NetworkDnsCardViewModel(), (c, d) => c.Update(d, CanEdit));
        IsEmpty = Rules.Count == 0;
    }

    /// <summary>Updates a card list in place by id, keeping the incoming order.</summary>
    private static void Sync<TCard, TInfo>(ObservableCollection<TCard> cards, IReadOnlyList<TInfo> incoming, Func<TInfo, string> infoId,
        Func<TCard, string> cardId, Func<TCard> create, Action<TCard, TInfo> update)
    {
        for (var i = cards.Count - 1; i >= 0; i--)
            if (incoming.All(r => infoId(r) != cardId(cards[i]))) cards.RemoveAt(i);
        for (var i = 0; i < incoming.Count; i++)
        {
            var at = -1;
            for (var j = 0; j < cards.Count; j++) if (cardId(cards[j]) == infoId(incoming[i])) { at = j; break; }
            TCard card;
            if (at < 0) { card = create(); cards.Insert(Math.Min(i, cards.Count), card); }
            else
            {
                card = cards[at];
                if (at != i) cards.Move(at, i);
            }
            update(card, incoming[i]);
        }
    }
}

public sealed partial class DestinationCardViewModel(DestinationsViewModel owner) : ObservableObject
{
    public DestinationRuleInfo Info { get; private set; } = null!;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string DetailText { get; set; } = "";
    [ObservableProperty] public partial string TargetText { get; set; } = "";
    [ObservableProperty] public partial string DownText { get; set; } = "";
    [ObservableProperty] public partial string RouteText { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool CanMoveUp { get; set; }
    [ObservableProperty] public partial bool CanMoveDown { get; set; }
    [ObservableProperty] public partial string Glyph { get; set; } = "";

    public void Update(DestinationRuleInfo info, bool canEdit, int count)
    {
        Info = info;
        CanEdit = canEdit;
        Name = string.IsNullOrWhiteSpace(info.DisplayName) ? info.Description : info.DisplayName;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.DisplayName)) parts.Add(info.Description);
        if (info.AppName is not null) parts.Add($"only from {info.AppName}");
        if (info.Rank > 0) parts.Add($"checked #{info.Rank}");
        DetailText = string.Join(" · ", parts);
        TargetText = owner.NetworkName(info.TargetAdapterId);
        DownText = info.OnTargetDown == OnTargetDown.Block ? "If unavailable: block" : "If unavailable: use Primary";
        RouteText = "Now: " + info.RouteNow;
        IsEnabled = info.Enabled;
        Glyph = info.Kind switch
        {
            DestinationKind.IpRange => "",
            DestinationKind.AnyHost => "",
            _ => "",
        };
        var index = owner.Rules.IndexOf(this);
        CanMoveUp = canEdit && index > 0;
        CanMoveDown = canEdit && index >= 0 && index < count - 1;
    }

    public DestinationRule ToRule() => new()
    {
        Id = Info.Id, DisplayName = Info.DisplayName, Kind = Info.Kind, Value = Info.Value, Ports = Info.Ports,
        Protocol = Info.Protocol, AppRuleId = Info.AppRuleId, TargetAdapterId = Info.TargetAdapterId,
        OnTargetDown = Info.OnTargetDown, Enabled = Info.Enabled,
    };
}

public sealed partial class NetworkDnsCardViewModel : ObservableObject
{
    public NetworkDnsInfo Info { get; private set; } = null!;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string ServersText { get; set; } = "";
    [ObservableProperty] public partial bool CanEdit { get; set; }

    public void Update(NetworkDnsInfo info, bool canEdit)
    {
        Info = info;
        CanEdit = canEdit;
        Name = info.Name + (info.IsUp ? "" : " (not connected)");
        ServersText = info.CustomServers.Count > 0
            ? "Custom: " + string.Join(", ", info.CustomServers)
            : "Automatic: " + (info.AutomaticServers.Count > 0 ? string.Join(", ", info.AutomaticServers)
                : "none reported, so " + string.Join(", ", info.EffectiveServers));
    }
}
