using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Leo.Core.Networking;
using Leo.Ipc;

namespace Leo.App.ViewModels;

/// <summary>One adapter card on the Networks page (LEO.md §6.1).</summary>
public sealed partial class AdapterCardViewModel : ObservableObject
{
    private readonly NetworksViewModel _owner;

    public AdapterCardViewModel(NetworksViewModel owner, AdapterInfo info)
    {
        _owner = owner;
        Info = info;
        Update(info, 0, false);
    }

    public AdapterInfo Info { get; private set; }

    public string Id => Info.Id;

    [ObservableProperty] public partial string Glyph { get; set; } = "";
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string NetworkText { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string DetailsText { get; set; } = "";
    [ObservableProperty] public partial string PriorityText { get; set; } = "";
    [ObservableProperty] public partial bool IsPrimary { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial bool ShowPriorityControls { get; set; }
    [ObservableProperty] public partial bool CanMakePrimary { get; set; }
    [ObservableProperty] public partial bool CanMoveUp { get; set; }
    [ObservableProperty] public partial bool CanMoveDown { get; set; }
    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool IsDemoted { get; set; }
    [ObservableProperty] public partial string DemotedText { get; set; } = "";
    [ObservableProperty] public partial bool IsWifi { get; set; }
    [ObservableProperty] public partial string PinsText { get; set; } = "";
    [ObservableProperty] public partial bool ShowDualLinkButton { get; set; }
    [ObservableProperty] public partial string DualLinkButtonText { get; set; } = "";
    [ObservableProperty] public partial string DualLinkText { get; set; } = "";
    [ObservableProperty] public partial bool ShowWifiButton { get; set; }

    // VPN cards (M6, LEO.md §20): no priority/Primary controls; what the VPN carries and how LEO treats it.
    [ObservableProperty] public partial bool IsVpn { get; set; }
    [ObservableProperty] public partial string VpnCarriesText { get; set; } = "";
    [ObservableProperty] public partial string VpnOwnTrafficText { get; set; } = "";
    [ObservableProperty] public partial string VpnFirewallText { get; set; } = "";
    [ObservableProperty] public partial bool ShowSkipUnruled { get; set; }
    [ObservableProperty] public partial bool SkipUnruled { get; set; }

    public void Update(AdapterInfo info, int candidateCount, bool canEdit)
    {
        Info = info;
        Glyph = GlyphFor(info.Kind);
        Title = info.DisplayName;
        Subtitle = info.Nickname is null ? KindLabel(info.Kind) : $"{info.Name} · {KindLabel(info.Kind)}";
        IsConnected = info.Status == AdapterStatus.Up;
        IsPrimary = info.IsPrimary;

        NetworkText = info.Network is { } n
            ? (n.Nickname is null ? n.Name : $"{n.Nickname} ({n.Name})")
            : info.Status == AdapterStatus.Connecting ? "Connecting…" : "Not connected";

        StatusText = HealthText(info);
        if (info.Network?.IsMetered == true) StatusText += " · Metered";

        IsDemoted = info.IsDemoted;
        DemotedText = info.FailbackInSeconds is { } back
            ? $"Skipped (failover) · back in {back}s if it stays healthy"
            : "Skipped (failover) · no internet";
        IsWifi = info.Kind == Leo.Core.Networking.AdapterKind.WiFi;
        PinsText = info.PinnedSsids.Count > 0 ? "Stays on: " + string.Join(", ", info.PinnedSsids) : "";

        // Dual link (DBS secondary station). A secondary can't pick its own network, so no Wi-Fi dialog for it.
        var isSecondary = info.FollowsAdapterId is not null;
        ShowWifiButton = IsWifi && !isSecondary;
        ShowDualLinkButton = info.DualLinkSupported;
        DualLinkButtonText = info.DualLinkEnabled ? "Turn off dual link" : "Turn on dual link";
        var secondaryName = _owner.NameOf(info.FollowsAdapterId) ?? "";
        var secondaryUp = _owner.SecondariesOf(info.Id).Any(s => s.Status == AdapterStatus.Up);
        DualLinkText = info.DualLinkSupported
            ? (!info.DualLinkEnabled
                ? "Dual link off: this card's second station is idle."
                : secondaryUp
                    ? "Dual link on: the second station also connects to this network, on the other band."
                    : "Dual link on, but the second station isn't connected: Windows keeps trying and the Wi-Fi driver declines on this network.")
            : isSecondary
                ? $"Second station of {secondaryName} · follows its network · dual link {(info.DualLinkEnabled ? "on" : "off")}"
                : "";

        var details = new List<string>();
        if (info.IPv4.FirstOrDefault(ip => !ip.StartsWith("169.254.", StringComparison.Ordinal)) is { } ip) details.Add($"IP {ip}");
        if (info.Gateways.FirstOrDefault() is { } gw) details.Add($"Gateway {gw}");
        if (info.LinkSpeedBps > 0) details.Add(FormatSpeed(info.LinkSpeedBps));
        if (info.Metric is { } m) details.Add($"Metric {m}{(info.AutomaticMetric ? " (auto)" : "")}");
        DetailsText = string.Join("  ·  ", details);

        ShowPriorityControls = info.IsPrimaryCandidate && info.PriorityIndex is not null;
        PriorityText = info.PriorityIndex is { } p ? $"#{p + 1}" : "";
        CanEdit = canEdit;
        CanMakePrimary = canEdit && info.IsPrimaryCandidate && info.PriorityIndex != 0;
        CanMoveUp = canEdit && info.PriorityIndex is > 0;
        CanMoveDown = canEdit && info.PriorityIndex is { } q && q < candidateCount - 1;

        IsVpn = info.Vpn is not null;
        if (info.Vpn is { } v)
        {
            var primary = _owner.PrimaryName();
            VpnCarriesText = !v.Connected ? ""
                : v.TakesDefault4 || v.TakesDefault6
                    ? (v.SkipUnruled ? "Carries: all traffic (Windows routes) · unruled apps skip it while LEO's rules are active" : "Carries: all traffic")
                        + (v.TakesDefault4 && !v.TakesDefault6 ? " · IPv6 follows Windows' routes" : "")
                : v.PublicRanges > 0 ? $"Carries: {v.PublicRanges} address range{(v.PublicRanges == 1 ? "" : "s")} it routes itself · private networks"
                : "Carries: private network only";
            VpnOwnTrafficText = !v.Connected ? ""
                : v.OwnTrafficRuled && v.OwnTrafficVia is { } via ? $"Its own traffic: carried by {via} (app rule)"
                : $"Its own traffic: chooses its own network (follows Primary{(primary is null ? "" : ": " + primary)})";
            VpnFirewallText = v.Firewall switch
            {
                VpnFirewall.BlocksBypass => $"{v.FirewallOwner ?? info.DisplayName}'s firewall blocks traffic outside the VPN. Rules that bypass it can't connect while it's on; the VPN's own settings decide this.",
                VpnFirewall.BlocksLeo => $"{v.FirewallOwner ?? info.DisplayName} only allows its own tunnel, so LEO paused split tunneling until it disconnects.",
                _ => "",
            };
            ShowSkipUnruled = v.TakesDefault4;
            SkipUnruled = v.SkipUnruled;
            DetailsText = string.Join("  ·  ", new[] { DetailsText, v.DnsServers > 0 ? $"{v.DnsServers} DNS server{(v.DnsServers == 1 ? "" : "s")}" : "", v.NrptRules > 0 ? $"{v.NrptRules} DNS name rule{(v.NrptRules == 1 ? "" : "s")}" : "" }.Where(s => s.Length > 0));
        }
        else
        {
            VpnCarriesText = VpnOwnTrafficText = VpnFirewallText = "";
            ShowSkipUnruled = SkipUnruled = false;
        }
    }

    [RelayCommand]
    private Task MakePrimaryAsync() => _owner.SetPrimaryAsync(this);

    [RelayCommand]
    private Task MoveUpAsync() => _owner.MoveAsync(this, -1);

    [RelayCommand]
    private Task MoveDownAsync() => _owner.MoveAsync(this, +1);

    /// <summary>LEO's own probe result in words. VPNs (M6): Connected · internet / private network only / Not connected.</summary>
    public static string HealthText(AdapterInfo info)
    {
        if (info.Vpn is { } vpn)
        {
            if (!vpn.Connected) return info.Status == AdapterStatus.Up ? "Not connected (signed out or idle)" : "Not connected";
            return info.Health switch
            {
                Leo.Core.Networking.HealthState.Up or Leo.Core.Networking.HealthState.Degraded =>
                    "Connected · internet" + (info.LatencyMs is { } ms ? $" · {ms} ms" : ""),
                Leo.Core.Networking.HealthState.Unknown => "Connected · checking…",
                _ => "Connected · private network only",
            };
        }
        if (info.Status == AdapterStatus.Connecting) return "Connecting";
        if (info.Status != AdapterStatus.Up) return "Disconnected";
        if (info.Health == Leo.Core.Networking.HealthState.BlockedByVpn) return "Blocked by a VPN's firewall";
        if (!info.IsPrimaryCandidate)
        {
            return info.Network?.Connectivity switch
            {
                Connectivity.Internet => "Internet",
                Connectivity.LocalAccess => "No internet",
                _ => "Connected",
            };
        }
        return info.Health switch
        {
            Leo.Core.Networking.HealthState.Up => info.LatencyMs is { } ms ? $"Internet · {ms} ms" : "Internet",
            Leo.Core.Networking.HealthState.Degraded => info.LatencyMs is { } ms ? $"Slow · {ms} ms" : "Slow",
            Leo.Core.Networking.HealthState.Captive => "Sign-in needed",
            Leo.Core.Networking.HealthState.NoInternet => "No internet",
            Leo.Core.Networking.HealthState.Disconnected => "Disconnected",
            _ => "Checking…",
        };
    }

    public static string GlyphFor(AdapterKind kind) => kind switch
    {
        AdapterKind.WiFi => "",
        AdapterKind.Ethernet => "",
        AdapterKind.UsbTether => "",
        AdapterKind.Cellular => "",
        AdapterKind.Bluetooth => "",
        AdapterKind.Vpn => "",
        _ => "",
    };

    public static string KindLabel(AdapterKind kind) => kind switch
    {
        AdapterKind.WiFi => "Wi-Fi",
        AdapterKind.Ethernet => "Ethernet",
        AdapterKind.UsbTether => "USB tethering",
        AdapterKind.Cellular => "Cellular",
        AdapterKind.Bluetooth => "Bluetooth",
        AdapterKind.Vpn => "VPN",
        _ => "Network adapter",
    };

    private static string FormatSpeed(long bps) => bps >= 1_000_000_000
        ? $"{bps / 1_000_000_000.0:0.#} Gbps"
        : $"{bps / 1_000_000.0:0.#} Mbps";
}
