namespace Leo.Core;

/// <summary>Well-known storage locations (see LEO.md §8).</summary>
public static class LeoPaths
{
    /// <summary>%ProgramData%\UNTERSTELLEN — the publisher folder above <see cref="MachineData"/>.</summary>
    public static string MachinePublisherDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), LeoInfo.Publisher);

    /// <summary>%ProgramData%\UNTERSTELLEN\LEO — machine config, stats, logs (service-owned).</summary>
    public static string MachineData { get; } = Path.Combine(MachinePublisherDir, LeoInfo.ProductName);

    /// <summary>%LocalAppData%\UNTERSTELLEN\LEO — per-user UI preferences.</summary>
    public static string UserData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LeoInfo.Publisher, LeoInfo.ProductName);

    /// <summary>
    /// The MSI's install folder, %ProgramFiles%\UNTERSTELLEN\LEO (LEO.md §24.1): LEO.exe, LEO.Service.exe and
    /// engine\mihomo.exe side by side. Admin-only writable.
    /// </summary>
    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), LeoInfo.Publisher, LeoInfo.ProductName);

    /// <summary>Where <c>tools\dev-service.ps1 deploy</c> put the service before the MSI existed; the MSI removes it.</summary>
    public static string DevServiceDir => Path.Combine(InstallDir, "Service");

    public static string ConfigFile => Path.Combine(MachineData, "config.json");
    public static string StatsDb => Path.Combine(MachineData, "stats.db");
    public static string MachineLogs => Path.Combine(MachineData, "logs");
    public static string UserLogs => Path.Combine(UserData, "logs");

    /// <summary>Engine work folder (config.yaml, cache, rule lists): SYSTEM + Administrators only (LEO.md §24.6).</summary>
    public static string EngineDir => Path.Combine(MachineData, "engine");

    /// <summary>Downloaded update MSIs and their install logs: SYSTEM + Administrators only (LEO.md §24.4).</summary>
    public static string UpdatesDir => Path.Combine(MachineData, "updates");

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/> (full paths, case-insensitive).</summary>
    public static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
