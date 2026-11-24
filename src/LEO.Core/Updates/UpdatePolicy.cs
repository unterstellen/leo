namespace Leo.Core.Updates;

public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Canary = "canary";

    public static string Normalize(string? channel) =>
        string.Equals(channel, Canary, StringComparison.OrdinalIgnoreCase) ? Canary : Stable;
}

/// <summary>Settings → Updates (LEO.md §24.4). Silent automatic updates are the default (user's decision, 2026-11-24).</summary>
public sealed class UpdateSettings
{
    /// <summary>Check GitHub once a day.</summary>
    public bool AutoCheck { get; set; } = true;

    /// <summary>Install a verified update without asking; off = a toast with "Update now" instead.</summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>"stable" (GitHub's latest release) or "canary" (pre-releases too, from releases.atom).</summary>
    public string Channel { get; set; } = UpdateChannels.Stable;
}

/// <summary>What the updater does with a version it found.</summary>
public enum UpdateAction
{
    Nothing,
    /// <summary>Tell the user (toast "Update now" / Settings).</summary>
    Offer,
    /// <summary>Download, verify and install on its own.</summary>
    Install,
}

/// <summary>
/// The updater's decisions, pure so they're unit-tested (LEO.md §24.4): when to check, which version counts, whether
/// to install on its own. Dev builds (not official, not signed) never update themselves.
/// </summary>
public static class UpdatePolicy
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    public static readonly TimeSpan MaxRandomOffset = TimeSpan.FromHours(2);

    /// <summary>
    /// When the next automatic check is due: a few minutes after the service starts (unless the last check was less
    /// than a day ago), then once a day plus a random offset so PCs don't all ask at the same minute.
    /// </summary>
    public static DateTimeOffset NextCheck(DateTimeOffset serviceStart, DateTimeOffset? lastCheck, TimeSpan randomOffset)
    {
        var first = serviceStart + FirstCheckDelay;
        if (lastCheck is null) return first;
        var daily = lastCheck.Value + Interval + randomOffset;
        return daily > first ? daily : first;
    }

    /// <summary>
    /// The newest version the channel offers that Windows Installer would accept as an upgrade, or null.
    /// Stable only knows the <c>releases/latest</c> target; canary also takes pre-releases from the feed.
    /// </summary>
    public static ReleaseVersion? Pick(ReleaseVersion installed, string channel, ReleaseVersion? latestStable, IEnumerable<ReleaseVersion> feed)
    {
        var candidates = new List<ReleaseVersion>();
        if (latestStable is not null) candidates.Add(latestStable);
        if (UpdateChannels.Normalize(channel) == UpdateChannels.Canary) candidates.AddRange(feed);
        return candidates.Where(v => v.FitsMsi && v.UpgradesOver(installed)).OrderDescending().FirstOrDefault();
    }

    /// <param name="updatesOn">Official build or code-signed service (LEO.md §26); dev builds never update.</param>
    public static UpdateAction Decide(UpdateSettings settings, bool updatesOn, ReleaseVersion? found)
    {
        if (!updatesOn || found is null) return UpdateAction.Nothing;
        return settings.AutoInstall ? UpdateAction.Install : UpdateAction.Offer;
    }

    /// <summary>
    /// "Update now / Later": the UI doesn't toast the same version again on the day the user said Later.
    /// </summary>
    public static bool ShouldToast(string? foundVersion, string? postponedVersion, DateOnly? postponedOn, DateOnly today) =>
        foundVersion is not null && !(foundVersion == postponedVersion && postponedOn == today);
}
