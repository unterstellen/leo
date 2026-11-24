using System.Xml.Linq;

namespace Leo.Core.Updates;

/// <summary>
/// Where LEO finds releases without GitHub's REST API (LEO.md §24.4; the anonymous API was rate-limited, §17):
/// <list type="bullet">
/// <item>Stable: <c>github.com/&lt;repo&gt;/releases/latest</c>, read without following the redirect: the Location
/// header ends in <c>/releases/tag/vX.Y.Z</c>. GitHub skips pre-releases here.</item>
/// <item>Canary: the public Atom feed <c>releases.atom</c>, which lists pre-releases too.</item>
/// <item>Files: <c>releases/download/&lt;tag&gt;/LEO-&lt;x.y.z&gt;-x64.msi</c> and <c>SHA256SUMS.txt</c>, fixed names.</item>
/// </list>
/// </summary>
public static class ReleaseFeed
{
    public static string LatestUrl => LeoInfo.ReleasesUrl + "/latest";
    public static string AtomUrl => LeoInfo.ReleasesUrl + ".atom";

    public static string MsiName(ReleaseVersion v) => $"LEO-{v.Core}-x64.msi";
    public static string MsiUrl(ReleaseVersion v) => $"{LeoInfo.ReleasesUrl}/download/{v.Tag}/{MsiName(v)}";
    public static string SumsUrl(ReleaseVersion v) => $"{LeoInfo.ReleasesUrl}/download/{v.Tag}/SHA256SUMS.txt";
    public static string NotesUrl(ReleaseVersion v) => $"{LeoInfo.ReleasesUrl}/tag/{v.Tag}";

    /// <summary>The version from the <c>releases/latest</c> redirect target; null for anything else.</summary>
    public static ReleaseVersion? FromLatestRedirect(Uri? location)
    {
        if (location is null) return null;
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
        const string marker = "/releases/tag/";
        var i = path.LastIndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var v = ReleaseVersion.TryParse(Uri.UnescapeDataString(path[(i + marker.Length)..].TrimEnd('/')));
        return v is { IsPrerelease: false } ? v : null;
    }

    /// <summary>Every release version named in the Atom feed (entry links end in <c>/releases/tag/&lt;tag&gt;</c>).</summary>
    public static IReadOnlyList<ReleaseVersion> FromAtom(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            return doc.Root?.Elements(atom + "entry")
                       .Select(e => e.Elements(atom + "link").Select(l => (string?)l.Attribute("href")).FirstOrDefault(h => h is not null))
                       .Select(h => h is null ? null : FromTagLink(h))
                       .OfType<ReleaseVersion>()
                       .Distinct()
                       .ToList()
                   ?? [];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static ReleaseVersion? FromTagLink(string href)
    {
        const string marker = "/releases/tag/";
        var i = href.LastIndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? null : ReleaseVersion.TryParse(Uri.UnescapeDataString(href[(i + marker.Length)..].TrimEnd('/')));
    }

    /// <summary>The SHA-256 for <paramref name="fileName"/> in a SHA256SUMS.txt ("hash  name" lines).</summary>
    public static string? ShaFor(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space != 64) continue;
            var name = line[space..].Trim().TrimStart('*');
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return line[..64].ToLowerInvariant();
        }
        return null;
    }
}
