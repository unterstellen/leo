using Leo.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Leo.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        Advanced = new AdvancedSettingsViewModel(App.Current.Connection);
        InitializeComponent();
    }

    public MainViewModel ViewModel => App.Current.ViewModel;

    public NetworksViewModel Networks => App.Current.Networks;

    public AdvancedSettingsViewModel Advanced { get; }

    public UpdatesViewModel Updates => App.Current.Updates;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try { await Advanced.LoadAsync(); }
        catch (Exception ex) { Services.UiLog.Write("Loading advanced settings failed", ex); }
    }

    private async void SaveAdvanced_Click(object sender, RoutedEventArgs e)
    {
        try { await Advanced.SaveAsync(); }
        catch (Exception ex) { Advanced.ErrorText = ex.Message; }
    }

    private void ResetAdvanced_Click(object sender, RoutedEventArgs e) => Advanced.ResetToDefaults();

    // ── Updates (M8) ─────────────────────────────────────────────────────────

    /// <summary>
    /// A switch the user flipped. The switches show the service's settings one-way, and a refresh also raises Toggled:
    /// while two values change, one switch already shows the new value and the other still the old one. So only the
    /// switch that fired counts, compared with that one setting; a refresh sets it to the value the service already
    /// has and is ignored (found live in M8: a refresh had saved "check automatically" as off).
    /// </summary>
    private async void UpdateSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch sw) return;
        bool autoCheck = Updates.AutoCheck, autoInstall = Updates.AutoInstall, canary = Updates.Canary;
        if (ReferenceEquals(sw, AutoCheckSwitch)) { if (sw.IsOn == autoCheck) return; autoCheck = sw.IsOn; }
        else if (ReferenceEquals(sw, AutoInstallSwitch)) { if (sw.IsOn == autoInstall) return; autoInstall = sw.IsOn; }
        else if (ReferenceEquals(sw, CanarySwitch)) { if (sw.IsOn == canary) return; canary = sw.IsOn; }
        else return;
        if (!Updates.CanEdit) return; // never save from a disabled switch
        try { await Updates.SaveSettingsAsync(autoCheck, autoInstall, canary); }
        catch (Exception ex) { Updates.ErrorText = ex.Message; }
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        try { await Updates.CheckNowAsync(); }
        catch (Exception ex) { Updates.ErrorText = ex.Message; }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        try { await Updates.UpdateNowAsync(); }
        catch (Exception ex) { Updates.ErrorText = ex.Message; }
    }

    private async void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Updates.NotesUrl is { } url) await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    // ── About (M8) ───────────────────────────────────────────────────────────

    private async void OpenSource_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(Updates.SourceUrl));

    private async void OpenNotices_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(Updates.NoticesUrl));

    private async void OpenPrivacy_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(Updates.PrivacyUrl));

    /// <summary>Puts the diagnostics report on the clipboard; LEO sends nothing (LEO.md §24.7).</summary>
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Updates.BuildDiagnostics(IncludePersonalCheck.IsChecked == true);
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            DiagnosticsStatus.Text = "Copied. Paste it into a GitHub issue.";
        }
        catch (Exception ex)
        {
            DiagnosticsStatus.Text = "Couldn't copy: " + ex.Message;
        }
    }

    /// <summary>Opens Windows' Installed apps page, where LEO is removed like any other app (LEO.md §24.5).</summary>
    private async void Uninstall_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:appsfeatures"));
}
