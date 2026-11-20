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

    /// <summary>Public source repository; releases here are LEO's update channel (LEO.md §9).</summary>
    public const string RepositoryUrl = "https://github.com/unterstellen/leo-windows";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string LatestReleaseApi = "https://api.github.com/repos/unterstellen/leo-windows/releases/latest";

    /// <summary>Windows service name (sc.exe / services.msc).</summary>
    public const string ServiceName = "LEO";
    public const string ServiceDisplayName = "LEO Network Engine";
    public const string ServiceDescription =
        "LEO multi-network manager and split-tunneling engine by UNTERSTELLEN.";

    /// <summary>Named pipe the service listens on (\.\pipe\LEO.Control).</summary>
    public const string ControlPipeName = "LEO.Control";

    /// <summary>IPC protocol version; bump on breaking contract changes.</summary>
    public const int ProtocolVersion = 2;

    public static string Version { get; } =
        typeof(LeoInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
