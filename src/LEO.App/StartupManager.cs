using Microsoft.Win32;
using Leo.Core;
using Leo.Core.Setup;

namespace Leo.App;

/// <summary>
/// "Start LEO at login" (LEO.md §7, §24.2). Installed by the MSI: one machine-wide HKLM Run entry, and this user's
/// switch lives in ui.json (an off switch makes <c>--tray</c> exit at once). Dev builds, or an install without the
/// machine entry: a per-user HKCU Run entry as before. This controls the UI only; the service always starts at boot.
/// </summary>
public static class StartupManager
{
    public const string TrayArgument = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string ValueName = LeoInfo.RunValueName;

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    /// <summary>True when the MSI's machine-wide entry decides (installed copy + HKLM Run value present).</summary>
    public static bool UsesMachineEntry => LoginStartRule.UsesMachineEntry(
        LoginStartRule.IsInstalledCopy(Environment.ProcessPath, LeoPaths.InstallDir), MachineEntryExists);

    private static bool MachineEntryExists
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
    }

    private static string? UserEntry
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
    }

    /// <summary>What the Settings toggle shows, given this user's saved choice.</summary>
    public static bool IsEnabled(bool? userChoice) => LoginStartRule.IsOn(UsesMachineEntry, userChoice,
        UserEntry is { } v && v.Equals(Command, StringComparison.OrdinalIgnoreCase));

    /// <summary>A <c>--tray</c> start for a user who turned it off (machine entry only).</summary>
    public static bool ShouldExitAtTrayStart(bool? userChoice, bool trayStart) =>
        LoginStartRule.ExitAtTrayStart(UsesMachineEntry, userChoice, trayStart);

    /// <summary>Removes an old HKCU value once the machine entry is in charge (else LEO would start twice).</summary>
    public static void RemoveStaleUserEntry()
    {
        if (!LoginStartRule.RemoveUserEntry(UsesMachineEntry, UserEntry is not null)) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Applies the switch. With the machine entry the caller's ui.json value is the switch, so only a stale HKCU value is
    /// cleaned up; otherwise the HKCU entry is written or removed.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (UsesMachineEntry)
        {
            RemoveStaleUserEntry();
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
