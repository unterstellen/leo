using Leo.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace Leo.App.Controls;

/// <summary>Searchable list of apps for the "Add app" dialogs.</summary>
public sealed partial class AppPickerPanel : UserControl
{
    private List<AppCandidate> _all = [];

    public AppPickerPanel()
    {
        InitializeComponent();
    }

    public AppCandidate? Selected => List.SelectedItem as AppCandidate;

    public void SetItems(List<AppCandidate> items)
    {
        _all = items;
        Apply();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Apply();

    private void Apply()
    {
        var q = SearchBox.Text.Trim();
        var items = q.Length == 0 ? _all : _all.Where(a =>
            a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        List.ItemsSource = items;
        StatusText.Text = _all.Count == 0 ? "No apps found." : $"{items.Count} of {_all.Count} apps";
    }
}
