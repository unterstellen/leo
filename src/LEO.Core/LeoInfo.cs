using System.Reflection;

namespace Leo.Core;

/// <summary>Product identity shared by every LEO component.</summary>
public static class LeoInfo
{
    public const string ProductName = "LEO";
    public const string Publisher = "UNTERSTELLEN";
    public const string PublisherUrl = "https://unterstellen.org";
    public const string Company = "INFERSTELLEN";
    public const string CompanyUrl = "https://inferstellen.com";

    /// <summary>LEO is free software: GNU GPL v3 or (at your option) any later version. See LICENSE.</summary>
    public const string License = "GPL-3.0-or-later";
    public const string LicenseUrl = "https://www.gnu.org/licenses/gpl-3.0.html";

    /// <summary>Public source repository; releases here are LEO's update channel (LEO.md §9).</summary>
    public const string RepositoryUrl = "https://github.com/unterstellen/leo";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    // No api.github.com: the updater reads the releases/latest redirect and releases.atom (Updates/ReleaseFeed, §24.4).

    /// <summary>Windows service name (sc.exe / services.msc).</summary>
    public const string ServiceName = "LEO";
    public const string ServiceDisplayName = "LEO Network Engine";
    public const string ServiceDescription =
        "LEO multi-network manager and split-tunneling engine by UNTERSTELLEN.";

    /// <summary>
    /// AppUserModelID of the unpackaged app: set by LEO.exe at start and on the MSI's Start menu shortcut, so toasts
    /// and the taskbar group belong to LEO (LEO.md §24.2).
    /// </summary>
    public const string AppUserModelId = "UNTERSTELLEN.LEO";

    /// <summary>The MSI's UpgradeCode, fixed forever (installer\Package.wxs); the updater checks it (LEO.md §24.4).</summary>
    public const string UpgradeCode = "{C17E6E01-40E8-4381-AF33-BCA6DCF81643}";

    /// <summary>HKLM\SOFTWARE\UNTERSTELLEN\LEO: machine-wide values set by an administrator.</summary>
    public const string MachineRegistryKey = @"SOFTWARE\UNTERSTELLEN\LEO";

    /// <summary>
    /// DWORD under <see cref="MachineRegistryKey"/>: 1 turns the pipe-client signature check off on a signed install
    /// (developers, opt-in live tests; LEO.md §24.6).
    /// </summary>
    public const string AllowUnsignedClientsValue = "AllowUnsignedClients";

    /// <summary>HKLM/HKCU Run value name for "Start LEO at login" (the MSI writes the HKLM one, LEO.md §24.2).</summary>
    public const string RunValueName = "LEO";

    /// <summary>Named pipe the service listens on (\.\pipe\LEO.Control).</summary>
    public const string ControlPipeName = "LEO.Control";

    /// <summary>IPC protocol version; bump on breaking contract changes.</summary>
    public const int ProtocolVersion = 11; // 11 = M8 updater

    public static string Version { get; } =
        typeof(LeoInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// True only in builds made by the release workflow (it passes <c>-p:LeoOfficialBuild=true</c>, which
    /// Directory.Build.props turns into assembly metadata). Official builds update themselves with LEO's own update key
    /// even without a code-signing certificate (LEO.md §26); local and Rider builds never do.
    /// </summary>
    public static bool IsOfficialBuild { get; } =
        typeof(LeoInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "LeoOfficialBuild" && a.Value == "true");

    /// <summary>
    /// LEO stays "Preview" until 1.0 (user's decision, LEO.md §24.7): every 0.x release is a normal GitHub release
    /// titled "LEO 0.x.y (Preview)", and the app says so.
    /// </summary>
    public static bool IsPreviewVersion(string version) => version.TrimStart('v').StartsWith("0.", StringComparison.Ordinal);

    public static bool IsPreview => IsPreviewVersion(Version);

    /// <summary>"LEO 0.1.0 (Preview)" / "LEO 1.2.0".</summary>
    public static string DisplayVersion(string version) => $"{ProductName} {version}{(IsPreviewVersion(version) ? " (Preview)" : "")}";

    /// <summary>This version's source code (GPL: recipients can find the source of what they run).</summary>
    public static string SourceUrl(string version) => $"{RepositoryUrl}/tree/v{version}";

    /// <summary>A file in the repository at this version's tag (privacy statement, third-party notices).</summary>
    public static string FileUrl(string version, string file) => $"{RepositoryUrl}/blob/v{version}/{file}";
}
