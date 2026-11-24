using CommunityToolkit.Mvvm.ComponentModel;
using Leo.App.Services;
using Leo.Core;
using Leo.Ipc;
using Microsoft.UI.Dispatching;

namespace Leo.App.ViewModels;

/// <summary>
/// Settings → Updates and About (LEO.md §24.4, §24.7, §24.8). Every action comes from a Click/Toggled handler on the
/// page; while one runs its buttons are disabled (no hidden time limits).
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly DispatcherQueue _dispatcher;

    public UpdatesViewModel(ServiceConnection connection, DispatcherQueue dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _connection.UpdateChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        _connection.Changed += (_, _) => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    public UpdateInfo Info => _connection.Update;

    public string VersionText => LeoInfo.DisplayVersion(LeoInfo.Version);
    public string SourceUrl => LeoInfo.SourceUrl(LeoInfo.Version);
    public string NoticesUrl => LeoInfo.FileUrl(LeoInfo.Version, "THIRD-PARTY-NOTICES.md");
    public string PrivacyUrl => LeoInfo.FileUrl(LeoInfo.Version, "PRIVACY.md");

    [ObservableProperty] public partial string SignedText { get; set; } = "";
    [ObservableProperty] public partial string StateText { get; set; } = "";
    [ObservableProperty] public partial string LastCheckText { get; set; } = "";
    [ObservableProperty] public partial bool AutoCheck { get; set; }
    [ObservableProperty] public partial bool AutoInstall { get; set; }
    [ObservableProperty] public partial bool Canary { get; set; }
    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool CanCheck { get; set; }
    [ObservableProperty] public partial bool ShowUpdateNow { get; set; }
    [ObservableProperty] public partial bool CanUpdateNow { get; set; }
    [ObservableProperty] public partial string? AdminHint { get; set; }
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial string? NotesUrl { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasAdminHint { get; set; }
    [ObservableProperty] public partial bool HasNotes { get; set; }

    private void Refresh()
    {
        var u = _connection.Update;
        var connected = _connection.State == ConnectionState.Connected;
        var full = _connection.Access == ClientAccess.Full;
        var running = u.State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Verifying or UpdateState.Installing;

        SignedText = !connected ? "LEO's service isn't connected."
            : u.Signed ? $"Signed by {u.Signer}" : "Development build (not signed): updates are off.";
        StateText = connected ? u.StateText : "";
        LastCheckText = u.LastCheck is { } t ? $"Last checked {t.LocalDateTime:g}" : "";
        AutoCheck = u.AutoCheck;
        AutoInstall = u.AutoInstall;
        Canary = u.Channel == "canary";
        CanEdit = connected && full && u.Signed && !IsBusy;
        CanCheck = connected && u.Signed && !running && !IsBusy;
        ShowUpdateNow = connected && u.Signed && u.LatestVersion is not null && u.State is UpdateState.Available or UpdateState.Failed;
        CanUpdateNow = ShowUpdateNow && full && !running && !IsBusy;
        AdminHint = ShowUpdateNow && !full ? "Ask an administrator to update LEO." : null;
        HasAdminHint = AdminHint is not null;
        NotesUrl = u.LatestNotesUrl;
        HasNotes = NotesUrl is not null;
    }

    public async Task CheckNowAsync()
    {
        await RunAsync(async () =>
        {
            var (_, error) = await _connection.QueryAsync(c => c.CheckForUpdatesAsync());
            ErrorText = error?.Message;
        });
    }

    public async Task UpdateNowAsync() =>
        await RunAsync(async () => ErrorText = await _connection.TryInvokeAsync(c => c.InstallUpdateAsync()));

    public async Task SaveSettingsAsync(bool autoCheck, bool autoInstall, bool canary) =>
        await RunAsync(async () => ErrorText = await _connection.TryInvokeAsync(
            c => c.SetUpdateSettingsAsync(autoCheck, autoInstall, canary ? "canary" : "stable")));

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Refresh();
        try { await action(); }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>"Copy diagnostics" text (LEO.md §24.7): built locally, nothing is sent anywhere.</summary>
    public string BuildDiagnostics(bool includePersonal)
    {
        var u = _connection.Update;
        return DiagnosticsReport.Build(new DiagnosticsReport.Input(
            LeoInfo.Version,
            _connection.Status?.ServiceVersion,
            u.Signed,
            u.Signer,
            $"{Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})",
            _connection.Networks,
            _connection.SplitTunnel,
            ReadServiceLog(),
            includePersonal));
    }

    /// <summary>The newest service log (readable by every user on purpose, LEO.md §24.6 item 8), last lines only.</summary>
    private static IReadOnlyList<string> ReadServiceLog()
    {
        try
        {
            var newest = new DirectoryInfo(LeoPaths.MachineLogs).EnumerateFiles("service-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return [];
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > 2000) lines.Dequeue();
            }
            return [.. lines];
        }
        catch
        {
            return [];
        }
    }
}
