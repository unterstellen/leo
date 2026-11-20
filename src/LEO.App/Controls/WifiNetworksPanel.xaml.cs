using Leo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leo.App.Controls;

/// <summary>
/// All row actions come from Click events and go through the view model's action gate; nothing is triggered
/// by bindings or commands that a recycled row element could replay.
/// </summary>
public sealed partial class WifiNetworksPanel : UserControl
{
    public WifiNetworksPanel(WifiNetworksViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public WifiNetworksViewModel ViewModel { get; }

    private void KeepConnected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: WifiRowViewModel row } box) _ = row.OnPinClickedAsync(box.IsChecked == true);
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WifiRowViewModel row }) _ = row.OnConnectClickedAsync();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WifiRowViewModel row }) _ = row.OnDisconnectClickedAsync();
    }
}
