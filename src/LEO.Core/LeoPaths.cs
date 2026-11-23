namespace Leo.Core;

/// <summary>Well-known storage locations (see LEO.md §8).</summary>
public static class LeoPaths
{
    /// <summary>%ProgramData%\UNTERSTELLEN\LEO — machine config, stats, logs (service-owned).</summary>
    public static string MachineData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        LeoInfo.Publisher, LeoInfo.ProductName);

    /// <summary>%LocalAppData%\UNTERSTELLEN\LEO — per-user UI preferences.</summary>
    public static string UserData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LeoInfo.Publisher, LeoInfo.ProductName);

    public static string ConfigFile => Path.Combine(MachineData, "config.json");
    public static string StatsDb => Path.Combine(MachineData, "stats.db");
    public static string MachineLogs => Path.Combine(MachineData, "logs");
    public static string UserLogs => Path.Combine(UserData, "logs");
}
