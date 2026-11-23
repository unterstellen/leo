using System.ComponentModel;
using Leo.App.Services;
using Leo.App.ViewModels;
using Leo.Core.Profiles;
using Leo.Ipc;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Leo.App.Pages;

/// <summary>Profiles (M5): list, add/edit/delete, triggers, switch, "active because…". Every change comes from a Click.</summary>
public sealed partial class ProfilesPage : Page
{
    // WinUI allows one ContentDialog at a time; a second ShowAsync throws.
    private static bool _dialogOpen;

    private static readonly (ProfileTriggerKind Kind, string Text)[] Kinds =
    [
        (ProfileTriggerKind.WifiConnected, "Wi-Fi network is connected"),
        (ProfileTriggerKind.WifiVisible, "Wi-Fi network is in range"),
        (ProfileTriggerKind.AdapterConnected, "Adapter is plugged in and connected"),
    ];

    public ProfilesPage()
    {
        InitializeComponent();
    }

    public ProfilesViewModel ViewModel => App.Current.Profiles;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ErrorBar.IsOpen = ViewModel.ErrorText is not null;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfilesViewModel.ErrorText)) ErrorBar.IsOpen = ViewModel.ErrorText is not null;
    }

    private void ErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissError();

    private async void Run(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            UiLog.Write($"{what} failed", ex);
            ViewModel.ErrorText = $"{what}: {ex.Message}";
        }
    }

    private void BackToAuto_Click(object sender, RoutedEventArgs e) => Run("Back to automatic", () => ViewModel.ActivateAsync(null));

    private void AutoSwitch_Click(object sender, RoutedEventArgs e) => Run("Automatic switching", ViewModel.ToggleAutoSwitchAsync);

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProfileCardViewModel card) Run("Switch profile", () => ViewModel.ActivateAsync(card.Info.Id));
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProfileCardViewModel card) Run("Move profile", () => ViewModel.MoveAsync(card, up: true));
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProfileCardViewModel card) Run("Move profile", () => ViewModel.MoveAsync(card, up: false));
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Run("Add profile", () => EditAsync(null));

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProfileCardViewModel card) Run("Edit profile", () => EditAsync(card.Info));
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Run("Delete profile", async () =>
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileCardViewModel card || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Delete {card.Name}?",
                Content = card.IsActive
                    ? "It's the active profile, so LEO switches to Default first. Its rules, priority, DNS and Wi-Fi pins are deleted with it; Wi-Fi networks saved in Windows stay."
                    : "Its rules, priority, DNS and Wi-Fi pins are deleted with it. Wi-Fi networks saved in Windows stay.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.DeleteAsync(card);
        }
        finally
        {
            _dialogOpen = false;
        }
    });

    // ── Add / edit dialog ────────────────────────────────────────────────────

    private async Task EditAsync(ProfileInfo? profile)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var isNew = profile is null;
            var ssids = await ViewModel.SuggestSsidsAsync();
            var adapters = ViewModel.Adapters;
            var triggers = profile?.Triggers.Select(t => new ProfileTrigger { Kind = t.Kind, Value = t.Value, Mac = t.Mac, Label = t.Label }).ToList() ?? [];

            var nameBox = new TextBox { Header = "Name", Text = profile?.Name ?? "", PlaceholderText = "Home, Office, Phone…", MaxLength = 32 };

            var startBox = new ComboBox { Header = "Start with", HorizontalAlignment = HorizontalAlignment.Stretch };
            startBox.Items.Add(new ComboBoxItem { Content = $"A copy of {ViewModel.Info.ActiveProfileName} (rules, priority, DNS, pins)", Tag = false });
            startBox.Items.Add(new ComboBoxItem { Content = "No rules, DNS or pins (keeps the priority list)", Tag = true });
            startBox.SelectedIndex = 0;

            var triggerList = new StackPanel { Spacing = 6 };
            var kindBox = new ComboBox { Header = "Add a trigger", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var k in Kinds) kindBox.Items.Add(new ComboBoxItem { Content = k.Text, Tag = k.Kind });
            kindBox.SelectedIndex = 0;
            var ssidBox = new ComboBox
            {
                IsEditable = true, PlaceholderText = "Wi-Fi name", HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = ssids,
            }.WithName("Wi-Fi name");
            var adapterBox = new ComboBox { PlaceholderText = "Adapter", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
            foreach (var a in adapters) adapterBox.Items.Add(new ComboBoxItem { Content = a.Name, Tag = a });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(adapterBox, "Adapter");
            var addButton = new Button { Content = "Add trigger" };
            var error = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            };

            void ShowError(string? text)
            {
                error.Text = text ?? "";
                error.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
            }

            void RenderTriggers()
            {
                triggerList.Children.Clear();
                if (triggers.Count == 0)
                {
                    triggerList.Children.Add(new TextBlock
                    {
                        Text = "No triggers: this profile only turns on when you pick it.", TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    });
                }
                foreach (var t in triggers.ToList())
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = ProfileSelector.Describe(t), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                    var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Remove trigger: " + ProfileSelector.Describe(t));
                    remove.Click += (_, _) => { triggers.Remove(t); RenderTriggers(); }; // form state only
                    Grid.SetColumn(remove, 1);
                    row.Children.Add(remove);
                    triggerList.Children.Add(row);
                }
            }

            void KindChanged()
            {
                var adapter = (ProfileTriggerKind)((ComboBoxItem)kindBox.SelectedItem).Tag == ProfileTriggerKind.AdapterConnected;
                ssidBox.Visibility = adapter ? Visibility.Collapsed : Visibility.Visible;
                adapterBox.Visibility = adapter ? Visibility.Visible : Visibility.Collapsed;
            }
            kindBox.SelectionChanged += (_, _) => KindChanged(); // form state only
            addButton.Click += (_, _) =>
            {
                var kind = (ProfileTriggerKind)((ComboBoxItem)kindBox.SelectedItem).Tag;
                ProfileTrigger t;
                if (kind == ProfileTriggerKind.AdapterConnected)
                {
                    if ((adapterBox.SelectedItem as ComboBoxItem)?.Tag is not TriggerAdapterOption a) { ShowError("Choose an adapter."); return; }
                    t = new ProfileTrigger { Kind = kind, Value = a.Id, Mac = a.Mac, Label = a.Name.Replace(" (not connected)", "", StringComparison.Ordinal) };
                }
                else
                {
                    var ssid = (ssidBox.Text ?? ssidBox.SelectedItem as string ?? "").Trim();
                    if (ssid.Length == 0) { ShowError("Enter or pick a Wi-Fi name."); return; }
                    t = new ProfileTrigger { Kind = kind, Value = ssid };
                }
                if (triggers.Any(x => x.Kind == t.Kind && x.Value.Equals(t.Value, StringComparison.OrdinalIgnoreCase))) { ShowError("That trigger is already listed."); return; }
                if (triggers.Count >= 8) { ShowError("Use up to 8 triggers per profile."); return; }
                triggers.Add(t);
                ShowError(null);
                RenderTriggers();
            };
            KindChanged();
            RenderTriggers();

            var panel = new StackPanel { Spacing = 12, Width = 460 };
            panel.Children.Add(nameBox);
            if (isNew) panel.Children.Add(startBox);
            if (profile?.IsDefault != true)
            {
                panel.Children.Add(new TextBlock { Text = "Turns on automatically when", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                panel.Children.Add(triggerList);
                panel.Children.Add(kindBox);
                var addRow = new Grid { ColumnSpacing = 8 };
                addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                addRow.Children.Add(ssidBox);
                addRow.Children.Add(adapterBox);
                Grid.SetColumn(addButton, 1);
                addRow.Children.Add(addButton);
                panel.Children.Add(addRow);
            }
            else
            {
                panel.Children.Add(new TextBlock { Text = "Default is used whenever no other profile's trigger matches, so it has no triggers.", TextWrapping = TextWrapping.Wrap });
            }
            panel.Children.Add(new TextBlock
            {
                Text = isNew
                    ? "To change its priority, rules, DNS or Wi-Fi pins later, switch to it and use the Networks, Apps and URLs & IPs pages."
                    : "Priority, rules, DNS and Wi-Fi pins are changed on the other pages while this profile is active.",
                TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            panel.Children.Add(error);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = isNew ? "Add profile" : $"Edit {profile!.Name}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            ProfileEdit? result = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var name = nameBox.Text.Trim();
                if (name.Length == 0) { ShowError("Give the profile a name."); args.Cancel = true; return; }
                result = new ProfileEdit(profile?.Id, name, triggers, isNew && (bool)((ComboBoxItem)startBox.SelectedItem).Tag);
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || result is null) return;
            await ViewModel.SaveAsync(result);
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}

internal static class AutomationExtensions
{
    public static T WithName<T>(this T element, string name) where T : DependencyObject
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
        return element;
    }
}
