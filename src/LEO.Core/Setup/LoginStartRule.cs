namespace Leo.Core.Setup;

/// <summary>
/// "Start LEO at login" (LEO.md §7, §24.2, user's decision 2026-11-24). An MSI install writes one machine-wide
/// <c>HKLM\…\Run\LEO = "&lt;install&gt;\LEO.exe" --tray</c>; each user's switch is then a value in their ui.json that
/// makes <c>--tray</c> exit at once. Without that machine entry (dev builds, or an install with START_AT_LOGIN=0)
/// the per-user HKCU Run entry is used as before.
/// </summary>
public static class LoginStartRule
{
    /// <summary>The machine entry is in charge: running from the install folder and the MSI wrote HKLM Run.</summary>
    public static bool UsesMachineEntry(bool installedCopy, bool machineEntryExists) => installedCopy && machineEntryExists;

    /// <summary>What the Settings toggle shows.</summary>
    public static bool IsOn(bool usesMachineEntry, bool? userChoice, bool userEntryPointsHere) =>
        usesMachineEntry ? userChoice != false : userEntryPointsHere;

    /// <summary>A <c>--tray</c> start from the machine entry for a user who turned it off: exit right away.</summary>
    public static bool ExitAtTrayStart(bool usesMachineEntry, bool? userChoice, bool trayStart) =>
        trayStart && usesMachineEntry && userChoice == false;

    /// <summary>
    /// With the machine entry in charge, an old per-user HKCU value (e.g. the dev one pointing at bin\Debug) would start
    /// a second copy at login, so it's removed the first time the installed app runs.
    /// </summary>
    public static bool RemoveUserEntry(bool usesMachineEntry, bool userEntryExists) => usesMachineEntry && userEntryExists;

    /// <summary>A copy inside the MSI's install folder (not the old dev service folder).</summary>
    public static bool IsInstalledCopy(string? exePath, string installDir) =>
        exePath is not null && LeoPaths.IsInside(exePath, installDir) && !LeoPaths.IsInside(exePath, Path.Combine(installDir, "Service"));
}
