using Leo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leo.App.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    public MainViewModel ViewModel => App.Current.ViewModel;

    public NetworksViewModel Networks => App.Current.Networks;

    private void ManageNetworks_Click(object sender, RoutedEventArgs e) => MainWindow.NavigateTo("Networks");
}
