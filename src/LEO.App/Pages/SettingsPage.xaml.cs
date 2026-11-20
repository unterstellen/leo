using Leo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Leo.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        Advanced = new AdvancedSettingsViewModel(App.Current.Connection);
        InitializeComponent();
    }

    public MainViewModel ViewModel => App.Current.ViewModel;

    public NetworksViewModel Networks => App.Current.Networks;

    public AdvancedSettingsViewModel Advanced { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try { await Advanced.LoadAsync(); }
        catch (Exception ex) { Services.UiLog.Write("Loading advanced settings failed", ex); }
    }

    private async void SaveAdvanced_Click(object sender, RoutedEventArgs e)
    {
        try { await Advanced.SaveAsync(); }
        catch (Exception ex) { Advanced.ErrorText = ex.Message; }
    }

    private void ResetAdvanced_Click(object sender, RoutedEventArgs e) => Advanced.ResetToDefaults();
}
