namespace Leo.Ipc;

/// <summary>Where the updater is (LEO.md §24.4).</summary>
public enum UpdateState
{
    /// <summary>Development build (not official, not code-signed): updates are off (LEO.md §26).</summary>
    Off = 0,
    Idle = 1,
    Checking = 2,
    UpToDate = 3,
    Available = 4,
    Downloading = 5,
    Verifying = 6,
    Installing = 7,
    Failed = 8,
}

/// <summary>Settings → Updates and About, from the service (protocol 11).</summary>
/// <remarks><see cref="Signed"/>/<see cref="Signer"/> = Authenticode (a CA certificate); an official build without one
/// still updates (State isn't Off), checked with LEO's own update key (LEO.md §26).</remarks>
public sealed record UpdateInfo(
    string InstalledVersion,
    bool Signed,
    string? Signer,
    UpdateState State,
    string StateText,
    string? LatestVersion,
    string? LatestNotesUrl,
    DateTimeOffset? LastCheck,
    bool AutoCheck,
    bool AutoInstall,
    string Channel,
    UpdateResult? LastResult)
{
    public static UpdateInfo Empty { get; } = new("", false, null, UpdateState.Off, "", null, null, null, true, true, "stable", null);
}

/// <summary>How the last update install ended, read once after the service restarted.</summary>
public sealed record UpdateResult(string Version, bool Succeeded, DateTimeOffset At, string? LogPath, string? NotesUrl);
