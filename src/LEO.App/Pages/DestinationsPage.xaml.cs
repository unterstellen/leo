using System.ComponentModel;
using Leo.App.Services;
using Leo.App.ViewModels;
using Leo.Core.SplitTunnel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Leo.App.Pages;

/// <summary>Destination rules, "test a rule" and per-network DNS (M4). Every change comes from a Click.</summary>
public sealed partial class DestinationsPage : Page
{
    // WinUI allows one ContentDialog at a time; a second ShowAsync throws.
    private static bool _dialogOpen;

    private static readonly (DestinationKind Kind, string Text, string Placeholder)[] Kinds =
    [
        (DestinationKind.DomainSuffix, "Website and its subdomains", "youtube.com"),
        (DestinationKind.DomainExact, "Exactly this host name", "www.example.com"),
        (DestinationKind.DomainKeyword, "Host names containing", "googlevideo"),
        (DestinationKind.IpRange, "IP address or range", "203.0.113.0/24"),
        (DestinationKind.AnyHost, "Any destination (ports only)", ""),
    ];

    public DestinationsPage()
    {
        InitializeComponent();
    }

    public DestinationsViewModel ViewModel => App.Current.Destinations;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ErrorBar.IsOpen = ViewModel.ErrorText is not null;
        FillTestApps();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) =>
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DestinationsViewModel.ErrorText)) ErrorBar.IsOpen = ViewModel.ErrorText is not null;
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

    private void Master_Click(object sender, RoutedEventArgs e) => Run("Split tunneling", ViewModel.ToggleMasterAsync);

    // ── Test a rule ──────────────────────────────────────────────────────────

    private void FillTestApps()
    {
        var selected = (TestAppBox.SelectedItem as ComboBoxItem)?.Tag as AppOption;
        TestAppBox.Items.Clear();
        foreach (var app in ViewModel.Apps.Where(a => a.Id is null || a.TestPath is not null))
            TestAppBox.Items.Add(new ComboBoxItem { Content = app.Id is null ? "Any app" : "From " + app.Name, Tag = app });
        TestAppBox.SelectedIndex = Math.Max(0, TestAppBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (i.Tag as AppOption)?.Id == selected?.Id));
    }

    private void Test_Click(object sender, RoutedEventArgs e) => RunTest();

    private void TestBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        RunTest();
    }

    private void RunTest()
    {
        var app = (TestAppBox.SelectedItem as ComboBoxItem)?.Tag as AppOption;
        Run("Test", () => ViewModel.TestAsync(TestBox.Text, app?.TestPath));
    }

    // ── Add / edit ───────────────────────────────────────────────────────────

    private void Add_Click(object sender, RoutedEventArgs e) => Run("Add destination", async () =>
    {
        var target = ViewModel.DefaultTarget();
        if (target is null)
        {
            ViewModel.ErrorText = "No network is available to send traffic through.";
            return;
        }
        await EditRuleAsync(new DestinationRule { TargetAdapterId = target.Id }, isNew: true);
    });

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DestinationCardViewModel card) return;
        Run("Edit rule", () => EditRuleAsync(card.ToRule(), isNew: false));
    }

    private async Task EditRuleAsync(DestinationRule rule, bool isNew)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var kindBox = new ComboBox { Header = "Match", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var k in Kinds) kindBox.Items.Add(new ComboBoxItem { Content = k.Text, Tag = k.Kind });
            kindBox.SelectedIndex = Array.FindIndex(Kinds, k => k.Kind == rule.Kind);

            var valueBox = new TextBox { Header = "Address", Text = rule.Value, MaxLength = 253 };
            var portsBox = new TextBox
            {
                Header = "Ports (optional)", Text = rule.Ports, PlaceholderText = "443 or 80, 443 or 27000-27100", MaxLength = 120,
            };
            var protoBox = new ComboBox { Header = "Protocol", HorizontalAlignment = HorizontalAlignment.Stretch };
            protoBox.Items.Add(new ComboBoxItem { Content = "TCP and UDP", Tag = DestinationProtocol.Any });
            protoBox.Items.Add(new ComboBoxItem { Content = "TCP only", Tag = DestinationProtocol.Tcp });
            protoBox.Items.Add(new ComboBoxItem { Content = "UDP only", Tag = DestinationProtocol.Udp });
            protoBox.SelectedIndex = (int)rule.Protocol;

            var appBox = new ComboBox
            {
                Header = "From", HorizontalAlignment = HorizontalAlignment.Stretch,
                Description = "Optional: only this app's traffic to the destination.",
            };
            foreach (var app in ViewModel.Apps) appBox.Items.Add(new ComboBoxItem { Content = app.Name, Tag = app.Id });
            appBox.SelectedIndex = Math.Max(0, ViewModel.Apps.ToList().FindIndex(a => a.Id == rule.AppRuleId));

            var netBox = new ComboBox { Header = "Network", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var net in ViewModel.Networks)
                netBox.Items.Add(new ComboBoxItem { Content = net.Name + (net.IsPrimary ? " (Primary)" : "") + (net.IsUp ? "" : " (not connected)"), Tag = net.Id });
            netBox.SelectedIndex = Math.Max(0, ViewModel.Networks.ToList().FindIndex(n => n.Id == rule.TargetAdapterId));

            var downBox = new ComboBox { Header = "If that network is unavailable", HorizontalAlignment = HorizontalAlignment.Stretch };
            downBox.Items.Add(new ComboBoxItem { Content = "Use the Primary network", Tag = OnTargetDown.FallbackPrimary });
            downBox.Items.Add(new ComboBoxItem { Content = "Block it", Tag = OnTargetDown.Block });
            downBox.SelectedIndex = (int)rule.OnTargetDown;

            var nameBox = new TextBox { Header = "Name (optional)", Text = rule.DisplayName, MaxLength = 80 };
            var error = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            };

            void KindChanged()
            {
                var k = Kinds[Math.Max(0, kindBox.SelectedIndex)];
                valueBox.PlaceholderText = k.Placeholder;
                valueBox.Visibility = k.Kind == DestinationKind.AnyHost ? Visibility.Collapsed : Visibility.Visible;
                valueBox.Header = k.Kind == DestinationKind.IpRange ? "IP address or range" : k.Kind == DestinationKind.DomainKeyword ? "Text" : "Address";
            }
            kindBox.SelectionChanged += (_, _) => KindChanged(); // form state only; nothing is saved until "Save"
            KindChanged();

            var panel = new StackPanel { Spacing = 12, Width = 440 };
            panel.Children.Add(kindBox);
            panel.Children.Add(valueBox);
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.Children.Add(portsBox);
            Grid.SetColumn(protoBox, 1);
            row.Children.Add(protoBox);
            panel.Children.Add(row);
            panel.Children.Add(appBox);
            panel.Children.Add(netBox);
            panel.Children.Add(downBox);
            panel.Children.Add(nameBox);
            panel.Children.Add(error);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = isNew ? "Add destination" : "Edit destination",
                Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            DestinationRule? result = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var r = new DestinationRule
                {
                    Id = rule.Id,
                    Enabled = rule.Enabled,
                    Kind = (DestinationKind)((ComboBoxItem)kindBox.SelectedItem).Tag,
                    Value = valueBox.Text,
                    Ports = portsBox.Text,
                    Protocol = (DestinationProtocol)((ComboBoxItem)protoBox.SelectedItem).Tag,
                    AppRuleId = (appBox.SelectedItem as ComboBoxItem)?.Tag as string,
                    TargetAdapterId = (netBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
                    OnTargetDown = (OnTargetDown)((ComboBoxItem)downBox.SelectedItem).Tag,
                    DisplayName = nameBox.Text,
                };
                var problem = DestinationMatcher.Normalize(r) ?? (r.TargetAdapterId.Length == 0 ? "Choose a network." : null);
                if (problem is not null)
                {
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true; // keep the dialog open so the user can fix it
                    return;
                }
                result = r;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || result is null) return;
            await ViewModel.SaveAsync(result, isNew ? $"add ({result.Kind})" : $"edit ({result.Kind})");
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ── Card actions ─────────────────────────────────────────────────────────

    private void Target_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not DestinationCardViewModel card) return;
        var menu = new MenuFlyout();
        foreach (var net in ViewModel.Networks)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = net.Name + (net.IsPrimary ? " (Primary)" : "") + (net.IsUp ? "" : " (not connected)"),
                IsChecked = net.Id == card.Info.TargetAdapterId,
            };
            var id = net.Id;
            item.Click += (_, _) =>
            {
                if (id == card.Info.TargetAdapterId) return;
                var rule = card.ToRule();
                rule.TargetAdapterId = id;
                Run("Change network", () => ViewModel.SaveAsync(rule, $"network -> {net.Name}"));
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "No networks", IsEnabled = false });
        menu.ShowAt(el);
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not DestinationCardViewModel card) return;
        var menu = new MenuFlyout();
        void Add(string text, OnTargetDown value)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = card.Info.OnTargetDown == value };
            item.Click += (_, _) =>
            {
                if (card.Info.OnTargetDown == value) return;
                var rule = card.ToRule();
                rule.OnTargetDown = value;
                Run("Change fallback", () => ViewModel.SaveAsync(rule, $"if unavailable -> {value}"));
            };
            menu.Items.Add(item);
        }
        Add("If unavailable: use Primary", OnTargetDown.FallbackPrimary);
        Add("If unavailable: block it", OnTargetDown.Block);
        menu.ShowAt(el);
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not DestinationCardViewModel card) return;
        var rule = card.ToRule();
        rule.Enabled = cb.IsChecked == true;
        Run("Change rule", () => ViewModel.SaveAsync(rule, $"active -> {rule.Enabled}"));
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DestinationCardViewModel card) Run("Move rule", () => ViewModel.MoveAsync(card, up: true));
    }

    private void DownMove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DestinationCardViewModel card) Run("Move rule", () => ViewModel.MoveAsync(card, up: false));
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Run("Remove rule", async () =>
    {
        if ((sender as FrameworkElement)?.Tag is not DestinationCardViewModel card || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Remove {card.Name}?",
                Content = "This destination will follow app rules or your Primary network again.",
                PrimaryButtonText = "Remove",
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

    // ── DNS ──────────────────────────────────────────────────────────────────

    private void Dns_Click(object sender, RoutedEventArgs e) => Run("Change DNS", async () =>
    {
        if ((sender as FrameworkElement)?.Tag is not NetworkDnsCardViewModel card || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var auto = new RadioButton { Content = "Automatic (the network's own DNS" + (card.Info.AutomaticServers.Count > 0 ? ": " + string.Join(", ", card.Info.AutomaticServers) : "") + ")", GroupName = "dns" };
            var custom = new RadioButton { Content = "Custom servers", GroupName = "dns" };
            var box = new TextBox
            {
                Text = string.Join(", ", card.Info.CustomServers),
                PlaceholderText = "1.1.1.1, https://dns.google/dns-query",
                Description = "Up to 4, separated by commas: IP addresses, or https:// (DoH) / tls:// (DoT) addresses.",
                MaxLength = 400,
            };
            (card.Info.CustomServers.Count > 0 ? custom : auto).IsChecked = true;
            var error = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            };
            var panel = new StackPanel { Spacing = 8, Width = 440 };
            panel.Children.Add(auto);
            panel.Children.Add(custom);
            panel.Children.Add(box);
            panel.Children.Add(error);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"DNS for {card.Info.Name}",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            List<string>? servers = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var list = custom.IsChecked == true
                    ? box.Text.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    : [];
                var problem = custom.IsChecked == true && list.Count == 0 ? "Enter at least one server, or choose Automatic."
                    : list.Count > 4 ? "Use up to 4 DNS servers."
                    : list.Select(EnginePlan.ValidateDnsServer).FirstOrDefault(p => p is not null);
                if (problem is not null)
                {
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }
                servers = list;
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || servers is null) return;
            await ViewModel.SetDnsAsync(card.Info.AdapterId, servers);
        }
        finally
        {
            _dialogOpen = false;
        }
    });
}
