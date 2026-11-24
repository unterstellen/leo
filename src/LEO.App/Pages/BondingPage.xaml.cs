using System.ComponentModel;
using Leo.App.Services;
using Leo.App.ViewModels;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace Leo.App.Pages;

/// <summary>Bonding (M7, LEO.md §22): bond groups and "Everything else uses". Every change comes from a Click.</summary>
public sealed partial class BondingPage : Page
{
    // WinUI allows one ContentDialog at a time; a second ShowAsync throws.
    private static bool _dialogOpen;

    private static readonly BondMode[] Modes = [BondMode.BySite, BondMode.Weighted, BondMode.EveryConnection, BondMode.Fastest];

    public BondingPage()
    {
        InitializeComponent();
    }

    public BondingViewModel ViewModel => App.Current.Bonding;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ErrorBar.IsOpen = ViewModel.ErrorText is not null;
        ViewModel.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BondingViewModel.ErrorText)) ErrorBar.IsOpen = ViewModel.ErrorText is not null;
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

    // ── Everything else uses ─────────────────────────────────────────────────

    private void Unruled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        var menu = new MenuFlyout();
        void Add(string text, string? bondId)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = ViewModel.UnruledBondId == bondId };
            item.Click += (_, _) => Run("Everything else", () => ViewModel.SetUnruledAsync(bondId));
            menu.Items.Add(item);
        }
        Add("Primary network", null);
        foreach (var b in ViewModel.BondInfos) Add($"Bond '{b.Name}'", b.Id);
        menu.ShowAt(el, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    // ── Cards ────────────────────────────────────────────────────────────────

    private void Add_Click(object sender, RoutedEventArgs e) => Run("New bond", () => EditAsync(null));

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BondCardViewModel card) Run("Edit bond", () => EditAsync(card.Info));
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BondCardViewModel card) Run("Move bond", () => ViewModel.MoveAsync(card, up: true));
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BondCardViewModel card) Run("Move bond", () => ViewModel.MoveAsync(card, up: false));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BondCardViewModel card) Run("Delete bond", () => DeleteAsync(card));
    }

    private async Task DeleteAsync(BondCardViewModel card)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var uses = card.Info.RuleCount > 0
                ? $" {card.Info.RuleCount} rule(s) use it; they stop working until you pick another network for them."
                : "";
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Delete bond '{card.Info.Name}'?",
                Content = new TextBlock { Text = "The networks themselves aren't changed." + uses, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.DeleteAsync(card);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>New bond (info null) or edit. Saved through the service while the dialog is open, so problems show in it.</summary>
    private async Task EditAsync(BondInfo? info)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var nameBox = new TextBox { Header = "Name", Text = info?.Name ?? "", PlaceholderText = "Fast downloads", MaxLength = 40 };

            var modeBox = new ComboBox { Header = "How connections are spread", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var m in Modes) modeBox.Items.Add(new ComboBoxItem { Content = BondRules.ModeName(m), Tag = m });
            modeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Modes, info?.Mode ?? BondMode.BySite));
            var modeHelp = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };

            // Members: every physical network, plus members of this bond that aren't plugged in right now.
            var rows = new List<(string AdapterId, CheckBox Box, NumberBox Weight)>();
            var membersPanel = new StackPanel { Spacing = 6 };
            membersPanel.Children.Add(new TextBlock { Text = "Networks (two or more)" });
            var candidates = ViewModel.Candidates.Select(a => (a.Id, Name: a.DisplayName + (a.Status == AdapterStatus.Up ? "" : " (not connected)")))
                .ToList();
            foreach (var m in info?.Members ?? [])
                if (candidates.All(c => !c.Id.Equals(m.AdapterId, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add((m.AdapterId, m.Name + " (not plugged in)"));
            foreach (var (id, name) in candidates)
            {
                var member = info?.Members.FirstOrDefault(m => m.AdapterId.Equals(id, StringComparison.OrdinalIgnoreCase));
                var box = new CheckBox { Content = name, IsChecked = member is not null, MinWidth = 0 };
                AutomationPropertiesHelper(box, $"Use {name} in the bond");
                var weight = new NumberBox
                {
                    Minimum = 1, Maximum = BondRules.MaxWeight, Value = member?.Weight ?? 1, SmallChange = 1, Width = 120,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Header = "Weight",
                };
                AutomationPropertiesHelper(weight, $"Weight of {name}");
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(box);
                Grid.SetColumn(weight, 1);
                row.Children.Add(weight);
                membersPanel.Children.Add(row);
                rows.Add((id, box, weight));
            }

            var downBox = new ComboBox { Header = "If no network in the bond works", HorizontalAlignment = HorizontalAlignment.Stretch };
            downBox.Items.Add(new ComboBoxItem { Content = "Use the Primary network", Tag = OnTargetDown.FallbackPrimary });
            downBox.Items.Add(new ComboBoxItem { Content = "Block it", Tag = OnTargetDown.Block });
            downBox.SelectedIndex = (int)(info?.OnAllDown ?? OnTargetDown.FallbackPrimary);

            var error = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            };

            void ModeChanged()
            {
                var mode = Modes[Math.Max(0, modeBox.SelectedIndex)];
                modeHelp.Text = char.ToUpperInvariant(BondCardViewModel.ModeExplanation(mode)[0]) + BondCardViewModel.ModeExplanation(mode)[1..] + ".";
                foreach (var r in rows) r.Weight.Visibility = mode is BondMode.Weighted or BondMode.EveryConnection ? Visibility.Visible : Visibility.Collapsed;
            }
            modeBox.SelectionChanged += (_, _) => ModeChanged(); // form state only; nothing is saved until "Save"
            ModeChanged();

            var panel = new StackPanel { Spacing = 12, Width = 440 };
            panel.Children.Add(nameBox);
            panel.Children.Add(modeBox);
            panel.Children.Add(modeHelp);
            panel.Children.Add(membersPanel);
            panel.Children.Add(downBox);
            panel.Children.Add(new TextBlock
            {
                Text = "One single connection always uses one network: bonding speeds up many connections at once, not one big download over one connection.",
                TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            panel.Children.Add(error);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = info is null ? "New bond" : "Edit bond",
                Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    var bond = new Bond
                    {
                        Id = info?.Id ?? "",
                        Name = nameBox.Text,
                        Mode = Modes[Math.Max(0, modeBox.SelectedIndex)],
                        OnAllDown = (OnTargetDown)((ComboBoxItem)downBox.SelectedItem).Tag,
                        Members = rows.Where(r => r.Box.IsChecked == true).Select(r => new BondMember
                        {
                            AdapterId = r.AdapterId,
                            Weight = double.IsNaN(r.Weight.Value) ? 1 : (int)Math.Round(r.Weight.Value),
                        }).ToList(),
                    };
                    var problem = await ViewModel.SaveAsync(bond);
                    if (problem is not null)
                    {
                        error.Text = problem;
                        error.Visibility = Visibility.Visible;
                        args.Cancel = true; // keep the dialog open so the user can fix it
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static void AutomationPropertiesHelper(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
