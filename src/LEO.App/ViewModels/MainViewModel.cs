using CommunityToolkit.Mvvm.ComponentModel;
using Leo.App.Services;
using Leo.Core;
using Leo.Ipc;
using Microsoft.UI.Dispatching;

namespace Leo.App.ViewModels;

/// <summary>App-wide state shown by the window and the tray.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ServiceConnection _connection;

    public MainViewModel(ServiceConnection connection, DispatcherQueue dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _loading = true;
        ApplyStartAtLoginDefault();
        StartAtLogin = StartupManager.IsEnabled;
        _loading = false;
        _connection.Changed += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.SplitTunnelChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    public string AppVersion => LeoInfo.Version;
    public string PublisherLine => $"{LeoInfo.Publisher} · {LeoInfo.Company}";

    [ObservableProperty] public partial string ConnectionText { get; set; } = "Connecting to LEO service…";
    [ObservableProperty] public partial string ConnectionGlyph { get; set; } = ""; // Sync
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string EngineStateText { get; set; } = "—";
    [ObservableProperty] public partial string ServiceVersionText { get; set; } = "—";
    [ObservableProperty] public partial string RunningAsText { get; set; } = "—";
    [ObservableProperty] public partial string UptimeText { get; set; } = "—";
    [ObservableProperty] public partial string AccessText { get; set; } = "—";
    [ObservableProperty] public partial string ClientsText { get; set; } = "—";
    [ObservableProperty] public partial string TrayToolTip { get; set; } = "LEO — connecting…";

    [ObservableProperty] public partial bool StartAtLogin { get; set; }

    /// <summary>Per-user preference (ui.json), read by App before showing a toast.</summary>
    public UiSettings Ui { get; } = UiSettings.Load();

    public bool ShowNotifications
    {
        get => Ui.ShowNotifications;
        set
        {
            if (Ui.ShowNotifications == value) return;
            Ui.ShowNotifications = value;
            Ui.Save();
            OnPropertyChanged();
        }
    }

    private bool _loading;

    partial void OnStartAtLoginChanged(bool value)
    {
        if (_loading) return;
        try
        {
            StartupManager.SetEnabled(value);
            Ui.StartAtLogin = value; // the user's choice from now on
            Ui.Save();
        }
        catch { StartAtLogin = StartupManager.IsEnabled; }
    }

    /// <summary>
    /// "Start at login" is on by default: on first launch (no choice saved) the app registers itself to start in the
    /// tray at login. If it's on but points at another copy of LEO.exe (moved/rebuilt elsewhere), it's re-pointed here.
    /// A user who turned it off keeps it off.
    /// </summary>
    private void ApplyStartAtLoginDefault()
    {
        try
        {
            if (Ui.StartAtLogin == false || StartupManager.IsEnabled) return;
            StartupManager.SetEnabled(true);
            if (Ui.StartAtLogin is null)
            {
                Ui.StartAtLogin = true;
                Ui.Save();
                UiLog.Write("Start at login: on (default)");
            }
        }
        catch (Exception ex)
        {
            UiLog.Write("Couldn't turn on Start at login", ex);
        }
    }

    private void Refresh()
    {
        var status = _connection.Status;
        IsConnected = _connection.State == ConnectionState.Connected && status is not null;

        (ConnectionText, ConnectionGlyph) = _connection.State switch
        {
            ConnectionState.Connected => ("Connected to LEO service", ""),          // CheckMark
            ConnectionState.Incompatible => ("LEO service version mismatch — reinstall LEO", ""), // Warning
            ConnectionState.ServiceUnavailable => ("LEO service is not running", ""),           // Error
            _ => ("Connecting to LEO service…", ""),
        };

        if (status is not null)
        {
            EngineStateText = status.State.ToString();
            ServiceVersionText = status.ServiceVersion;
            RunningAsText = status.IsElevated ? $"{status.RunningAs} (elevated)" : status.RunningAs;
            UptimeText = FormatUptime(DateTimeOffset.Now - status.StartedAt);
            ClientsText = status.ConnectedClients.ToString();
            AccessText = _connection.Access == ClientAccess.Full ? "Full control" : "Read-only";
        }
        else
        {
            EngineStateText = ServiceVersionText = RunningAsText = UptimeText = ClientsText = AccessText = "—";
        }

        // M6: say when unruled traffic follows a VPN, or a VPN paused split tunneling.
        var split = _connection.SplitTunnel;
        var vpnNote = split.State == SplitTunnelEngineState.StoodAside ? " · split tunneling paused by a VPN"
            : split.UnruledVia is { } via ? $" · through {via}"
            : split.UnruledBondNow is { } bond ? $" · everything else through bond '{bond}'" : "";
        TrayToolTip = IsConnected ? $"LEO — engine {EngineStateText.ToLowerInvariant()}{vpnNote}" : $"LEO — {ConnectionText}";
    }

    private static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m"
        : $"{t.Minutes}m {t.Seconds}s";
}
