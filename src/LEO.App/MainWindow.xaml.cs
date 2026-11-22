using Leo.App.Pages;
using Leo.App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Leo.App;

public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Title = "LEO";
        AppWindow.SetIcon(AppIcons.IcoPath);
        AppWindow.Resize(new SizeInt32(1100, 740));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 520;
        }
        AppWindow.Closing += (_, e) =>
        {
            var args = new CloseRequestedEventArgs();
            CloseRequested?.Invoke(this, args);
            e.Cancel = args.Cancel;
        };

        ContentFrame.Navigate(typeof(DashboardPage));
        _current = this;
    }

    private static MainWindow? _current;

    /// <summary>Selects the sidebar item with the given Tag (e.g. "Networks").</summary>
    public static void NavigateTo(string tag)
    {
        if (_current?.Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag) is { } item)
        {
            _current.Nav.SelectedItem = item;
        }
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Raised when the user closes the window; set Cancel to keep it alive (hide to tray).</summary>
    public event EventHandler<CloseRequestedEventArgs>? CloseRequested;

    public void Hide() => AppWindow.Hide();

    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p)
        {
            p.Restore();
        }
        Activate();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        switch ((args.SelectedItem as NavigationViewItem)?.Tag as string)
        {
            case "Dashboard":
                ContentFrame.Navigate(typeof(DashboardPage));
                break;
            case "Networks":
                ContentFrame.Navigate(typeof(NetworksPage));
                break;
            case "Apps":
                ContentFrame.Navigate(typeof(AppsPage));
                break;
            case "Destinations":
                ContentFrame.Navigate(typeof(ComingSoonPage), new ComingSoonInfo("URLs & IPs", "M4",
                    "Send specific domains, IP ranges and ports through a chosen network."));
                break;
            case "Profiles":
                ContentFrame.Navigate(typeof(ComingSoonPage), new ComingSoonInfo("Profiles", "M5",
                    "Saved setups like Home, Office or Gaming that switch automatically."));
                break;
        }
    }
}

public sealed class CloseRequestedEventArgs : EventArgs
{
    public bool Cancel { get; set; }
}
