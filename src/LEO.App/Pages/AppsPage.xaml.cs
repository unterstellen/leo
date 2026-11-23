using System.ComponentModel;
using Leo.App.Controls;
using Leo.App.Services;
using Leo.App.ViewModels;
using Leo.Core.SplitTunnel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Leo.App.Pages;

/// <summary>Split tunneling by app. Every change comes from a Click; nothing is written from bindings.</summary>
public sealed partial class AppsPage : Page
{
    // WinUI allows one ContentDialog at a time; a second ShowAsync throws.
    private static bool _dialogOpen;

    public AppsPage()
    {
        InitializeComponent();
    }

    public AppsViewModel ViewModel => App.Current.Apps;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ErrorBar.IsOpen = ViewModel.ErrorText is not null;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) =>
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppsViewModel.ErrorText)) ErrorBar.IsOpen = ViewModel.ErrorText is not null;
    }

    private void ErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissError();

    private void Fail(string what, Exception ex)
    {
        UiLog.Write($"{what} failed", ex);
        ViewModel.ErrorText = $"{what}: {ex.Message}";
    }

    private async void Run(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Fail(what, ex); }
    }

    // ── Master switch ────────────────────────────────────────────────────────

    private void Master_Click(object sender, RoutedEventArgs e) => Run("Split tunneling", ViewModel.ToggleMasterAsync);

    // ── Add ──────────────────────────────────────────────────────────────────

    private void AddRunning_Click(object sender, RoutedEventArgs e) =>
        Run("Add app", () => PickFromListAsync("Add a running app", () => Task.Run(AppCatalog.RunningApps)));

    private void AddInstalled_Click(object sender, RoutedEventArgs e) =>
        Run("Add app", () => PickFromListAsync("Add an installed app", AppCatalog.InstalledAppsAsync));

    // M6: installed VPN clients (WireGuard, OpenVPN…): an app rule picks the network that carries the VPN tunnel.
    private void AddVpnClient_Click(object sender, RoutedEventArgs e) =>
        Run("Add VPN client", () => PickFromListAsync("Add a VPN client (choose the network that carries its tunnel)", ViewModel.VpnClientsAsync));

    private async Task PickFromListAsync(string title, Func<Task<List<AppCandidate>>> load)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var panel = new AppPickerPanel();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = panel,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 640.0;
            _ = LoadIntoAsync(panel, load);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || panel.Selected is not { } app) return;
            await ViewModel.AddAsync(app);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task LoadIntoAsync(AppPickerPanel panel, Func<Task<List<AppCandidate>>> load)
    {
        try
        {
            panel.SetItems(await load());
        }
        catch (Exception ex)
        {
            UiLog.Write("App list failed", ex);
            panel.SetItems([]);
        }
    }

    private void AddBrowse_Click(object sender, RoutedEventArgs e) => Run("Add app", async () =>
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await ViewModel.AddAsync(new AppCandidate(AppCatalog.FriendlyName(file.Path), file.Path, AppMatchKind.ExactPath, ""));
    });

    private void AddFolder_Click(object sender, RoutedEventArgs e) => Run("Add folder", async () =>
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await ViewModel.AddAsync(new AppCandidate($"Everything in {folder.Name}", folder.Path, AppMatchKind.Folder, ""));
    });

    private void AddName_Click(object sender, RoutedEventArgs e) => Run("Add programs by name", async () =>
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var box = new TextBox
            {
                Header = "Program file name",
                PlaceholderText = "game*.exe",
                Description = "Use * for any characters. Matches programs with this name in any folder.",
                MaxLength = 120,
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Add programs by name",
                Content = box,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var pattern = box.Text.Trim();
            if (pattern.Length == 0) return;
            if (pattern.IndexOfAny(['\\', '/', ':']) >= 0)
            {
                ViewModel.ErrorText = "Enter just a file name, like game.exe or game*.exe.";
                return;
            }
            await ViewModel.AddAsync(new AppCandidate($"Programs named {pattern}", pattern, AppMatchKind.NamePattern, ""));
        }
        finally
        {
            _dialogOpen = false;
        }
    });

    // ── Card actions ─────────────────────────────────────────────────────────

    private void Target_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not AppRuleCardViewModel card) return;
        var menu = new MenuFlyout();
        foreach (var net in ViewModel.Networks)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = net.Label,
                IsChecked = net.Id == card.Info.TargetAdapterId,
            };
            var id = net.Id;
            item.Click += (_, _) =>
            {
                if (id == card.Info.TargetAdapterId) return;
                var rule = card.ToRule();
                // Moving a rule onto a VPN: block when the VPN is down, as for new VPN rules (LEO.md §20.2).
                var wasVpn = ViewModel.Networks.FirstOrDefault(n => n.Id == rule.TargetAdapterId)?.IsVpn == true;
                if (net.IsVpn && !wasVpn) rule.OnTargetDown = OnTargetDown.Block;
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
        if (sender is not FrameworkElement el || el.Tag is not AppRuleCardViewModel card) return;
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
        Add("If unavailable: block the app", OnTargetDown.Block);
        menu.ShowAt(el);
    }

    private void Children_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not AppRuleCardViewModel card) return;
        var rule = card.ToRule();
        rule.IncludeChildren = cb.IsChecked == true;
        Run("Change rule", () => ViewModel.SaveAsync(rule, $"include children -> {rule.IncludeChildren}"));
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not AppRuleCardViewModel card) return;
        var rule = card.ToRule();
        rule.Enabled = cb.IsChecked == true;
        Run("Change rule", () => ViewModel.SaveAsync(rule, $"active -> {rule.Enabled}"));
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Run("Remove rule", async () =>
    {
        if ((sender as FrameworkElement)?.Tag is not AppRuleCardViewModel card || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Remove {card.Name}?",
                Content = "This app will use your Primary network again.",
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
}
