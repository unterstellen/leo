using System.ComponentModel;
using Leo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Leo.App.Pages;

public sealed partial class NetworksPage : Page
{
    public NetworksPage()
    {
        InitializeComponent();
    }

    public NetworksViewModel ViewModel => App.Current.Networks;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ErrorBar.IsOpen = ViewModel.ErrorText is not null;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) =>
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NetworksViewModel.ErrorText)) ErrorBar.IsOpen = ViewModel.ErrorText is not null;
    }

    private void ErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissError();

    // WinUI allows one ContentDialog at a time; a second ShowAsync throws (and async void would crash the app).
    private static bool _dialogOpen;

    private async void DualLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is AdapterCardViewModel card) await ViewModel.ToggleDualLinkAsync(card);
        }
        catch (Exception ex)
        {
            Fail("Dual link", ex);
        }
    }

    private async void Wifi_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AdapterCardViewModel card || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var vm = new WifiNetworksViewModel(ViewModel.Connection, card.Info, ViewModel.CanEdit);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Wi-Fi networks · {card.Info.DisplayName}",
                Content = new Controls.WifiNetworksPanel(vm),
                CloseButtonText = "Done",
                DefaultButton = ContentDialogButton.Close,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 640.0;
            _ = vm.OpenAsync();
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Fail("Wi-Fi networks", ex);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void Fail(string what, Exception ex)
    {
        Services.UiLog.Write($"{what} failed", ex);
        ViewModel.ErrorText = $"{what}: {ex.Message}";
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AdapterCardViewModel card || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            await RenameAsync(card);
        }
        catch (Exception ex)
        {
            Fail("Rename", ex);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task RenameAsync(AdapterCardViewModel card)
    {

        var adapterBox = new TextBox
        {
            Header = $"Adapter nickname ({card.Info.Name})",
            PlaceholderText = card.Info.Name,
            Text = card.Info.Nickname ?? "",
            MaxLength = 48,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(adapterBox);

        TextBox? networkBox = null;
        if (card.Info.Network is { } network)
        {
            networkBox = new TextBox
            {
                Header = $"Network nickname ({network.Name})",
                PlaceholderText = network.Name,
                Text = network.Nickname ?? "",
                MaxLength = 48,
            };
            panel.Children.Add(networkBox);
            panel.Children.Add(new TextBlock
            {
                Text = "The network nickname follows that network to any adapter. Leave a box empty to use the Windows name.",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RenameAsync(card, adapterBox.Text, networkBox?.Text);
        }
    }
}
