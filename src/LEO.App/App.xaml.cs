using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Leo.App.Services;
using Leo.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Leo.App;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MainWindow? _window;
    private DispatcherQueue? _dispatcher;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
    }

    public static new App Current => (App)Application.Current;

    public ServiceConnection Connection { get; } = new();
    public MainViewModel ViewModel { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ViewModel = new MainViewModel(Connection, _dispatcher);
        Connection.Start();

        CreateTrayIcon();

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
        _trayIcon?.Dispose();
        _window?.Close();
        await Connection.DisposeAsync();
        Exit();
    }

    private void CreateTrayIcon()
    {
        var open = new MenuFlyoutItem { Text = "Open LEO", Icon = new FontIcon { Glyph = "" } };
        open.Command = new RelayCommand(ShowMainWindow);

        var quit = new MenuFlyoutItem { Text = "Quit LEO UI", Icon = new FontIcon { Glyph = "" } };
        quit.Command = new RelayCommand(ExitApp);

        var status = new MenuFlyoutItem { IsEnabled = false };
        status.SetBinding(MenuFlyoutItem.TextProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = ViewModel,
            Path = new PropertyPath(nameof(MainViewModel.ConnectionText)),
        });

        var menu = new MenuFlyout();
        menu.Items.Add(status);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(open);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quit);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = ViewModel.TrayToolTip,
            ContextMenuMode = ContextMenuMode.SecondWindow,
            ContextFlyout = menu,
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
        _trayIcon.ForceCreate();
    }
}
