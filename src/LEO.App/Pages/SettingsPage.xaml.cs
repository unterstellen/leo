using Leo.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Leo.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public MainViewModel ViewModel => App.Current.ViewModel;
}
