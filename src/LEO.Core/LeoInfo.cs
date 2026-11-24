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
    public const string RepositoryUrl = "https://github.com/unterstellen/leo-windows";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string LatestReleaseApi = "https://api.github.com/repos/unterstellen/leo-windows/releases/latest";

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
    public const int ProtocolVersion = 10;

    public static string Version { get; } =
        typeof(LeoInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
