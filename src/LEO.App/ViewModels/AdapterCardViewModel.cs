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

        StatusText = info.Status switch
        {
            AdapterStatus.Up => info.Network?.Connectivity switch
            {
                Connectivity.Internet => "Internet",
                Connectivity.ConstrainedInternet => "Sign-in or limited",
                Connectivity.LocalAccess => "No internet",
                _ => "Connected",
            },
            AdapterStatus.Connecting => "Connecting",
            _ => "Disconnected",
        };
        if (info.Network?.IsMetered == true) StatusText += " · Metered";

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
    }

    [RelayCommand]
    private Task MakePrimaryAsync() => _owner.SetPrimaryAsync(this);

    [RelayCommand]
    private Task MoveUpAsync() => _owner.MoveAsync(this, -1);

    [RelayCommand]
    private Task MoveDownAsync() => _owner.MoveAsync(this, +1);

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
