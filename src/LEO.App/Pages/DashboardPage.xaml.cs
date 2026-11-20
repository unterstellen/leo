using Leo.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Leo.App.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    public MainViewModel ViewModel => App.Current.ViewModel;
}
