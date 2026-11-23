using System.ComponentModel;
using Leo.App.Services;
using Leo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Leo.App.Pages;

/// <summary>Stats and live connections (M5). Polls only while the page is shown.</summary>
public sealed partial class StatsPage : Page
{
    private static bool _dialogOpen;

    public StatsPage()
    {
        InitializeComponent();
    }

    public StatsViewModel ViewModel => App.Current.Stats;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ErrorBar.IsOpen = ViewModel.ErrorText is not null;
        FilterBox.Text = ViewModel.ConnectionsFilter;
        ViewModel.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatsViewModel.ErrorText)) ErrorBar.IsOpen = ViewModel.ErrorText is not null;
    }

    private void ErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.ErrorText = null;

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ViewModel.ApplyFilter(FilterBox.Text); // view filter only

    private void Pause_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePause();

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Clear all statistics?",
                Content = "This deletes every saved daily total (per network and per app) from this PC. It can't be undone.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ClearAsync();
        }
        catch (Exception ex)
        {
            UiLog.Write("Clear statistics failed", ex);
            ViewModel.ErrorText = ex.Message;
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
