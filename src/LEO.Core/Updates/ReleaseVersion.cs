using System.Globalization;
using System.Text.RegularExpressions;

namespace Leo.Core.Updates;

/// <summary>
/// A LEO release version (LEO.md §24.2): <c>MAJOR.MINOR.PATCH</c>, tags <c>v0.2.0</c>, canary tags
/// <c>v0.2.1-canary.1</c>. Windows Installer compares only three fields (major ≤ 255, minor ≤ 255, build ≤ 65535),
/// so every tag's x.y.z must be higher than every earlier tag's; the suffix only orders builds of one x.y.z.
/// </summary>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string? Channel = null, int ChannelNumber = 0)
    : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^v?(\d{1,5})\.(\d{1,5})\.(\d{1,5})(?:-([a-z]+)\.(\d{1,5}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public bool IsPrerelease => Channel is not null;

    /// <summary>The three fields Windows Installer compares.</summary>
    public string Core => $"{Major}.{Minor}.{Patch}";

    public string Tag => IsPrerelease ? $"v{Core}-{Channel}.{ChannelNumber}" : $"v{Core}";

    public override string ToString() => Tag[1..];

    /// <summary>Fits Windows Installer's ProductVersion limits.</summary>
    public bool FitsMsi => Major <= 255 && Minor <= 255 && Patch <= 65535;

    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern().Match(text.Trim().Split('+')[0]);
        if (!m.Success) return null;
        static int N(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);
        return new ReleaseVersion(N(m.Groups[1]), N(m.Groups[2]), N(m.Groups[3]),
            m.Groups[4].Success ? m.Groups[4].Value.ToLowerInvariant() : null,
            m.Groups[5].Success ? N(m.Groups[5]) : 0);
    }

    /// <summary>x.y.z first; for the same x.y.z a pre-release sorts before the release.</summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (IsPrerelease != other.IsPrerelease) return IsPrerelease ? -1 : 1;
        c = string.CompareOrdinal(Channel, other.Channel);
        return c != 0 ? c : ChannelNumber.CompareTo(other.ChannelNumber);
    }

    /// <summary>
    /// What Windows Installer would do: an MSI only upgrades when its three fields are higher. Used by the updater
    /// so it never downloads something the MSI would refuse or treat as the same product version.
    /// </summary>
    public bool UpgradesOver(ReleaseVersion installed) =>
        (Major, Minor, Patch).CompareTo((installed.Major, installed.Minor, installed.Patch)) > 0;

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
}

/// <summary>The release workflow's tag check (LEO.md §24.2, §24.4 step 1).</summary>
public static class TagRules
{
    /// <summary>Null when <paramref name="tag"/> may be released after <paramref name="earlierTags"/>, else why not.</summary>
    public static string? Check(string tag, IEnumerable<string> earlierTags)
    {
        var v = ReleaseVersion.TryParse(tag);
        if (v is null || !tag.StartsWith('v')) return $"'{tag}' isn't a release tag (vX.Y.Z or vX.Y.Z-canary.N).";
        if (v.IsPrerelease && v.Channel != "canary") return $"'{tag}': the only pre-release channel is canary.";
        if (!v.FitsMsi) return $"'{tag}': Windows Installer allows major and minor up to 255 and patch up to 65535.";
        foreach (var e in earlierTags.Select(ReleaseVersion.TryParse).OfType<ReleaseVersion>())
        {
            if (!v.UpgradesOver(e)) return $"'{tag}': x.y.z must be higher than every earlier tag's ({e.Tag} exists).";
        }
        return null;
    }
}
