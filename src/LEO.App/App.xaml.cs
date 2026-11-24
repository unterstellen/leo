using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Leo.App.Services;
using Leo.App.ViewModels;
using Leo.Ipc;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leo.App;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MenuFlyout? _trayMenu;
    private MainWindow? _window;
    private DispatcherQueue? _dispatcher;
    private bool _exiting;

    public App()
    {
        InitializeComponent();

        // A tray app must not vanish on one bad click: log every unhandled UI exception and keep running.
        UnhandledException += (_, e) =>
        {
            UiLog.Write($"Unhandled UI exception: {e.Message}", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            UiLog.Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    public static new App Current => (App)Application.Current;

    public ServiceConnection Connection { get; } = new();
    public MainViewModel ViewModel { get; private set; } = null!;
    public NetworksViewModel Networks { get; private set; } = null!;
    public AppsViewModel Apps { get; private set; } = null!;
    public DestinationsViewModel Destinations { get; private set; } = null!;
    public BondingViewModel Bonding { get; private set; } = null!;
    public ProfilesViewModel Profiles { get; private set; } = null!;
    public StatsViewModel Stats { get; private set; } = null!;

    /// <summary>Main window handle, for Windows file/folder pickers.</summary>
    public IntPtr MainWindowHandle => _window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ViewModel = new MainViewModel(Connection, _dispatcher);
        Networks = new NetworksViewModel(Connection, _dispatcher);
        Apps = new AppsViewModel(Connection, _dispatcher);
        Destinations = new DestinationsViewModel(Connection, _dispatcher);
        Bonding = new BondingViewModel(Connection, _dispatcher);
        Profiles = new ProfilesViewModel(Connection, _dispatcher);
        Stats = new StatsViewModel(Connection, _dispatcher);

        Toasts.Register(() => _dispatcher.TryEnqueue(ShowMainWindow));
        Connection.Notified += (_, n) =>
        {
            if (ViewModel.ShowNotifications) Toasts.Show(n);
        };
        Connection.Start();

        CreateTrayIcon();
        Networks.Refreshed += (_, _) => RebuildTrayMenu();
        Profiles.Refreshed += (_, _) => RebuildTrayMenu();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ConnectionText)) RebuildTrayMenu();
        };

        _window = new MainWindow(ViewModel);
        _window.CloseRequested += (_, e) =>
        {
            // Closing the window only hides it; LEO keeps living in the tray.
            if (!_exiting)
            {
                e.Cancel = true;
                _window.Hide();
            }
        };

        if (!Program.StartInTray)
        {
            ShowMainWindow();
        }
    }

    /// <summary>Another launch was redirected to this instance (LEO.App.Program).</summary>
    public void OnRedirectedActivation() => _dispatcher?.TryEnqueue(ShowMainWindow);

    public void ShowMainWindow()
    {
        _window?.ShowAndActivate();
    }

    public async void ExitApp()
    {
        _exiting = true;
        Toasts.Unregister();
        _trayIcon?.Dispose();
        _window?.Close();
        await Connection.DisposeAsync();
        Exit();
    }

    private void CreateTrayIcon()
    {
        _trayMenu = new MenuFlyout();
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = ViewModel.TrayToolTip,
            // Native Win32 popup menu, rebuilt from ContextFlyout on every right-click, so the live
            // network list shows up. (SecondWindow mode snapshots the items once → empty/stale menu.)
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = _trayMenu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(ShowMainWindow),
            Icon = AppIcons.LoadTrayIcon(),
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TrayToolTip) && _trayIcon is not null)
            {
                _trayIcon.ToolTipText = ViewModel.TrayToolTip;
            }
        };
        RebuildTrayMenu();
        _trayIcon.ForceCreate();
    }

    /// <summary>
    /// Tray quick menu (LEO.md §6.9): status, every network with its nickname and state
    /// (click to make it Primary), Open LEO, Quit.
    /// </summary>
    private void RebuildTrayMenu()
    {
        if (_trayMenu is null) return;
        var items = _trayMenu.Items;
        items.Clear();

        items.Add(new MenuFlyoutItem { Text = ViewModel.ConnectionText, IsEnabled = false });

        var candidates = Networks.Snapshot.Adapters
            .Where(a => a.IsPrimaryCandidate)
            .OrderBy(a => a.PriorityIndex ?? int.MaxValue)
            .ToList();
        if (candidates.Count > 0)
        {
            items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = "Primary network", IsEnabled = false });
            foreach (var adapter in candidates)
            {
                var connected = adapter.Status == AdapterStatus.Up;
                var network = adapter.Network is { } n
                    ? $"{n.Nickname ?? n.Name} ({AdapterCardViewModel.HealthText(adapter)})"
                    : "Not connected";
                var item = new ToggleMenuFlyoutItem
                {
                    Text = $"{adapter.DisplayName} · {network}",
                    IsChecked = adapter.IsPrimary,
                    IsEnabled = Networks.CanEdit && (connected || adapter.PriorityIndex != 0),
                    Icon = new FontIcon { Glyph = AdapterCardViewModel.GlyphFor(adapter.Kind) },
                };
                var id = adapter.Id;
                var alreadyPrimary = adapter.IsPrimary;
                item.Command = new AsyncRelayCommand(async () =>
                {
                    if (alreadyPrimary) { RebuildTrayMenu(); return; } // keep the check mark
                    await Networks.SetPrimaryAsync(id);
                });
                items.Add(item);
            }
        }

        // Profile switcher (LEO.md §6.9): pick one by hand, or let the triggers decide.
        var profiles = Profiles.Info;
        if (profiles.Profiles.Count > 1)
        {
            items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = "Profile", IsEnabled = false });
            var auto = new ToggleMenuFlyoutItem
            {
                Text = "Automatic",
                IsChecked = profiles.AutoSwitch && !profiles.Manual && !profiles.Paused,
                IsEnabled = Profiles.CanEdit,
                Icon = new FontIcon { Glyph = "" },
            };
            var autoOn = auto.IsChecked;
            auto.Command = new AsyncRelayCommand(async () =>
            {
                if (autoOn) { RebuildTrayMenu(); return; }
                await Profiles.ActivateAsync(null);
            });
            items.Add(auto);
            foreach (var p in profiles.Profiles)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = p.Name,
                    IsChecked = p.IsActive,
                    IsEnabled = Profiles.CanEdit,
                    Icon = new FontIcon { Glyph = p.IsDefault ? "" : "" },
                };
                var id = p.Id;
                var active = p.IsActive;
                item.Command = new AsyncRelayCommand(async () =>
                {
                    if (active) { RebuildTrayMenu(); return; }
                    await Profiles.ActivateAsync(id);
                });
                items.Add(item);
            }
        }

        items.Add(new MenuFlyoutSeparator());
        items.Add(new MenuFlyoutItem
        {
            Text = "Open LEO",
            Icon = new FontIcon { Glyph = "" },
            Command = new RelayCommand(ShowMainWindow),
        });
        items.Add(new MenuFlyoutItem
        {
            Text = "Quit LEO UI",
            Icon = new FontIcon { Glyph = "" },
            Command = new RelayCommand(ExitApp),
        });
    }
}
